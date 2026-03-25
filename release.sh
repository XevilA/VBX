#!/bin/bash
# ╔═══════════════════════════════════════════════════╗
# ║  VBX Release Script — Tag + Push + GitHub Release ║
# ╚═══════════════════════════════════════════════════╝
set -e

# Colors
RED='\033[0;31m'
GRN='\033[0;32m'
YEL='\033[1;33m'
NC='\033[0m'

# Get current version from latest tag
CURRENT=$(git describe --tags --abbrev=0 2>/dev/null || echo "v0.0.0")
echo -e "${YEL}Current version: ${CURRENT}${NC}"

# Parse version
IFS='.' read -r MAJOR MINOR PATCH <<< "${CURRENT#v}"

# Menu
echo ""
echo "Release type:"
echo "  1) Patch  (bug fix)       → v${MAJOR}.${MINOR}.$((PATCH+1))"
echo "  2) Minor  (new feature)   → v${MAJOR}.$((MINOR+1)).0"
echo "  3) Major  (breaking)      → v$((MAJOR+1)).0.0"
echo "  4) Custom (enter version)"
echo ""
read -p "Choose [1-4]: " CHOICE

case $CHOICE in
    1) NEW="v${MAJOR}.${MINOR}.$((PATCH+1))" ;;
    2) NEW="v${MAJOR}.$((MINOR+1)).0" ;;
    3) NEW="v$((MAJOR+1)).0.0" ;;
    4) read -p "Enter version (e.g. v2.0.0): " NEW ;;
    *) echo -e "${RED}Invalid choice${NC}"; exit 1 ;;
esac

echo ""
echo -e "${GRN}New version: ${NEW}${NC}"
echo ""

# Show recent commits since last tag
echo -e "${YEL}Changes since ${CURRENT}:${NC}"
git log ${CURRENT}..HEAD --oneline 2>/dev/null || git log --oneline -5
echo ""

read -p "Confirm release ${NEW}? [y/N]: " CONFIRM
if [[ "$CONFIRM" != "y" && "$CONFIRM" != "Y" ]]; then
    echo "Cancelled."
    exit 0
fi

# Stage, commit if needed
if [[ -n $(git status --porcelain) ]]; then
    echo -e "${YEL}Uncommitted changes found — committing...${NC}"
    git add -A
    read -p "Commit message: " MSG
    git commit -m "${MSG}"
fi

# Push
echo -e "${YEL}Pushing to remote...${NC}"
git push --force

# Tag
echo -e "${YEL}Creating tag ${NEW}...${NC}"
git tag -f "${NEW}"
git push origin "${NEW}" --force

echo ""
echo -e "${GRN}═══════════════════════════════════════${NC}"
echo -e "${GRN}  ✓ Released ${NEW} successfully!${NC}"
echo -e "${GRN}  GitHub Actions will build the .exe${NC}"
echo -e "${GRN}═══════════════════════════════════════${NC}"
