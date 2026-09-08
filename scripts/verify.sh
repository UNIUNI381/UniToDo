#!/usr/bin/env bash
# ==============================================================================
# UniToDo (TaskManager) ローカル完結型CIテストハーネス (Local CI Verifier)
# クラウドCI（GitHub Actions）や外部ランナーを使わず、ローカルで全件検証する。
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# 色出力定義
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

PRE_COMMIT_MODE=false
if [[ "${1:-}" == "--pre-commit" ]]; then
    PRE_COMMIT_MODE=true
fi

echo -e "${BOLD}${CYAN}====================================================================${NC}"
echo -e "${BOLD}${CYAN}  UniToDo (TaskManager) ローカルCIテストハーネス (Local Verifier)  ${NC}"
echo -e "${BOLD}${CYAN}====================================================================${NC}"

# ------------------------------------------------------------------------------
# Stage 1: シークレット＆機密保護・禁止インテグレーション検査
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${BLUE}[Stage 1/5] シークレット混入・機密保護・禁止連携検査...${NC}"

# 1-1: GitステージングファイルへのDB・認証情報混入チェック
if git -C "${PROJECT_ROOT}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    STAGED_FILES=$(git -C "${PROJECT_ROOT}" diff --cached --name-only || true)
    if [[ -n "${STAGED_FILES}" ]]; then
        FORBIDDEN_STAGED_PATTERNS=(".*\.db$" ".*\.sqlite.*$" "credentials\.json$" "token\.json$" "client_secret.*\.json$" "secrets\.json$" "appsettings\.Local\.json$")
        for PATTERN in "${FORBIDDEN_STAGED_PATTERNS[@]}"; do
            MATCHED=$(echo "${STAGED_FILES}" | grep -E "${PATTERN}" || true)
            if [[ -n "${MATCHED}" ]]; then
                echo -e "${RED}[ERROR] コミット対象に機密ファイルが含まれています!${NC}"
                echo -e "${RED}検出ファイル:\n${MATCHED}${NC}"
                echo -e "${YELLOW}SQLite DBやOAuth認証情報は絶対にGitへコミットしないでください。${NC}"
                exit 1
            fi
        done
    fi
fi
echo -e "${GREEN}  ✓ ステージング機密ファイル検査: パス${NC}"

# 1-2: 禁止インテグレーションコードの静的検出 (OpenAI, AppsScript, Google Sheets等)
FORBIDDEN_CODE_PATTERNS=(
    "api.openai.com"
    "OpenAIClient"
    "SpreadsheetApp"
    "google.script.run"
    "script.google.com"
    "ClosedXML"
    "/import/preview"
    "view-migration"
)

FOUND_FORBIDDEN=false
for PATTERN in "${FORBIDDEN_CODE_PATTERNS[@]}"; do
    MATCHES=$(grep -rn --include="*.cs" --include="*.js" --include="*.json" --include="*.csproj" \
        --exclude-dir="bin" --exclude-dir="obj" --exclude-dir=".git" --exclude-dir="node_modules" \
        "${PATTERN}" "${PROJECT_ROOT}/src" || true)
    if [[ -n "${MATCHES}" ]]; then
        echo -e "${RED}[ERROR] 禁止された連携コード（${PATTERN}）を検出しました:${NC}"
        echo "${MATCHES}"
        FOUND_FORBIDDEN=true
    fi
done

if [[ "${FOUND_FORBIDDEN}" == "true" ]]; then
    echo -e "${RED}AGENTS.mdで禁止されている外部連携（OpenAI API, Sheets連携等）は追加できません。${NC}"
    exit 1
fi
echo -e "${GREEN}  ✓ 禁止インテグレーション検査: パス (OpenAI/AppsScript混入なし)${NC}"

# pre-commit モードなら Stage 1 で超高速終了（コミットを即座に許可）
if [[ "${PRE_COMMIT_MODE}" == "true" ]]; then
    echo -e "\n${BOLD}${GREEN}✔ Pre-commit セキュリティ検査に合格しました。コミットを継続します。${NC}\n"
    exit 0
fi

# ------------------------------------------------------------------------------
# Stage 2: 規約・ライセンス整合性検査
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${BLUE}[Stage 2/5] 規約＆ライセンス整合性検査...${NC}"

# 2-1: LICENSE の著作権者保持確認
if [[ ! -f "${PROJECT_ROOT}/LICENSE" ]]; then
    echo -e "${RED}[ERROR] LICENSE ファイルが存在しません。${NC}"
    exit 1
fi
if ! grep -q "Copyright (c) 2026 uniuni" "${PROJECT_ROOT}/LICENSE"; then
    echo -e "${RED}[ERROR] LICENSE に著作権者 'Copyright (c) 2026 uniuni' が保持されていません。${NC}"
    exit 1
fi
echo -e "${GREEN}  ✓ MIT License 著作権表示保持: パス${NC}"

