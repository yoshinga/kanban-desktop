# kanban-desktop

同期フォルダー上のプレーンテキスト（例: Dropbox）を正本とする、C# / .NET 8 / WPF のカンバン。

- データ正本: 同期フォルダー内の Markdown / JSON（任意のディレクトリを vault として指定）
- アプリ: WPF（ライトテーマ）
- ローカル REST API: アプリ内で Kestrel をホスト（`127.0.0.1`）
- エージェント: 独立した C# MCP サーバ（stdio）→ ローカル API

## 必要環境

- Windows
- .NET SDK 8.0（WPF / ASP.NET Core ランタイム同梱）

## ビルドと実行

```powershell
dotnet build Kanban.Desktop.sln
dotnet run --project src/Kanban.Desktop
```

初回起動時に `%LOCALAPPDATA%\Kanban\settings.json` を生成し、vault パス（既定
`%USERPROFILE%\KanbanVault`。変更は settings.json を直接編集）・ポート・トークンを保存する。
同ディレクトリの `api.json` に `{ port, token }` を書き出し、MCP サーバがこれを読む。

### ヘッドレス起動（API のみ）

```powershell
dotnet run --project src/Kanban.Desktop -- --api-only
```

### UI の見た目を PNG に保存（開発用・画面を前面に出さない）

```powershell
dotnet run --project src/Kanban.Desktop -- --screenshot C:\path\to\outdir
```

`shot-boards.png` / `shot-adding.png` / `shot-detail.png` / `shot-detail-bottom.png` / `shot-detail-quickadd.png` / `shot-detail-wide-zoom.png` / `shot-settings.png` / `shot-markdown.png` / `shot-detail-edit.png` / `shot-today.png` /
`shot-collapsed.png` / `shot-sidebar-search.png` / `shot-board-settings.png` / `shot-activity.png` / `shot-search.png` を出力して終了する。
常駐中の本体とは別に動く（多重起動チェック・API ホストは省略）。

### コメントの Markdown プレビュー確認（開発用）

コメントファイル（`comments/<番号>/*.md`）を詳細画面と同じ幅で描いて PNG に保存する。表の崩れなどの確認用。

```powershell
dotnet run --project src/Kanban.Desktop -- --mdtest <コメントの .md> <出力 .png>
```

### 自動検証（開発用）

vault のコピーを渡すと、Tags 画面の操作と絞り込み中の追加を実行して `selftest-result.txt` に PASS/FAIL を書き出す。
**本番の vault は渡さないこと**（タグやアイテムを実際に作成・変更する）。

```powershell
dotnet run --project src/Kanban.Desktop -- --selftest <vaultのコピー>
```

### 入力の実機検証（開発用）

本物のキー / マウス入力（SendInput）で、入力欄の Ctrl+C / Ctrl+V（IME オン中を含む）を確かめる。vault のコピーを 2 つ目に渡すと、
メイン画面の検索欄・詳細のタイトル/説明、Shift+ホイールの横スクロール、Ctrl+クリックの複数選択と番号コピーも確かめる。
結果は `<出力先>/clip-test.txt`。数秒間マウス・キーボード・クリップボードを使う（終了時にクリップボードの文字列は戻す）。
**本番の vault は渡さないこと**（詳細のタイトル・説明を書き換える）。

```powershell
dotnet run --project src/Kanban.Desktop -- --cliptest C:\path\to\outdir <vaultのコピー>
```

### 性能計測（開発用）

```powershell
dotnet run --project src/Kanban.Desktop -c Release -- --bench C:\path\to\bench.txt
```

起動・再読み込み・アイテムを開く/閉じる・フィルタ切替の所要時間（描画込み）を書き出す。データは変更しない。

## UI の操作

- **ヘッダー**: プロジェクト切替（選択は `%LOCALAPPDATA%\Kanban\ui.json` に保存）、検索、
  Boards / Activity の切替
