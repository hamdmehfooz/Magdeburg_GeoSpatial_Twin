#!/usr/bin/env bash
#
# Shrink this repository so it can be pushed to GitHub.
#
# GitHub hard-rejects any single file over 100 MB and blocks pushes over 2 GB.
# This repository is 5.71 GiB packed with eleven files over that limit, so it
# cannot be pushed as it stands.
#
# THIS SCRIPT REWRITES HISTORY. Every commit hash changes. Nothing has been
# pushed yet, so no one else is affected — but make the backup in step 1 anyway.
#
# Usage:  bash tools/clean-history.sh          # dry run, prints what would go
#         bash tools/clean-history.sh --apply  # actually rewrite
#
set -euo pipefail

APPLY=0
[ "${1:-}" = "--apply" ] && APPLY=1

cd "$(dirname "$0")/.."
REPO="$(pwd)"

# --------------------------------------------------------------------------
# TIER 1 — Unity build output. Regenerable; always safe to remove.
# About 2.7 GB. A build was written into Results3/ instead of a Build/ folder.
# --------------------------------------------------------------------------
TIER1=(
  "Results3/IMIQ_Data"
  "Results3/MonoBleedingEdge"
  "Results3/D3D12"
  "Results3/IMIQ.exe"
  "Results3/UnityPlayer.dll"
  "Results3/UnityCrashHandler64.exe"
  "Results3/WinPixEventRuntime.dll"
)

# --------------------------------------------------------------------------
# TIER 2 — large binary sources. NOT regenerable. Remove these only once they
# are archived somewhere you can retrieve them (university storage, Zenodo,
# an external drive). Comment out any group you want to keep.
# --------------------------------------------------------------------------
TIER2=(
  "Assets/External_Models"              # ~1.9 GB  .glb / .fbx station models
  "Assets/GTFS/stop_times.txt"          # 2.5 GB   transit feed
  "Assets/GTFS/shapes.txt"              # 287 MB
  "Assets/GTFS/trips.txt"               # 169 MB
  "Assets/GTFS/stops.txt"               # 56 MB
  "Assets/GTFS/transfers.txt"           # 51 MB
  "Assets/TerrainSampleAssets/Textures" # ~240 MB  .tif normal maps
)

echo "Repository: $REPO"
echo "Current packed size: $(git count-objects -vH | awk '/size-pack/{print $2, $3}')"
echo

# pip installs the module but does not always put the git-filter-repo shim on
# PATH, so fall back to invoking the module directly.
if command -v git-filter-repo >/dev/null 2>&1; then
  FR=(git filter-repo)
elif python -c "import git_filter_repo" >/dev/null 2>&1; then
  FR=(python -m git_filter_repo)
else
  cat <<'EOF'
git-filter-repo is not installed. Install it with:

    pip install git-filter-repo

It is the tool the Git project itself recommends; git filter-branch is
deprecated and orders of magnitude slower on a repository this size.
EOF
  exit 1
fi

ARGS=()
echo "TIER 1 — build output (safe):"
for p in "${TIER1[@]}"; do echo "  - $p"; ARGS+=(--path "$p"); done
echo
echo "TIER 2 — large binary sources (archive these first):"
for p in "${TIER2[@]}"; do echo "  - $p"; ARGS+=(--path "$p"); done
echo

if [ "$APPLY" -eq 0 ]; then
  cat <<'EOF'
Dry run only — nothing changed.

Before running with --apply:
  1. Back up:   git clone --mirror . ../vr-backup.git
  2. Archive anything in TIER 2 you still need.
  3. Re-run:    bash tools/clean-history.sh --apply

EOF
  exit 0
fi

echo ">>> Rewriting history. This takes several minutes."
"${FR[@]}" --invert-paths "${ARGS[@]}" --force

echo ">>> Repacking..."
git reflog expire --expire=now --all
git gc --prune=now --aggressive

echo
echo "New packed size: $(git count-objects -vH | awk '/size-pack/{print $2, $3}')"
echo
echo "Remaining files over 100 MB (GitHub will still reject these):"
git rev-list --objects --all \
  | git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' \
  | awk '$1=="blob" && $3 > 104857600 {printf "  %.1f MB  %s\n", $3/1048576, substr($0, index($0,$4))}' \
  | sort -rn

cat <<'EOF'

filter-repo removes the remote as a safety measure. Restore it with:

    git remote add origin https://github.com/hamdmehfooz/Magdeburg_GeoSpatial_Twin.git

NOTE: Assets/Scenes/SmartMobilityCore.unity is 287 MB and is NOT removed by
this script — you need it. It exceeds GitHub's 100 MB limit, so it requires
either Git LFS, or reducing the scene itself (see tools/README-cleanup.md).
EOF
