#!/usr/bin/env bash
# ==============================================================================
# Git pre-commit フックのインストールスクリプト
# コミット前にシークレット混入や禁止連携コードを自動検出・物理遮断する。
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
GITHOOKS_DIR="${PROJECT_ROOT}/.githooks"
HOOK_FILE="${GITHOOKS_DIR}/pre-commit"

mkdir -p "${GITHOOKS_DIR}"

cat << 'EOF' > "${HOOK_FILE}"
#!/usr/bin/env bash
# ==============================================================================
# UniToDo pre-commit hook
# ==============================================================================
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
VERIFY_SCRIPT="${REPO_ROOT}/scripts/verify.sh"

if [[ -f "${VERIFY_SCRIPT}" ]]; then
    exec "${VERIFY_SCRIPT}" --pre-commit
fi
exit 0
EOF

chmod +x "${HOOK_FILE}"

# git config core.hooksPath を設定してワークツリーや別環境でも確実にフックを有効化
git -C "${PROJECT_ROOT}" config core.hooksPath .githooks

echo "✔ pre-commit フックを正常にインストールしました: ${HOOK_FILE}"
echo "✔ git config core.hooksPath を .githooks に設定しました。"
