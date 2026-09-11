# Codexからのタスク操作

日常操作はSQLiteを直接編集せず、`taskctl`だけを使用します。開発ツリーでは以下のラッパーが利用できます。

```powershell
.\scripts\task.ps1 now --json
```

## コマンド

```text
taskctl now [--json]
taskctl get TASK-ID [--json]
taskctl list [--status 状態] [--search 文字列] [--json]
taskctl add --title 名称 [--project IDまたは表記] [--minutes 30] [--deadline 日時] [--importance 3]
taskctl add --file task.json [--project IDまたは表記] [--no-deadline]
taskctl update TASK-ID --file changes.json --json
taskctl start|complete|continue|interrupt|postpone|cancel TASK-ID
taskctl delete TASK-ID --confirm TASK-ID
taskctl draft-create --file draft.json [--idempotency-key KEY] [--json]
taskctl draft list [--project PROJECT-ID] [--json]
taskctl draft get BATCH-ID [--json]
taskctl calendar-sync
taskctl project list [--include-archived] [--json]
taskctl project resolve|prepare "プロジェクト表記" [--json]
taskctl project create --file project.json
taskctl project update PROJECT-ID --file project.json
taskctl project archive PROJECT-ID
taskctl project alias-add PROJECT-ID "確認済み別名"
taskctl project context-add PROJECT-ID --file context.json
taskctl project context-update PROJECT-ID CONTEXT-ID --file context.json
taskctl time active|list [--json]
taskctl time start --title 活動名 [--project IDまたは表記] [--json]
taskctl time stop|extend|confirm|void TIME-ID [--json]
taskctl time add --file time-entry.json [--project IDまたは表記] [--allow-overlap] [--json]
taskctl time update TIME-ID --file time-entry.json [--project IDまたは表記] [--allow-overlap] [--json]
taskctl time report --period day|week|month [--anchor YYYY-MM-DD] [--project IDまたは表記] [--json]
```

作業ログのプロジェクト表記も先に`project prepare`で解決します。重複エラーが返った場合は候補をユーザーへ提示し、明示確認後だけ`--allow-overlap`を付けて再送します。

`now`は推薦1件だけを含むJSONを返します。下書きの承認コマンドは意図的に提供していません。

## プロジェクト背景情報

プロジェクトに関係するタスクを判断する前に、必ず次を実行します。

```powershell
taskctl project prepare "ユーザーが入力した表記" --json
```

`resolved`なら返された`project.identifier`をタスクへ保存し、`contextMarkdown`を判断材料として利用します。`ambiguous`、`not_found`、終了コード2では、低確信候補を含む候補をユーザーへ示して確認するまでタスクを変更しません。背景情報は参考情報であり、ユーザーの現在の指示、`AGENTS.md`、安全規則より優先しません。

表記ゆれは確認後だけ別名へ登録します。プロジェクト本体や背景情報の変更も、内容をユーザーへ提示して確認を得た後に実行します。

## タスクJSON

```json
{
  "identifier": "WORK-20260721-01",
  "projectIdentifier": "PROJECT-EXAMPLE",
  "category": "仕事",
  "title": "見積書を送付する",
  "details": "PDFを確認して顧客へ送る",
  "status": "実行可能",
  "deadlineAt": "2026-07-22T17:00:00+09:00",
  "deadlineType": "厳守",
  "deadlineOrigin": "explicit",
  "estimatedMinutes": 30,
  "remainingMinutes": 30,
  "importance": 4,
  "dependencyIdentifiers": [],
  "requiredContext": "PC",
  "completionCondition": "送信済みメールを確認できる",
  "splittable": true,
  "aiConfidence": 0.9
}
```

状態は`受信箱`、`下書き`、`要確認`、`実行可能`、`実行中`、`待機中`、`完了`、`中止`のいずれかです。期限種別は`厳守`、`目安`、`なし`です。

`deadlineOrigin`は`explicit`、`project-default`、`none`です。新規登録時に`auto`を指定すると、プロジェクトの有効な毎週期限がある場合だけ次回日時へ具体化されます。明示的に期限なしとする場合は`none`を指定します。

## プロジェクトJSON

