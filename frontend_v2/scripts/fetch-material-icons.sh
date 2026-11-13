#!/usr/bin/env bash
# Fetch the Material Symbols Outlined icon font from Google Fonts and subset it
# to only the icons the HUD uses.
#
# Why subset: the full variable font is ~4 MB; the HUD needs a handful of
# glyphs, so the subset is a couple of KB and is committed to the repo
# (frontend/vendor/material-symbols/) - the client builds and runs offline, and
# the icons are loaded with the FontFace API (see frontend/src/icons.js), so
# there is no CSS step in the webpack build.
#
# The icon list lives in ONE place: frontend/icons.json (single source of
# truth).  src/icons.js derives every glyph string from it, so nothing else has
# to be kept in sync.  Re-run this script whenever you add an icon there.
#
# Requirements: curl, uv (for a throwaway fonttools environment), python3.

set -euo pipefail
cd "$(dirname "$0")/.."

FAMILY="Material+Symbols+Outlined:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200"
# A modern UA makes Google Fonts return the full font as a single file
# instead of unicode-range slices.
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
OUT="vendor/material-symbols"
ICONS_JSON="icons.json"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> Reading icons from $ICONS_JSON"
UNICODES="$(python3 - "$ICONS_JSON" <<'EOF'
import json, sys
icons = json.load(open(sys.argv[1]))
print(",".join("U+" + i["codepoint"] for i in icons))
EOF
)"

echo "==> Resolving font URL from Google Fonts CSS"
CSS="$(curl -fsSL -A "$UA" "https://fonts.googleapis.com/css2?family=$FAMILY")"
URL="$(printf '%s' "$CSS" | grep -oP 'url\(\K[^)]+' | head -1)"
[ -n "$URL" ] || { echo "error: could not extract font URL"; exit 1; }
echo "    $URL"

echo "==> Downloading full variable font"
curl -fsSL "$URL" -o "$TMP/full.woff2"

echo "==> Subsetting to $(printf '%s' "$UNICODES" | tr ',' '\n' | grep -c .) icons"
uv run --with fonttools --with brotli pyftsubset \
  "$TMP/full.woff2" \
  --unicodes="$UNICODES" \
  --layout-features='' \
  --flavor=woff2 \
  --output-file="$TMP/subset.woff2"

mkdir -p "$OUT"
mv "$TMP/subset.woff2" "$OUT/material-symbols-outlined.woff2"

echo "==> Fetching Apache-2.0 license text"
curl -fsSL "https://www.apache.org/licenses/LICENSE-2.0.txt" -o "$OUT/LICENSE"

echo "==> Done:"
ls -l "$OUT"
