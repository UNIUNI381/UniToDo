# UniToDo

仕事と私用を一括評価し、「今やること」を1件だけ返すWindows専用タスク管理システムです。.NET 10、ASP.NET Core、SQLite、常駐トレイ、`taskctl`で構成され、タスク・自由活動の作業時間もローカルへ記録します。OpenAI API、Apps Script、業務データを保存する外部サーバーは使いません。Androidからは任意のTailscale Serve経由で接続できます。

利用者向けの正式名称は「UniToDo」です。互換性維持のため、実行ファイル、ソリューション、名前空間、保存先などの開発上の名前は`TaskManager`のままです。

AI・開発者向けの設計資料は[UniToDo設計Vault](docs/TaskManager-Vault/00_入口.md)にあります。Obsidianでは`docs/TaskManager-Vault`を専用Vaultとして開きます。

## ライセンス

本プロジェクトが独自に作成したソース、文書、`TaskManager.ico`、`favicon.svg`はMIT Licenseで提供します。

Copyright (c) 2026 uniuni ([https://x.com/lept_on](https://x.com/lept_on))

ライセンス本文は[LICENSE](LICENSE)、外部ライブラリと自己完結型.NETランタイムの条件は[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)を確認してください。頒布物とインストール先にも同じ情報と、発行に使用した.NET SDK付属のライセンス原文を収録します。

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
- PC・Android共通のCodex会話画面（App Server経由の送信、応答、質問・承認、停止）
- F13からOllamaとTypeWhisperを必要時だけ自動起動し、校正した音声入力を確認・編集して指定CodexタスクへCLI送信

## 開発環境の準備

Windows 10または11で、Microsoft公式の.NET 10 SDK `10.0.303`以降を導入します。

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet --list-sdks
```

各スクリプトはワークスペース内の`.dotnet`を優先し、なければPATH上の.NET 10を使います。ビルドとテストはWindows PowerShell 5.1とPowerShell 7のどちらでも実行できます。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

## 発行とインストール

自己完結型`win-x64`配布物を作り、ユーザー領域へインストールします。利用PCに.NETランタイムは不要です。

発行とインストールにはPowerShell 7を使用します。Windows PowerShell 5.1の`powershell`ではなく、次のように`pwsh`を明示して実行してください。

```powershell
pwsh -NoProfile -File .\scripts\install.ps1
```

`install.ps1`は内部で発行します。発行だけなら`publish.ps1`、検証済み発行物の導入だけなら`install.ps1 -SkipPublish`を使います。

インストール先は`%LOCALAPPDATA%\Programs\TaskManager`、データ保存先は`%LOCALAPPDATA%\TaskManager`です。デスクトップには`UniToDo`ショートカット、ログオン時の自動起動にはWindowsタスク`UniToDo Watchdog`が設定され、`http://127.0.0.1:48120`だけで待ち受けます。

Androidから利用する場合は、[スマートフォンアクセス](docs/TaskManager-Vault/11_スマートフォンアクセス.md)に沿って本人限定のTailscale Serveを設定します。PCはログオン済み・スリープなしで稼働させます。

「Codexでタスク管理」はWeb画面内で会話できます。PCに導入済みのCodex CLIとログインを使用します。音声入力も同じ会話へ送信します。詳細は[Codex会話](docs/TaskManager-Vault/12_Codex会話.md)を参照してください。

## GitHubからの入手と頒布

ソースコードはGitHubの公開リポジトリで提供し、利用者向けの自己完結型`win-x64`頒布物はGitHub Releasesで提供します。利用者は各Releaseに添付された`UniToDo-win-x64.zip`をダウンロードしてください。GitHubが自動生成する`Source code (zip)`と`Source code (tar.gz)`には発行済みランタイムが含まれないため、インストール用頒布物として使用しません。

頒布ZIPの生成は明示的に必要な場合だけ行います。生成コマンド、検査、公開、受取人の導入手順は[頒布と受取人セットアップ](docs/TaskManager-Vault/08_頒布と受取人セットアップ.md)にまとめています。受取人はZIPを解凍し、`最初にお読みください.txt`から開始してください。

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

F13で録音を開始・停止し、校正本文を確認・編集してWebと共通のCodex会話へ送信します。OllamaとTypeWhisperは必要時に起動します。追加質問への回答や停止はWeb画面で行います。構成・設定・障害対応は[音声入力](docs/TaskManager-Vault/07_音声入力.md)を参照してください。

## 初回設定

1. デスクトップの`UniToDo`ショートカットを開きます（実行ファイル名は`TaskManager.exe`のままです）。
2. 「設定」で活動時間、作業時間、優先度係数、通知を確認します。音声入力の送信先はWebのCodex会話と共通です。
3. Google CloudでCalendar APIを有効にし、デスクトップアプリ用OAuthクライアントの`credentials.json`を取得します。
4. 「設定」からJSONを登録し、「Google Calendarに接続」を押します。要求権限は`calendar.readonly`のみです。

認証トークンはWindowsユーザー単位のDPAPIで暗号化されます。同期失敗時は最後のカレンダーキャッシュを維持し、画面へ最終同期日時とエラーを表示します。

## CLI

```powershell
taskctl now --json
taskctl list --status 実行可能 --json
taskctl get TASK-ID --json
```

Codexからの登録・更新・下書き・作業時間の操作は[AI操作ガイド](docs/AI_OPERATIONS.md)を参照してください。開発ツリーからの手動実行には `./scripts/task.ps1 <command>` を使えます。

## バックアップ

ドライブ故障への備えとして、設定画面の「追加バックアップ」で別の物理ディスク上の専用フォルダを指定し、ONにすることをおすすめします。初期設定はOFF・保存先未指定です。別ディスクがない環境ではOFFのまま利用できます。保存後は「今すぐバックアップ」で成功を確認してください。

追加バックアップは稼働中に1時間ごと・1日ごとに確認し、各区分の直近バックアップからDBの内容・構造が変わった場合だけ、正本DBを`hourly`（最大6世代）と`daily`（最大30世代）へ保存します。変更なしの場合は世代を増やさず、既存世代も削除しません。設定画面には最終確認時刻と実際の最終保存時刻、スキップ結果を別々に表示します。手動実行でも変更なしなら保存を省略します。設定・予定同期などによるDB更新も比較対象です。

過去のバックアップは含めず、停止中の時間帯も補完しません。失敗時は設定画面に理由を表示し、次の1分周期に再試行します。手動保存も時間別6世代に含みます。OFFや保存先変更で既存ファイルは削除しません。ドライブ文字が違っても同じ物理ディスクなら、そのディスクの故障対策にはなりません。

復元手順は[開発運用のバックアップ復元](docs/TaskManager-Vault/06_開発運用.md#バックアップ復元)を参照してください。

- スキーマ更新前と毎日、`%LOCALAPPDATA%\TaskManager\backups`へSQLiteバックアップを作成します。
- 新しい順に30件を保持します。

## アンインストール

```powershell
.\scripts\uninstall.ps1
```

実行ファイル、ログイン時起動、PATH設定だけを解除します。タスクDB、認証情報、バックアップは切り戻しのため保持します。
