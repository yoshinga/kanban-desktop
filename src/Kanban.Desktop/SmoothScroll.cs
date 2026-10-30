using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kanban.Desktop;

/// <summary>
/// マウスホイールのなめらかスクロール。WPF 既定はホイール 1 ノッチで一気に動く（カクカクする）ので、
/// アプリ内のすべての ScrollViewer のホイール処理を差し替え、目標位置へ毎フレーム減速しながら近づける。
/// 内側の一覧が端まで来ていれば処理せず外側の ScrollViewer に回す（既定と同じ連鎖）。
/// Shift を押しながらのホイールは横スクロール（横に動けない一覧は外側へ回す）。
/// </summary>
public static class SmoothScroll
{
    /// <summary>ホイール 1 ノッチ (delta=120) あたりの移動量 (px)。</summary>
    private const double PixelsPerNotch = 110;
    /// <summary>追従の時定数 (秒)。小さいほどキビキビ、大きいほどぬるっと。</summary>
    private const double TimeConstant = 0.075;

    private sealed class Axis
    {
        public double Target;
        public bool Animating;
    }

    private sealed class State
    {
        public readonly Axis V = new();
        public readonly Axis H = new();
        public TimeSpan LastTime;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly HashSet<ScrollViewer> Active = new();
    private static bool _hooked;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnMouseWheel));
        // スクロールバーのドラッグなど、マウスで直接操作し始めたら追従をやめる
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((s, _) =>
            {
                if (s is ScrollViewer sv && States.TryGetValue(sv, out var st)) Stop(sv, st);
            }), handledEventsToo: true);
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        var state = States.GetOrCreateValue(sv);
        var d = -e.Delta / 120.0 * PixelsPerNotch;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // 横に動ける一番内側の ScrollViewer を自分で探して動かす。
            // 素通しすると内側の一覧（縦スクロール）の既定処理が縦に動かして止めてしまうため、
            // 横に動けない場合も Handled にして縦には動かさない
            for (DependencyObject? d0 = sv; d0 is not null; d0 = VisualTreeHelper.GetParent(d0))
            {
                if (d0 is not ScrollViewer h || h.ScrollableWidth <= 0
                    || h.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
                var hs = States.GetOrCreateValue(h);
                var basisX = hs.H.Animating ? hs.H.Target : h.HorizontalOffset;
                if ((d < 0 && basisX <= 0) || (d > 0 && basisX >= h.ScrollableWidth)) continue;
                ScrollByX(h, d);
                break;
            }
            e.Handled = true;
            return;
        }
        if (sv.ScrollableHeight <= 0 || sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return;
        // 端まで来ていてその方向へは動けない → 外側へ回す
        var basis = state.V.Animating ? state.V.Target : sv.VerticalOffset;
        if ((d < 0 && basis <= 0) || (d > 0 && basis >= sv.ScrollableHeight)) return;
        ScrollBy(sv, d);
        e.Handled = true;
    }

    /// <summary>なめらかに dy (px) だけスクロールする（下が正）。</summary>
    public static void ScrollBy(ScrollViewer sv, double dy) =>
        Start(sv, s => s.V, sv.VerticalOffset, sv.ScrollableHeight, dy);

    /// <summary>なめらかに dx (px) だけ横スクロールする（右が正）。</summary>
    public static void ScrollByX(ScrollViewer sv, double dx) =>
        Start(sv, s => s.H, sv.HorizontalOffset, sv.ScrollableWidth, dx);

    private static void Start(ScrollViewer sv, Func<State, Axis> pick, double offset, double max, double delta)
    {
        var state = States.GetOrCreateValue(sv);
        var axis = pick(state);
        var basis = axis.Animating ? axis.Target : offset;
        axis.Target = Math.Clamp(basis + delta, 0, max);
        if (!axis.Animating)
        {
            axis.Animating = true;
            if (Active.Add(sv)) state.LastTime = TimeSpan.Zero;
        }
        if (!_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        foreach (var sv in Active.ToList())
        {
            var state = States.GetOrCreateValue(sv);
            if (!sv.IsLoaded)
            {
                Stop(sv, state);
                continue;
            }
            var dt = state.LastTime == TimeSpan.Zero ? 1.0 / 60 : (now - state.LastTime).TotalSeconds;
            state.LastTime = now;
            dt = Math.Clamp(dt, 0, 0.1);
            var k = 1 - Math.Exp(-dt / TimeConstant);
            if (state.V.Animating)
                Step(state.V, sv.VerticalOffset, sv.ScrollableHeight, k, sv.ScrollToVerticalOffset);
            if (state.H.Animating)
                Step(state.H, sv.HorizontalOffset, sv.ScrollableWidth, k, sv.ScrollToHorizontalOffset);
            if (!state.V.Animating && !state.H.Animating) Active.Remove(sv);
        }
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private static void Step(Axis axis, double cur, double max, double k, Action<double> scrollTo)
    {
        // 一覧の伸び縮み（ページング・高さ変化）に合わせて目標を収める
        axis.Target = Math.Clamp(axis.Target, 0, max);
        var diff = axis.Target - cur;
        if (Math.Abs(diff) < 0.5)
        {
            scrollTo(axis.Target);
            axis.Animating = false;
            return;
        }
        scrollTo(cur + diff * k);
    }

    private static void Stop(ScrollViewer sv, State state)
    {
        state.V.Animating = false;
        state.H.Animating = false;
        Active.Remove(sv);
    }
}