- **サイドバー（プロジェクトタグ）**: すべて / 今日 / 仕事 (`w-*`) / プロジェクト (`p-*`)。
  件数は Inbox + Doing の在籍数。クリックで単一選択（再クリックで解除）、Ctrl+クリックで複数選択。
  「今日」は期限が今日以前のカード（`p-tracker` 除外）。左上の `<` で折りたたみ（状態は保存）。
  上部の「タグを検索」で名前・説明の部分一致で絞り込み（Enter で先頭を選択、Esc でクリア）。
  「仕事」「プロジェクト」の見出しクリックでグループを開閉（状態は `ui.json` に保存。タグ検索中・アイコン表示中は閉じていても中身を出す）
- **ボードの設定**: カラム見出しの `⋯` か、右端の「+ ボードを追加」でモーダルを開く。各ボードの絵文字（見出しの名前の前に表示）・名前・色・完了扱い (`is_done`)・折り返し表示 (`layout`) の編集、▲▼ で並び替え、削除（中のアイテムも消える）、新規ボードの追加ができる。名前は Enter かフォーカスアウトで保存、Esc で取り消し / 閉じる。Inbox / Doing はサイドバー件数の判定に名前を使う
- **ボード**: カラムの「+ アイテムを追加」でインライン追加（ボードの先頭に入る。Enter で連続追加、Esc で閉じる。長いタイトルは入力欄内で折り返す）。
  ヘッダーの漏斗アイコンで、そのボードだけを非カテゴリタグで絞り込み（件数は「表示/全体」）。
  ボード設定が `layout: "board"` のボード（Personal の Inbox）は縦スクロールせず、下端で次の列に折り返して横に広がる。
  Done ボードは最初の 30 件だけ取得し、一番下までスクロールする（または「↓ もっと読み込む」を押す）と次の 30 件を追加取得（件数ピルは「30+」）。サイドバーやボード内のタグで絞り込み中は、絞り込んだ結果に対して 30 件ずつ出す（件数も絞り込み後）
- **カード**: クリックで詳細。ドラッグ&ドロップで移動（Trello 風: つかんだカードが傾いてカーソルに付いてきて、元の場所は灰色の枠、落とし先にはカードの高さぶんの隙間が開く。カラムの外で離しても最後に開いた隙間へ入る。ボード名のあたりで離すと先頭、カード一覧の下の空きで離すと末尾）。
  右クリックで「内容をコピー（Markdown）」「番号をコピー（`#N`）」「期限（今日/明日/1週間後/クリア）」「タグの付け外し」。
  Ctrl+クリックで複数選択（開かずに選択の切り替え。青枠）。選択中は画面下にバーが出て、`⋯` / Ctrl+C / 右クリックで番号をまとめてコピー（`kanban 1000, 1001, 1002` 形式・番号順）、Esc か ✕ で解除。
  タグは最大3つ+`+N`、期限はチップ、期限超過は赤ドット、優先度「高」はフラグ、Done ボードは淡色+取り消し線
- **検索**: Ctrl+F でフォーカス、Esc でクリア。結果カードには所属ボード名を表示
- **詳細（モーダル）**: 暗い背景のクリック / ✕ / Esc で閉じる。タイトル・期限・説明はフォーカスを外すと自動保存
  （閉じるときも保存）。説明欄は Markdown をそのまま書くテキスト欄（Ctrl+S でも保存）。
  優先度ピル・期限クイック設定は即保存。左上のコンボでボード移動、`#N` クリックで番号をコピー。
  タグ入力中は既存タグの候補を表示し、Enter は候補の先頭を付ける（新規作成は「＋ を作成して付ける」から）。
  サブタスクは Enter で追加、コメントは一覧の上の入力欄から Ctrl+Enter で投稿（一覧は新しい順。本文は表も描ける簡易 Markdown プレビューで、ドラッグで選択してコピーできる。右クリックで原文もコピー可）。
  アクティビティは畳んであり、見出しのクリックで開く
  左右の端（ホバーで青い線）をドラッグで幅を変更（中央寄せのまま両側に広がる）。幅は `ui.json` に保存して次回も同じ幅で開く。端のダブルクリックで既定幅（672px）に戻す
