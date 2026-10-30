using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Kanban.Desktop;

/// <summary>
/// ドラッグ中にカーソルへ追従する「つかんだカード」の見た目（Trello 風）。
/// 元カードのスナップショットを少し傾けて浮かせ、影を付けて描く。
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    private readonly Border _host;
    private readonly Point _grabOffset;
    private Point _position;

    public DragGhostAdorner(UIElement adornedRoot, FrameworkElement source, Point grabOffset) : base(adornedRoot)
    {
        IsHitTestVisible = false;
        _grabOffset = grabOffset;

        var w = source.ActualWidth;
        var h = source.ActualHeight;
        var image = new Image
        {
            Source = Snapshot(source),
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // 影は画像の背面の別レイヤー（Effect を画像に直接掛けない）
        var shadow = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 8, Opacity = 0.22, Color = Color.FromRgb(0x0F, 0x17, 0x2A) },
        };
        var stack = new Grid();
        stack.Children.Add(shadow);
        stack.Children.Add(image);

        _host = new Border
        {
            Child = stack,
            // 少し傾けて「持ち上げている」感を出す
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = { new RotateTransform(3), new ScaleTransform(1.02, 1.02) },
            },
            Opacity = 0.97,
        };
        AddVisualChild(_host);
    }

    private static BitmapSource Snapshot(FrameworkElement el)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        // 文字サイズ設定で拡大表示しているときは、その倍率ぶん細かく撮る（拡大されてもぼやけないように）
        var zoom = UiZoom.Scale;
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX * zoom)),
            Math.Max(1, (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY * zoom)),
            dpi.PixelsPerInchX * zoom, dpi.PixelsPerInchY * zoom, PixelFormats.Pbgra32);
        // 要素自身の位置に関係なく (0,0) から描くため VisualBrush 経由で描画
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>カーソル位置（adornedRoot 座標）を更新する。</summary>
    public void MoveTo(Point p)
    {
        _position = p;
        InvalidateArrange();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _host;

    protected override Size MeasureOverride(Size constraint)
    {
        _host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = _position.X - _grabOffset.X;
        var y = _position.Y - _grabOffset.Y;
        _host.Arrange(new Rect(new Point(x, y), _host.DesiredSize));
        return finalSize;
    }
}
