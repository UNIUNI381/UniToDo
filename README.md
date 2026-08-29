# Local Task Manager

仕事と私用を一括評価し、「今やること」を1件だけ返すWindows専用タスク管理システムです。.NET 10、ASP.NET Core、SQLite、常駐トレイ、`taskctl`で構成され、タスク・自由活動の作業時間もローカルへ記録します。OpenAI API、Apps Script、外部サーバーは使いません。

AI・開発者向けの設計資料は[Task Manager設計Vault](docs/TaskManager-Vault/00_入口.md)にあります。Obsidianでは`docs/TaskManager-Vault`を専用Vaultとして開きます。

## 主な機能

- 期限までの累積作業量を基に、期限リスク0.45、重要度0.25、後続解放0.10、延期0.10、継続性0.10、開始可能日からの経過0.10を相対加重平均して優先順位を計算
- 後続の期限を先行タスクへ継承し、先の締切を損なわない範囲で後日の重要タスクを提案
- 20%の予備時間、15分未満の新規開始禁止、実行中タスクの継続を考慮
- 親子タスク、正規化した依存関係、下書き承認、循環依存検出
- プロジェクト別の表記ゆれ、自由記述背景情報、毎週の既定期限
- Codex向けの名前解決・統合コンテキスト取得CLI
- Google Calendarの読取専用同期と予定前後10分の余白
- 朝の最優先通知、推奨時間＋10分後の完了確認、30世代のSQLiteバックアップ
- 別Codexスレッドから操作できるCLI
- F13からOllamaとTypeWhisperを必要時だけ自動起動し、校正した音声入力を確認・編集して指定CodexタスクへCLI送信

## 開発環境の準備

Windows 10または11で、Microsoft公式の.NET 10 SDKを導入します。

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet --list-sdks
```

この作業環境ではWinGetが利用できなかったため、公式`dotnet-install`でワークスペース内の`.dotnet`へSDK 10.0.302を導入しています。各スクリプトはローカルSDKを優先し、なければPATH上の.NET 10を使います。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

## 発行とインストール

自己完結型`win-x64`配布物を作り、ユーザー領域へインストールします。利用PCに.NETランタイムは不要です。

```powershell
.\scripts\publish.ps1
.\scripts\install.ps1
```

インストール先は`%LOCALAPPDATA%\Programs\TaskManager`、データ保存先は`%LOCALAPPDATA%\TaskManager`です。ログオン時に監視親を起動するWindowsタスクとユーザーPATHが設定され、`http://127.0.0.1:48120`だけで待ち受けます。

## 他の人への直接頒布

利用・改良に必要なソース、自己完結ランタイム、Codex Skillをまとめ、個人データを検査したZIPを作成します。

```powershell
.\scripts\create-distribution.ps1
```

生成物は`artifacts/distribution`配下です。ワークスペースを手動でZIP化せず、必ずこのスクリプトを使用します。DB、バックアップ、資格情報、OAuthトークン、実タスク、作成者固有のCodexタスクIDは含まれません。

受取人はZIPを展開し、展開ルートをCodexの作業フォルダーとして開いてから、`最初にお読みください.txt`のプロンプトをCodexへ入力します。Codexが`AGENTS.md`と設計Vaultを読み、環境確認、`Install.ps1`、タスク操作、開発準備を案内します。詳細は[頒布と受取人セットアップ](docs/TaskManager-Vault/08_頒布と受取人セットアップ.md)を参照してください。

## 音声入力連携

TypeWhisper連携にはOllama、TypeWhisper、公式Windowsインストーラー版Codex CLIが必要です。CLIを導入して個別にログインします。

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
& "$env:USERPROFILE\.codex\packages\standalone\current\bin\codex.exe" login
```

TypeWhisperを終了し、最初に`-WhatIf`で変更対象を確認してから連携を適用します。

```powershell
.\scripts\install-typewhisper-integration.ps1 -WhatIf
.\scripts\install-typewhisper-integration.ps1
```

設定画面で送信先CodexタスクIDを指定すると、利用者はF13で録音を開始・停止し、校正本文を確認・編集して送信できます。Task ManagerはTypeWhisperのループバックAPIで「音声校正」ワークフローを直接開始・停止します。API起動と選択モデルのダウンロード済み状態を確認した後、録音開始API内で認識モデルを遅延ロードし、実際の録音開始を確認してから表示を進めます。F14開始とF15停止は手動操作用の予備経路として残します。Ollamaの起動とモデルロードは録音と並行し、録音停止後は対象モデルのロード完了を確認してから本文をOllamaへ渡します。Codex CLIはGit管理外の常駐アプリ配置先から起動するため、この送信経路だけリポジトリ検査を省略します。サンドボックスと承認設定は維持します。構成、データ保護、CLI送信、テスト、障害対応は[音声入力設計](docs/TaskManager-Vault/07_音声入力.md)を参照してください。

## 初回設定

1. `TaskManager.exe`を起動し、ダッシュボードを開きます。
2. 「設定」で活動時間、作業時間、優先度係数、通知、Codex音声入力の送信先タスクIDを確認します。
3. Google CloudでCalendar APIを有効にし、デスクトップアプリ用OAuthクライアントの`credentials.json`を取得します。
4. 「設定」からJSONを登録し、「Google Calendarに接続」を押します。要求権限は`calendar.readonly`のみです。

認証トークンはWindowsユーザー単位のDPAPIで暗号化されます。同期失敗時は最後のカレンダーキャッシュを維持し、画面へ最終同期日時とエラーを表示します。

## CLI

```powershell
taskctl now --json
taskctl list --status 実行可能 --json
taskctl add --title "見積書を送付" --minutes 30 --importance 4
taskctl start TASK-ID
taskctl complete TASK-ID
taskctl postpone TASK-ID --minutes 60
taskctl cancel TASK-ID
taskctl project prepare "サンプル案件" --json
taskctl draft-create --file draft.json --idempotency-key REQUEST-KEY --json
taskctl draft list --project PROJECT-ID --json
taskctl draft get BATCH-ID --json
taskctl time active --json
taskctl time start --title "資料調査" --project PROJECT-ID
taskctl time stop TIME-ID
taskctl time report --period week --json
```

作業ログの手入力・更新では、既存区間と重複すると保存前にエラーになります。内容を確認して両方を集計する場合だけ`--allow-overlap`を指定します。

開発ツリーからは`.\scripts\task.ps1 now --json`のように実行できます。`draft-create --help`、`project prepare --help`、`add --help`でJSON出力と終了コードを確認できます。JSONによる登録・更新・下書き分解は[AI操作ガイド](docs/AI_OPERATIONS.md)を参照してください。

## バックアップ

- スキーマ更新前と毎日、`%LOCALAPPDATA%\TaskManager\backups`へSQLiteバックアップを作成します。
- 新しい順に30件を保持します。

## アンインストール

```powershell
.\scripts\uninstall.ps1
```

実行ファイル、ログイン時起動、PATH設定だけを解除します。タスクDB、認証情報、バックアップは切り戻しのため保持します。
