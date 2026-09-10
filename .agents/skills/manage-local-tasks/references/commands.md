# taskctl参照

## コマンド

```text
now|list [--json]
get TASK-ID [--json]
add ... | update ID --file task.json
start|complete|continue|interrupt|postpone|cancel ID
delete ID --confirm ID
draft-create --file draft.json --idempotency-key KEY --json
draft list [--project PROJECT-ID] --json
draft get BATCH-ID --json
project list|resolve|prepare|create|update|archive|alias-add|context-add|context-update
calendar-sync
time active|list|start|stop|add|update|extend|confirm|void|report [--json]
```

不明なオプションは`taskctl <コマンド> --help`で確認する。

## 短いJSON入出力

UniToDo専用会話では専用PATHの`taskctl`を直接実行する。通常のCodexはスキルのラッパーを使用する。

- すべての`--file`入力は`--file -`で標準入力のJSONオブジェクトを読める。既存ファイル入力も使用可能。
- `--json`は字下げなし・日本語をUnicodeエスケープしないJSONを返す。
- 読取では`--fields identifier,title,projectIdentifier,status`で必要項目だけを選ぶ。`--json`は省略できる。ネストは`recommendation.task.title`形式。配列の件数、null、空配列を保持する。存在しない項目はエラー。ただしnull・空配列の内部の項目名は検証できない。
- 対応する読取は`now/list/get`、`project list`、`draft list/get`、`time active/list/report`。`project prepare/resolve`と更新応答は確認情報・警告を省略しない。
- 書込み前後の確認にはID、projectIdentifier、状態、変更対象を必ず含める。単一タスクは`get ID`を使う。

PowerShell 7での例（5.1では日本語を保つため同じコマンドの先頭に`$OutputEncoding=[Text.Encoding]::UTF8;`を付ける）:

```powershell
'{"details":"確認済みの補足"}' | taskctl update TASK-ID --file - --json
taskctl get TASK-ID --fields identifier,projectIdentifier,status,details
```

長文や引用符を含むJSONには、PowerShellの単一引用符のヒアストリングを使う。本文中の`$`やバッククォートを展開しない。

## 権限エラー

UniToDo専用会話では権限不足を報告して停止する。通常のCodexでラッパー実行が拒否されたら、同じラッパーを`require_escalated`で再実行する。承認prefixは`powershell -NoProfile -ExecutionPolicy Bypass -File <スキル絶対パス>\scripts\invoke-taskctl.ps1`までに限定する。

## PowerShell 5.1

- 日本語を含む`.ps1`はUTF-8 BOM付きにする。`apply_patch`後に必要なら次で変換する。

```powershell
$content = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
Set-Content -LiteralPath $scriptPath -Value $content -Encoding UTF8
```

- Markdownは`Get-Content -LiteralPath $path -Encoding UTF8`で読む。
- JSON配列はパイプライン内で直接`@(...)`に包まない。

```powershell
$jsonText = $commandOutput -join [Environment]::NewLine
$parsedItems = $jsonText | ConvertFrom-Json
$items = @($parsedItems)
if ($items.Count -eq 1 -and $items[0] -is [array]) {
    throw "JSON配列が二重化されています。"
}
```

- ID検索は一致件数が1件であることを確認する。

```powershell
$matches = @($items | Where-Object { $_.identifier -eq $targetIdentifier })
if ($matches.Count -ne 1) { throw "対象IDの一致件数が不正です。" }
```

## 書込みの検証

- バッチ処理は各コマンドの終了コードを確認し、非ゼロなら直ちに停止して対象IDを再取得する。
- 終了コード0・標準出力なしも成功条件にしない。
- 再取得後、期待件数、ID、`projectIdentifier`、状態、期限など変更対象を個別に比較する。
- 一部反映時は後続処理や同一入力の再送を行わない。
- 下書きの`postProcessingSucceeded: false`は保存済み警告である。`batchIdentifier`を使って`draft get`し、重複登録しない。

## 手動バックアップ

完全削除など、復元が難しい変更の直前に限り、UniToDoのループバックAPIでオンラインバックアップを作成する。タスク、プロジェクト、作業時間の操作にはこのAPIを使わない。

