# ローカルGit管理

## 目的と前提

作成者の開発ワークスペースはローカルGitで管理します。ソース、テスト、運用スクリプト、設計資料を同じ変更単位で記録し、実データや端末固有情報は履歴へ入れません。

リモートリポジトリへの送信や公開は前提にしません。リモートを追加する場合は、公開範囲と資格情報を別途確認します。

## 追跡するもの

| 場所 | 内容 |
|---|---|
| `.agents/` | このリポジトリを安全に操作するCodex Skillと設定 |
| `src/`、`tests/` | アプリ、CLI、テスト、プロジェクト定義、lockファイル、正規の画像・Web資産 |
| `scripts/`、`integrations/` | ビルド、テスト、発行、導入、頒布、外部連携のソース |
| `docs/` | AI操作ガイド、設計Vault、共有に必要なObsidian設定 |
| ルート文書 | `AGENTS.md`、`README.md`、`最初にお読みください.txt` |
| ルート構成 | `.gitignore`、`Directory.Build.props`、`global.json`、`TaskManager.slnx`、`Install.ps1` |

Vaultでは`.obsidian/app.json`と`.obsidian/.gitignore`を追跡します。リンク形式は共有しますが、ワークスペース、外観、有効プラグイン、キャッシュ、ホットキーは個人設定として除外します。

## 除外するもの

- `.codex/`の下書き、`.dotnet/`のローカルSDK、`.vs/`、`.idea/`、`.vscode/`などの端末固有設定
- `artifacts/`、`bin/`、`obj/`、`TestResults/`、coverage、ログ、一時ファイルなどの生成物
- `data/`、SQLite本体、WAL、SHM、journal、バックアップ
- `credentials.json`、OAuthトークン、環境変数ファイル、秘密鍵・証明書秘密鍵
- Obsidianの`workspace*.json`、`appearance.json`、`core-plugins.json`、ローカルプラグインとキャッシュ
- `%LOCALAPPDATA%\TaskManager`にある正本データ、設定、ログ、インストール済み実行物

除外対象のサンプルや公開鍵をテスト資産として追加する必要が生じた場合は、対象を確認してから`.gitignore`へ狭い例外を追加します。

## ステージとコミット

1. 変更前に`git status --short --ignored`で未追跡物と除外物を確認する。
2. 初回登録では管理対象のルートとファイルを明示して`git add`する。ワークスペース全体を無検査で登録しない。
3. `git diff --cached --name-status`と`git diff --cached --stat`でステージ済み一覧を確認する。
4. DB、バックアップ、資格情報、トークン、個人設定、生成物、大容量バイナリがないことをファイル名と内容の両面で検査する。
5. `scripts/test.ps1`を実行し、実装とVaultが一致した状態で変更単位ごとにコミットする。

秘密情報を誤ってコミットした場合は、ファイルを削除するだけで済ませず、該当する資格情報を失効・再発行してから履歴を処置します。

## 頒布との境界

`.gitignore`はGitへの登録を防ぐだけで、手動ZIPへの混入は防ぎません。頒布には必ず`scripts/create-distribution.ps1`のallowlist方式と個人データ検査を使用し、`.git/`とローカル履歴を含めません。詳細は[頒布と受取人セットアップ](08_頒布と受取人セットアップ.md)を参照します。

## 受入基準

- 追跡一覧にDB、バックアップ、資格情報、トークン、個人設定、生成物がない。
- ソース、テスト、スクリプト、Skill、設計資料、再現に必要な設定が追跡されている。
- 変更後に`git status`で意図しない未追跡ファイルが残らない。
- クリーンなチェックアウトから.NET 10環境で`scripts/test.ps1`を実行できる。
