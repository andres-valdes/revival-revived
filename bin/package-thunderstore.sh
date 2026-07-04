#!/usr/bin/env bash
# Package RevivalRevived for Thunderstore.
#
# Builds the plugin in Release, stages the Thunderstore layout (manifest.json,
# README.md, CHANGELOG.md, icon.png, plugin DLL at the archive root), validates
# everything Thunderstore rejects uploads for, and produces
#   dist/RevivalRevived-<version>.zip
#
# The version in Plugin.cs (PluginVersion) is the single source of truth; the
# manifest and csproj must agree or the script fails.

set -euo pipefail
cd "$(dirname "$0")/.."

TS_DIR=thunderstore
DIST_DIR=dist
NAME=RevivalRevived

fail() { echo "package-thunderstore: ERROR: $*" >&2; exit 1; }

# --- Version: Plugin.cs is authoritative; everything must match -------------
VERSION=$(sed -n 's/.*PluginVersion = "\([0-9.]*\)".*/\1/p' Plugin.cs)
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "PluginVersion '$VERSION' in Plugin.cs is not MAJOR.MINOR.PATCH"

MANIFEST_VERSION=$(python3 -c "import json;print(json.load(open('$TS_DIR/manifest.json'))['version_number'])")
[[ "$MANIFEST_VERSION" == "$VERSION" ]] || fail "manifest.json version ($MANIFEST_VERSION) != Plugin.cs ($VERSION)"

CSPROJ_VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' $NAME.csproj)
[[ "$CSPROJ_VERSION" == "$VERSION" ]] || fail "csproj <Version> ($CSPROJ_VERSION) != Plugin.cs ($VERSION)"

# --- Validate the static package pieces -------------------------------------
[[ -f "$TS_DIR/manifest.json" && -f "$TS_DIR/README.md" && -f "$TS_DIR/icon.png" ]] \
    || fail "missing manifest.json, README.md or icon.png in $TS_DIR/"

python3 - "$TS_DIR/manifest.json" <<'EOF'
import json, re, sys
m = json.load(open(sys.argv[1]))
assert re.fullmatch(r"[A-Za-z0-9_]+", m["name"]), "manifest name must be alphanumeric/underscore"
assert len(m["description"]) <= 250, f"description is {len(m['description'])} chars (max 250)"
assert isinstance(m["dependencies"], list) and m["dependencies"], "dependencies missing"
assert "website_url" in m, "website_url key required (may be empty)"
EOF

ICON_SIZE=$(identify -format "%wx%h" "$TS_DIR/icon.png")
[[ "$ICON_SIZE" == "256x256" ]] || fail "icon.png is $ICON_SIZE, Thunderstore requires exactly 256x256"

# --- Build -------------------------------------------------------------------
echo "==> Building $NAME $VERSION (Release)"
dotnet build "$NAME.csproj" -c Release -v q

DLL="bin/Release/$NAME.dll"
[[ -f "$DLL" ]] || fail "build output $DLL not found"

# --- Stage & zip ---------------------------------------------------------
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

cp "$TS_DIR/manifest.json" "$TS_DIR/README.md" "$TS_DIR/icon.png" "$STAGE/"
[[ -f "$TS_DIR/CHANGELOG.md" ]] && cp "$TS_DIR/CHANGELOG.md" "$STAGE/"
cp "$DLL" "$STAGE/"

mkdir -p "$DIST_DIR"
OUT="$DIST_DIR/$NAME-$VERSION.zip"
rm -f "$OUT"
(cd "$STAGE" && zip -q -r - .) > "$OUT"

echo "==> Packaged $OUT"
unzip -l "$OUT"
echo "==> Upload at https://thunderstore.io/c/valheim/create/ (community: Valheim)"