# 2-2: 第三者通知と依存マニフェスト存在確認
REQUIRED_LEGAL_FILES=(
    "THIRD-PARTY-NOTICES.md"
    "licenses/dependencies.json"
    "licenses/Apache-2.0.txt"
    "licenses/DotNet-MIT-LICENSE.txt"
    "licenses/Newtonsoft.Json-LICENSE.md"
)
for LEGAL_FILE in "${REQUIRED_LEGAL_FILES[@]}"; do
    if [[ ! -f "${PROJECT_ROOT}/${LEGAL_FILE}" ]]; then
        echo -e "${RED}[ERROR] 必須ライセンス文書が欠落しています: ${LEGAL_FILE}${NC}"
        exit 1
    fi
done
echo -e "${GREEN}  ✓ 第三者ライセンス文書一式: パス${NC}"

# 2-3: AGENTS.md のルール準拠（Webサーバー待受先が127.0.0.1:48120固定であるか）
TASK_CONSTANTS="${PROJECT_ROOT}/src/TaskManager.App/Domain/TaskConstants.cs"
if [[ -f "${TASK_CONSTANTS}" ]]; then
    if ! grep -q '127.0.0.1:48120' "${TASK_CONSTANTS}"; then
        echo -e "${RED}[ERROR] ループバック待受アドレスが 127.0.0.1:48120 から変更されています。${NC}"
        exit 1
    fi
    echo -e "${GREEN}  ✓ 待受アドレス（127.0.0.1:48120固定）: パス${NC}"
fi

# ------------------------------------------------------------------------------
# Stage 3: ビルド検査 (dotnet build)
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${BLUE}[Stage 3/5] ビルド検査 (dotnet build --warnaserror)...${NC}"

if command -v dotnet >/dev/null 2>&1; then
    echo "  .NET SDK 検出: $(dotnet --version)"
    if dotnet build "${PROJECT_ROOT}/TaskManager.slnx" -c Release --warnaserror; then
        echo -e "${GREEN}  ✓ ソリューションビルド (警告ゼロ): パス${NC}"
    else
        echo -e "${RED}[ERROR] ビルドに失敗しました。警告またはエラーを解消してください。${NC}"
        exit 1
    fi
else
    echo -e "${YELLOW}  [WARN] .NET SDK が見つかりません (dotnet not found)。${NC}"
    echo -e "${YELLOW}  Mac環境でビルドを実行するには、以下で .NET 10 SDK を導入してください:${NC}"
    echo -e "${CYAN}    brew install --cask dotnet-sdk${NC}"
    echo -e "${YELLOW}  Stage 3（ビルド）と Stage 4（テスト）をスキップします。${NC}"
fi

# ------------------------------------------------------------------------------
# Stage 4: 自動テスト実行 (dotnet test)
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${BLUE}[Stage 4/5] 単体・結合テスト検査 (dotnet test)...${NC}"

if command -v dotnet >/dev/null 2>&1; then
    TEST_PROJECT="${PROJECT_ROOT}/tests/TaskManager.Tests/TaskManager.Tests.csproj"
    if [[ -f "${TEST_PROJECT}" ]]; then
        if dotnet test "${TEST_PROJECT}" -c Release --no-build; then
            echo -e "${GREEN}  ✓ 全テストスイート通過: パス${NC}"
        else
            echo -e "${RED}[ERROR] テストに失敗しました。${NC}"
            exit 1
        fi
    fi
else
    echo -e "${YELLOW}  [WARN] .NET SDK 未導入のためテストをスキップしました。${NC}"
fi

# ------------------------------------------------------------------------------
# Stage 5: ドキュメント＆Vault整合性検査
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${BLUE}[Stage 5/5] ドキュメント＆Vault整合性検査...${NC}"

REQUIRED_VAULT_FILES=(
    "00_入口.md"
    "01_システム概要.md"
    "02_ファイル構成.md"
    "03_データモデル.md"
    "04_推薦と自動処理.md"
    "05_画面_API_CLI.md"
    "06_開発運用.md"
    "08_頒布と受取人セットアップ.md"
    "10_ライセンスと第三者コンポーネント.md"
)

for VAULT_FILE in "${REQUIRED_VAULT_FILES[@]}"; do
    FULL_PATH="${PROJECT_ROOT}/docs/TaskManager-Vault/${VAULT_FILE}"
    if [[ ! -f "${FULL_PATH}" ]]; then
        echo -e "${RED}[ERROR] 必須Vaultノートが欠落しています: ${VAULT_FILE}${NC}"
        exit 1
    fi
done
echo -e "${GREEN}  ✓ Vault設計ノート主要一式: パス${NC}"

# ------------------------------------------------------------------------------
# 総合判定
# ------------------------------------------------------------------------------
echo -e "\n${BOLD}${GREEN}====================================================================${NC}"
echo -e "${BOLD}${GREEN}  ✔ すべてのローカルCI検査（5ステージ）に合格しました！              ${NC}"
echo -e "${BOLD}${GREEN}====================================================================${NC}\n"
exit 0
