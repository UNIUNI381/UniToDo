# 画面・API・CLI

## 画面

利用者向けの製品表示名は`UniToDo`です。Webタイトルと左上ブランド、トレイ、通知、音声確認画面で同じ名称を使用します。

| タブ | 主な機能 |
|---|---|
| ダッシュボード | 未確認の異常終了をCodex共有まで案内する永続警告、絞り込み別の推薦1件、待機中の一時的なタスク強制表示、非実行中の完了確認を含む固定幅の状態操作と右端のタスク編集、Codexタスク管理画面の起動、Google予定・締切・作業ログ、終了済みログの直接編集と実行中ログの開始時刻修正 |
| タスク | 活動時間外でも維持する優先度による優先順／登録順、検索、編集、Codexタスク管理画面の起動、仕事・私用・プロジェクト絞り込み、完了・中止の表示切替、親タスク名で系列を示して各カードから編集できるタスクツリー |
| 作業時間 | 時刻を省いた日付見出しと未来期間への移動制限を備えた日・週・月集計、未割当を含むプロジェクト絞り込み、プロジェクト色付きの活動別統計、ログ編集 |
| プロジェクト | 作成・編集、正式名称、別名、背景、既定期限、HSV色 |
| AI下書き | 検証結果、依存ツリー、一括承認 |
| 設定 | 活動時間、重み、Calendar、通知、埋め込みURL、Codex音声入力の送信先 |
| 履歴 | 通常履歴。Calendar同期・作業ログは任意表示 |

画面は`wwwroot/index.html`、`app.js`、`styles.css`によるフレームワーク非依存の単一ページUIです。変更後は静的資産のクエリ版も更新します。
スマホ幅では画面全体を端末幅に収め、上部メニューだけを横スクロール可能にします。初期表示は80%へ縮小し、利用者のピンチ拡大・縮小は制限しません。
初期化時に`GET /api/v1/access`で接続経路を取得します。Serve経由では設定タブとCodex起動ボタンを隠し、設定タブの復元も抑止します。API側でもPC専用操作を拒否します。リモート接続の許可設定は通常の設定APIから変更できません。詳細は[スマートフォンアクセス](11_スマートフォンアクセス.md)。
左上のブランドアイコンは現在表示中のタブ名を一時保存して再読み込みし、読込完了後に同じタブへ戻します。
APIまたは常駐処理が表示データを変更するとSSEで接続中の画面へ通知します。画面はページ全体を再読み込みせず、選択中のタブに必要なデータだけを再取得するため、タブ、絞り込み、作業時間の表示期間を維持します。同じ画面自身の保存通知はクライアントIDで除外し、ダイアログまたは未保存の設定を編集中なら閉じるか保存するまで外部変更の反映を保留します。ブラウザタブへ戻った時にも表示中のタブを再取得し、SSE切断中の変更を整合させます。

F13音声入力はWebタブではなく、トレイ常駐プロセスの状態画面とWinForms確認画面を使用します。操作、表示状態、TypeWhisper連携は[音声入力](07_音声入力.md)を参照します。

## ローカルAPI

すべて`/api/v1`配下で、camelCase JSONを使用します。

| グループ | 主なパス | 用途 |
|---|---|---|
| 状態 | `/health`、`/changes`、`/dashboard`、`/system-incidents/pending`、`/system-incidents/{id}/acknowledge` | 稼働確認、SSE変更通知、推薦、カレンダーウィジェット、異常終了の確認 |
| タスク | `/tasks`、`/tasks/{id}/actions/{action}` | CRUDと状態操作 |
| 作業時間 | `/time-entries`、`/time-entries/{id}/start`、`/time-reports` | タイマー、実行中ログの開始時刻修正、手入力、確認、無効化、集計 |
| プロジェクト | `/projects`、`/projects/resolve`、`/projects/prepare` | 管理、名前解決、AI向け背景 |
| 下書き | `/draft-batches` | 冪等登録、読取、画面承認 |
| Calendar | `/calendar/credentials`、`connect`、`synchronize` | OAuthと読取専用同期 |
| [Codex音声入力](07_音声入力.md) | `/codex/reviews`、`/codex/task-thread/open` | 確認待ち受付、ブラウザへ`codex://`リンクを返す設定済みタスクの表示（サーバー側では外部プロセスを起動しない） |
| 設定・履歴 | `/settings`、`/history` | 設定と監査履歴 |
| 保全 | `/backup` | SQLiteバックアップ |

画面とCLI以外へ公開するAPIではありません。待受アドレスをLANへ広げません。

`PUT /tasks/{id}`と`taskctl update ID --file changes.json`は指定項目だけを更新します。CLIはJSONの省略を維持し、Serviceが操作ロック内で最新値へ合成します。省略は維持、nullable項目の`null`と配列の`[]`は明示的な解除です。空入力、未知・重複項目、不正な型、非nullable項目のnull、空白名称、ID変更を保存前に拒否します。全項目を送る既存画面とも互換です。期限日時だけの指定では期限由来を`explicit`（nullでは`none`）へ補完し、期限種別は既存の正規化規則に従います。履歴・更新日時・検証結果・推薦は通常どおり再計算します。

複数件の更新は各件保存です。AI操作では対象を事前に絞り、各件の直前確認と読戻し比較を行い、失敗・不一致時は後続を停止して成功済みIDと未処理件数を報告します。

音声確認APIの契約、Codex CLI引数、失敗時の扱いは[音声入力](07_音声入力.md)を正本とします。

## taskctl

| 用途 | コマンド |
|---|---|
| 今やること | `taskctl now --json` |
| タスク | `list`、`add`、`update`、`start`、`complete`、`continue`、`interrupt`、`postpone`、`cancel`、`delete` |
| 下書き | `draft-create`、`draft list`、`draft get` |
| プロジェクト | `project list`、`resolve`、`prepare`、`create`、`update`、`archive`、`alias-add`、`context-add`、`context-update` |
| 作業時間 | `time active`、`list`、`start`、`stop`、`add`、`update`、`extend`、`confirm`、`void`、`report` |
| Calendar | `calendar-sync` |

開発ツリーでは`./scripts/task.ps1`がインストール済みCLIの代替です。後続処理へ渡す場合は`--json`を使います。

## AI操作上の境界

- プロジェクト付き操作前に`project prepare`を行い、解決済みIDを明示する。
- `ambiguous`または`not_found`は終了コード2。確認前に紐付け・別名登録しない。
- 下書き承認は画面だけで行い、CLIコマンドを提供しない。
- 手入力ログの重複はHTTP 409で拒否し、確認後だけ`--allow-overlap`を使う。
- 通常の取り消しは`cancel`。完全削除は同じIDを`--confirm`へ再指定する。
- CLIエラーをSQLite直接編集で迂回しない。

完全なコマンド形式、終了コード、JSON例は[AI操作ガイド](../AI_OPERATIONS.md)を正本とします。