- **Tags**: ヘッダーの「Tags」でタグ管理画面。新規タグ（絵文字・名前・色・path・説明、
  色は未指定なら名前から自動配色）と、仕事 / プロジェクト / その他 / アーカイブ済み のグループ別一覧。
  各行で名前・絵文字・色・path・説明（Enter かフォーカスアウトで保存、Esc で取り消し）、お気に入り、
  アーカイブ / 復活、削除、親タグ（p-* / w-* 以外）を編集できる。「全タグを自動配色」で色を一括リセット
- **絞り込み中の追加**: サイドバーでタグを選んでいる間に追加したアイテムにはそのタグが、「今日」選択中は今日の期限が付く
- **アクティビティ**: 操作を日本語ラベル+移動元→先で表示（エージェントの操作は紫のバッジ）
- **カラー絵文字**: WPF は標準ではカラー絵文字を描けないので Emoji.Wpf を使う。カードのタイトルは
  `local:EmojiText.Text`（`EmojiText.cs`。絵文字だけ EmojiInline に置き換え、「#番号」と同じ行に混ぜられる）、
  サイドバーのタグ絵文字は `emoji:TextBlock`、詳細のタイトル欄と Tags 画面の絵文字欄は `emoji:TextBox`（入力中もカラー）。
  国旗は Windows の絵文字フォントに無いので出ない
- **なめらかスクロール**: マウスホイールはアプリ内のすべてのスクロール領域で、目標位置へ減速しながら近づける
  （1 ノッチ約 110px・約 0.3 秒）。内側の一覧が端まで来たら外側へ回すのは既定どおり（`SmoothScroll.cs`）。
  Shift+ホイールは横スクロール（横に動けない一覧は外側＝ボード全体の横スクロールへ回す）
- **設定**: ヘッダーの「設定」でモーダル。**文字サイズ**は画面全体の表示倍率（80〜160%、5% 刻み）で、ポップアップ・ツールチップ・右クリックメニューにも同じ倍率を掛ける（文字サイズは各所に直書きなので、`UiZoom.cs` の共有 ScaleTransform を `LayoutTransform` に掛けてまとめて拡大する）。Ctrl+マウスホイール / Ctrl+; / Ctrl+- / Ctrl+0 でも変更でき、`ui.json` に保存する（開発用モードは常に 100%）。詳細の幅もここで既定に戻せる
- **通知**: コピーや移動などは画面下部のトーストで知らせる
- **IME オン中のショートカット**: 日本語 IME がオンだと Ctrl+V などが `Key.ImeProcessed` として届き、WPF 標準の
  編集ショートカットが効かない。`ImeShortcuts.cs` で入力欄の Ctrl+V / C / X / A / Z / Y（Ctrl+Shift+Z）を拾い直し、
  アプリ独自の Ctrl+F / Ctrl+S / Ctrl+Enter / Ctrl+C も `e.RealKey()` で本当のキーを見る
- **スクショ / ファイルの貼り付け**: クリップボードに文字列が無く画像があるとき、入力欄への貼り付け（Ctrl+V・右クリック・IME オン中も）は
  画像のパスを入れる。Snipping Tool が自動保存したスクショ（スクリーンショット フォルダーの直近 10 分で縦横のピクセル数が同じ PNG）があればそのパス、
  無ければ同じフォルダーに `kanban-paste <日時>.png` で保存してそのパス。エクスプローラーでコピーしたファイルはそのパス（複数なら改行区切り）。`PastePath.cs`

## 起動アイコン / タスクトレイ常駐

デスクトップとスタートメニューに「Kanban」アイコンを作る:

