# Antigravity General Project Rules
- `always_on`: **Single Source of Truth (SSoT)**. プロジェクトの仕様、アーキテクチャ、データ保護規則、コーディング規約はすべてルートの `AGENTS.md` を唯一の正本とする。作業開始前に必ず `AGENTS.md` を精読し、その記載内容に厳格に従うこと。
- `always_on`: **Local-First & Zero Cloud Costs**. GitHub Actionsや外部クラウドランナーは使用しない。すべての検証はローカルの `scripts/verify.sh` を通じて行う。
- `always_on`: **Data Evaporation Prevention**. SQLite DBやOAuthトークンなどの永続データは、Git作業ツリーの外部（`~/.unitodo/` 等）または `.git` が存在しないポータブルフォルダにのみ保存し、`git clean -fdx` で絶対に消失させない。
- `always_on`: **Loopback Security Boundary**. Webサーバーの待受先は `127.0.0.1:48120` を厳守し、`Sec-Fetch-Site: cross-site` リクエストは無条件で拒否する。

# Feature Completion Process (Review Pipeline)
- `trigger: model_decision` (Trigger when declaring a feature complete or finalizing implementation)
- **Local CI First**: 作業完了を宣言する前に、必ず `./scripts/verify.sh` を実行してパス（Exit Code 0）させること。失敗した場合は即座に修正すること。
- **Bypass Criteria (レッドチームスキップ条件)**:
  以下の軽微な変更のみである場合は、`verify.sh` の通過をもって完了とし、レッドチーム監査をスキップできる：
  1. Markdown / Vault設計ノート等のドキュメントのみの変更
  2. 画像やCSS調整などロジックに無関係な静的アセットのみの変更
  3. 明らかなTypo修正・日本語コメント追加のみの変更
- **Mandatory Red Team Review**:
  バイパス条件に該当しない機能追加・Core改修・DBスキーマ変更・セキュリティ変更の場合、完了宣言前に必ず `invoke_subagent` で `red-team` スキルを持つサブエージェントを起動して差分を監査させること。
- **Circuit Breaker (サーキットブレーカー)**:
  - `red_team_report.md` はリポジトリ内やVault内には出力せず、Antigravityアーティファクト領域にのみ出力すること。
  - レビュー指摘の修正・再レビューの往復は**最大2回**までとする。2回往復しても重大な指摘が解消しない場合は、AIによる自動解決を停止し、直ちに人間にエスカレーションすること。
