#!/usr/bin/env bash
# Build the plugin and emit a Jellyfin plugin-repository feed: a versioned zip
# plus a repo.json referencing it (v12 format: PackageInfo[] with camelCase keys
# and MD5 checksums — verified against Jellyfin's InstallationManager).
#
# Usage: packaging/build-release.sh <base-url> [out-dir]
#   <base-url>  where the out-dir contents will be hosted, e.g.
#               https://raw.githubusercontent.com/<you>/<repo>/<branch>/dist
#               https://example.com/jellyfin
set -euo pipefail

BASE_URL="${1:?usage: build-release.sh <base-url> [out-dir]}"
BASE_URL="${BASE_URL%/}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${2:-$ROOT/dist}"
CSPROJ="$ROOT/Jellyfin.Plugin.NineNow/Jellyfin.Plugin.NineNow.csproj"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' "$CSPROJ")
TARGET_ABI=$(grep -oPm1 '(?<=PackageReference Include="Jellyfin.Controller" Version=")[^"]+' "$CSPROJ")
PLUGIN_NAME="Nine Network (9Now)"
GUID="343f098f-4087-425d-a3a2-182e50992673"
ZIP="nine-network_${VERSION}.zip"

"$DOTNET" build -c Release "$ROOT/Jellyfin.Plugin.NineNow" --nologo -v q
DLL="$ROOT/Jellyfin.Plugin.NineNow/bin/Release/net10.0/Jellyfin.Plugin.NineNow.dll"
test -f "$DLL"

mkdir -p "$OUT"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
# v12 extracts the zip into plugins/<package.Name>_<version>/; only the dll goes in.
cp "$DLL" "$STAGE/"
(cd "$STAGE" && zip -q "$OUT/$ZIP" "$(basename "$DLL")")

CHECKSUM=$(md5sum "$OUT/$ZIP" | cut -d' ' -f1)
TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)

python3 - "$OUT/repo.json" <<EOF
import json, sys

repo = [{
    "name": "$PLUGIN_NAME",
    "description": "Nine Network live TV for Jellyfin. Pulls channels, stream URLs, EPG and artwork live from the 9Now service, served to Jellyfin via its built-in M3U tuner and XmlTV provider. One-click tuner + guide registration on the plugin settings page. Australian viewers only.",
    "overview": "Nine Network (Channel 9, 9Gem, 9Go!, 9Life, 9Rush + FAST channels) live in the Jellyfin UI with guide data, logos and show posters.",
    "owner": "cuey",
    "category": "General",
    "guid": "$GUID",
    "versions": [{
        "version": "$VERSION",
        "changelog": "-",
        "timestamp": "$TIMESTAMP",
        "targetAbi": "$TARGET_ABI.0",
        "sourceUrl": "$BASE_URL/$ZIP",
        "checksum": "$CHECKSUM"
    }]
}]
json.dump(repo, open(sys.argv[1], "w"), indent=2)
EOF

echo "built $PLUGIN_NAME $VERSION (abi $TARGET_ABI.0)"
echo "  zip    : $OUT/$ZIP  (md5 $CHECKSUM)"
echo "  feed   : $OUT/repo.json -> $BASE_URL/$ZIP"
