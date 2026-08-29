# Codexからのタスク操作

日常操作はSQLiteを直接編集せず、`taskctl`だけを使用します。開発ツリーでは以下のラッパーが利用できます。

```powershell
.\scripts\task.ps1 now --json
```

## コマンド

```text
taskctl now [--json]
taskctl list [--status 状態] [--search 文字列] [--json]
taskctl add --title 名称 [--project IDまたは表記] [--minutes 30] [--deadline 日時] [--importance 3]
taskctl add --file task.json [--project IDまたは表記] [--no-deadline]
taskctl update TASK-ID --file task.json
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

## 安全規則

- 通常は削除せず`cancel`を使います。
- 完全削除はユーザーがIDを明示した場合だけ、同じIDを`--confirm`へ指定します。
- 下書き、要確認、依存未完了のタスクは開始できません。
- CLIエラーをDB直接編集で回避しません。