```powershell
$backupResult = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:48120/api/v1/backup"
if ([string]::IsNullOrWhiteSpace([string]$backupResult.backupPath)) {
    throw "UniToDoの手動バックアップを確認できませんでした。"
}
```

APIが失敗した場合は破壊的な変更を実行しない。返されたパスのDB内容を直接開いたり、Gitや文書へコピーしたりしない。

## プロジェクト解決

`prepare`の`resolved`だけを自動採用する。`ambiguous`または`not_found`では低確信候補をユーザーへ示す。候補不足時は`project list --json`と`list --status 実行中 --json`を参照し、確認後だけ別名登録する。

各ユーザー依頼の開始時に前回のプロジェクトID、親ID、対象IDを破棄する。現在の依頼で指定された表記を毎回`prepare`し、解決前に書き込まない。既存タスクの前後へ追加する場合は`list --project PROJECT-ID --json`で対象を検索し、一致件数が1件で、対象の`projectIdentifier`が解決済みIDと一致することを登録前に確認する。

期限未指定は期限なしではない。期限を述べられていない新規タスクは`deadlineOrigin: "auto"`にし、`prepare`が返した既定期限との一致を登録後に確認する。`none`と`--no-deadline`は、ユーザーが期限なしを明示した場合だけ使う。

相対期限の基準は次の順で選び、絶対日時へ変換して`explicit`で登録する。

1. ユーザーが示した基準日時
2. プロジェクトに既定期限があれば`prepare.nextDefaultDeadlineAt`
3. プロジェクトなし、または既定期限なしなら現在のローカル日時

「来週」は選んだ基準の7日後とする。既定期限が`2026-08-04 20:00`なら`2026-08-11 20:00`、基準が現在日時なら現在日時の7日後である。明示された曜日・時刻・期限種別を優先し、日時を一意に決められなければ登録前に確認する。

## タスクの部分更新

`update ID --file changes.json --json`は指定項目だけを更新する。省略は現状維持、`null`はnullable項目の解除、`[]`は配列の解除。名称の空白、未知・重複項目、不正な型、ID変更は保存前に拒否する。

締切だけを変えるJSON例：`{"deadlineAt":"2026-09-10T18:00:00+09:00","deadlineOrigin":"explicit"}`。期限種別を省略すると現在の種別を維持する。`deadlineAt`だけでも由来は自動的に`explicit`になる。期限解除を指示された場合は`{"deadlineAt":null}`を使う。

各件の更新前後を`list --json`で読み、ID、名称、プロジェクトID、状態、変更項目を比較する。複数件は各件保存で、全件まとめたロールバックはない。完了・中止のタスクは、変更対象として明示されない限り期限切れ一括変更から除外する。

## 新規登録用の最小タスクJSON

```json
{
  "identifier": "",
  "projectIdentifier": "PROJECT-ID",
  "category": "仕事",
  "title": "成果物を確認する",
  "status": "実行可能",
  "deadlineAt": null,
  "deadlineType": "目安",
  "deadlineOrigin": "auto",
  "estimatedMinutes": 30,
  "remainingMinutes": 30,
  "importance": 3,
  "dependencyIdentifiers": [],
  "requiredContext": "PC",
  "completionCondition": "確認結果が記録されている"
}
```

状態は`受信箱`、`下書き`、`要確認`、`実行可能`、`実行中`、`待機中`、`完了`、`中止`。期限種別は`厳守`、`目安`、`なし`。

## 最小下書きJSON

```json
{
  "parentIdentifier": "PARENT-ID",
  "title": "長期タスクの分解",
  "idempotencyKey": "PROJECT-DECOMPOSE-V1",
  "tasks": [
    {
      "identifier": "",
      "projectIdentifier": "PROJECT-ID",
      "parentIdentifier": "PARENT-ID",
      "title": "最初の作業",
      "status": "下書き",
      "estimatedMinutes": 45,
      "remainingMinutes": 45,
      "importance": 3,
      "dependencyIdentifiers": [],
      "completionCondition": "成果が保存されている",
      "aiReferenceKey": "WORK-01"
    }
  ]
}
```

`aiReferenceKey`は必須かつバッチ内一意にする。下書き間の依存はこのキーで指定し、承認はローカル画面で行う。