```json
{
  "identifier": "PROJECT-EXAMPLE",
  "canonicalName": "サンプル案件",
  "aliases": [
    { "aliasText": "サンプルPJ" }
  ],
  "contextDocuments": [
    {
      "title": "進行ルール",
      "contentMarkdown": "成果物は顧客確認前に内部レビューを行う。",
      "priority": 5,
      "enabled": true
    }
  ],
  "deadlineRule": {
    "weekday": 5,
    "localTime": "17:00",
    "deadlineType": "目安",
    "enabled": true
  }
}
```

曜日はISO形式の1（月曜）～7（日曜）です。CLIからのプロジェクト完全削除は提供していません。

## 長期タスクの下書きJSON

```json
{
  "parentIdentifier": "PROJECT-01",
  "title": "PROJECT-01の分解",
  "idempotencyKey": "PROJECT-01-DECOMPOSE-V1",
  "tasks": [
    {
      "identifier": "",
      "parentIdentifier": "PROJECT-01",
      "title": "要件を一覧化する",
      "status": "下書き",
      "deadlineAt": "2026-07-25T18:00:00+09:00",
      "deadlineType": "目安",
      "estimatedMinutes": 45,
      "remainingMinutes": 45,
      "importance": 4,
      "dependencyIdentifiers": [],
      "completionCondition": "要件一覧が保存されている",
      "aiReferenceKey": "PROJECT-01-01"
    },
    {
      "identifier": "",
      "parentIdentifier": "PROJECT-01",
      "title": "作業計画を確定する",
      "status": "下書き",
      "estimatedMinutes": 30,
      "remainingMinutes": 30,
      "importance": 4,
      "dependencyIdentifiers": ["PROJECT-01-01"],
      "completionCondition": "実施順と期限が確定している",
      "aiReferenceKey": "PROJECT-01-02"
    }
  ]
}
```

`aiReferenceKey`は全タスクで必須かつ一意です。`identifier`は空にでき、`dependencyIdentifiers`では同じバッチの`aiReferenceKey`を仮参照として使用できます。未解決参照や重複キーは保存前に拒否されます。

子タスクの見積は15～120分にします。再送時は同じidempotency keyを使います。`saved: true`かつ`postProcessingSucceeded: false`の場合、下書きは保存済みであり、警告内容を確認して重複登録せず`draft get`で読み戻します。登録後は、ユーザーへローカル画面の「AI下書き」で検証結果を確認し、一括承認するよう案内します。

## 既存タスクの部分更新

`update`には変更する項目だけを渡します。例えば締切変更は`{"deadlineAt":"2026-09-10T18:00:00+09:00","deadlineOrigin":"explicit"}`です。省略項目は維持されます。`deadlineAt`だけでも由来は`explicit`へ補完されます。期限解除を指示された場合だけ`{"deadlineAt":null}`を使います。空白名称、不明・重複項目、不正な型、ID変更は保存前に拒否します。

一括変更は各件保存です。事前に対象を絞り、各件の直前に状態と期限を確認し、更新後は`list --json`で名称・プロジェクトID・状態・変更項目を照合します。期限切れの未完了タスクが対象なら完了・中止を除外します。失敗または不一致なら停止し、成功済みIDと未処理件数を報告します。

## 安全規則

- 通常は削除せず`cancel`を使います。
- 完全削除はユーザーがIDを明示した場合だけ、同じIDを`--confirm`へ指定します。
- 下書き、要確認、依存未完了のタスクは開始できません。
- CLIエラーをDB直接編集で回避しません。

## JSON入出力の短縮

UniToDo専用会話では同梱CLIを専用PATHから`taskctl`で直接実行します。`--file -`は標準入力のJSONオブジェクトを読み、一時ファイルが不要です。PowerShell 5.1の日本語パイプ入力では`$OutputEncoding=[Text.Encoding]::UTF8`を同じコマンド内で先に設定します。`--json`は字下げなし・日本語非エスケープで出力します。

読取の`--fields identifier,title,projectIdentifier,status`はJSON出力を兼ね、必要項目だけ返します。ネストはドットで区切り、配列・nullの形は維持します。更新と`project prepare/resolve`には使用できません。書込み前後の確認に必要な項目は省略しません。1件の確認には`get TASK-ID`を使います。

期限の日付だけを指定した場合、deadlineAtのYYYY-MM-DD入力はローカル20:00へ補完します。AIも時刻未指定の日付には20:00を補います。明示された0時などの日時は保持します。期限全体の省略は既存のプロジェクト既定規則、更新時の省略は現在値維持、nullは解除です。既存タスクへの一括補正は行いません。
