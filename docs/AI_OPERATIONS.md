# Codexからのタスク操作

日常操作には `taskctl` を使用します。規則と例は次の2箇所に集約しています。

- [タスク操作Skill](../.agents/skills/manage-local-tasks/SKILL.md)：実行経路、対象・プロジェクトの確認、書込み検証、下書き、データ保護。
- [taskctl参照](../.agents/skills/manage-local-tasks/references/commands.md)：コマンド、JSON例、期限の解決、PowerShellの注意、バックアップ。

CodexはSkillで指定する実行経路を使います。開発者が開発ツリーからCLIを手動実行する場合は `./scripts/task.ps1 now --json` を使えます。個別オプションは `taskctl <コマンド> --help`、実装の契約は[画面・API・CLI](TaskManager-Vault/05_画面_API_CLI.md)を参照してください。