```powershell
pwsh -File tools/install.ps1
```

- `dotnet publish -c Release` で `%LOCALAPPDATA%\Kanban\app` に配置し、
  デスクトップ / スタートメニューにショートカットを作成する。
- アプリは **タスクトレイに常駐**する。ウィンドウの「×」で閉じても終了せず、
  トレイに残る（最小化でもトレイに隠れる）。トレイのダブルクリックで再表示、
  右クリックメニューの「終了」で完全終了。
- アイコンは `tools/make-icon.ps1` で生成（`src/Kanban.Desktop/Assets/kanban.ico`）。
- 多重起動は Mutex で防止（2 回目はメッセージを出して終了）。

## プロジェクト構成

```
src/
  Kanban.Core/      ファイルストア・ドメインルール・変更イベント（UI 非依存）
  Kanban.Api/       Kestrel 上のローカル REST API（MCP と外部ツール用）
  Kanban.Desktop/   WPF アプリ（UI + API ホスト + ファイル監視）
  Kanban.Mcp/       MCP サーバ（stdio → ローカル API）
tests/
  Kanban.Core.Tests/
```

## vault 構成

```
<vault root>/
  vault.json  projects.json  boards.json  tags.json  sequence.json
  items/<number>.md                       # frontmatter(メタ) + 本文(description)
  comments/<number>/<utc>-<actor>-<id8>.md
  activity/<YYYY-MM>.ndjson
```

- 1 アイテム = 1 Markdown。frontmatter に id / number / board / priority / due / tags /
  archived / is_template / position / done_at / created / updated / subtasks を持つ。
- コメントは**追記型の別ファイル**（同時編集でコンフリクトしない）。
- アクティビティは月別 NDJSON で追記。
- `position` は小数点つきの疎な値（並び替えは隣接値の中点。詰まったら再採番）。

## ローカル API

`X-API-Key: <token>` を要求（`/api/health` を除く）。`X-Actor` で実行者名を指定。
主なルート:

```
GET  /api/projects, /api/boards, /api/boards/{id}/items, /api/boards/{id}/stats
GET  /api/items/{id}, POST /api/items, PATCH/DELETE /api/items/{id}
POST /api/items/{id}/move, /archive, /subtasks, /comments, /tags
GET  /api/tags, /api/activity, /api/search
```

## MCP（エージェント連携）

`Kanban.Mcp` は stdio の MCP サーバ。デスクトップアプリ（ローカル API）が起動している
必要がある。`%LOCALAPPDATA%\Kanban\api.json` から port/token を読む。

Claude Code / opencode の MCP 設定例:

```json
{
  "mcpServers": {
    "kanban": {
      "command": "C:\\path\\to\\kanban-desktop\\src\\Kanban.Mcp\\bin\\Debug\\net8.0\\Kanban.Mcp.exe"
    }
  }
}
```

環境変数でも上書きできる: `KANBAN_API_URL`, `KANBAN_API_KEY`, `KANBAN_ACTOR`。

提供ツール:
`list_projects, create_project, list_boards, get_board, create_board, update_board,
delete_board, board_stats, list_items, list_items_by_tag, get_item, create_item, update_item, move_item,
delete_item, archive_item, add_subtask, update_subtask, delete_subtask, add_comment,
delete_comment, list_tags, create_tag, update_tag, delete_tag, attach_tag, detach_tag,
list_activity, search`

## テスト

```powershell
dotnet test
```

## 実装メモ

- 検索インデックスは SQLite ではなく **in-memory**。vault をメモリに展開し、検索・一覧は
  LINQ で処理（約 1,000 アイテム規模では十分高速で、破損時の再構築も不要）。
- item frontmatter の `board` はボード GUID（表示名は `boards.json` から解決）。
- トラッカー（スケジュール・自動 tick）は実装しない。
- コンフリクト解決 UI・タグ絞り込み UI は今後（現状は検知の土台のみ）。
