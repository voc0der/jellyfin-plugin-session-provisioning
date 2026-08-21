#!/usr/bin/env bash
# Builds the installable archive. The archive holds only this plugin's own
# assembly plus meta.json; Jellyfin's assemblies are compile-time references and
# must not ship.
#
# VERSION may be overridden by CI: VERSION=1.0.0.7 ./build.sh
set -euo pipefail

cd "$(dirname "$0")"

VERSION="${VERSION:-$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)}"
SERVER="$(grep -oP '(?<=<JellyfinPackageVersion>)[^<]+' Directory.Build.props | head -1)"
ABI="$(grep -oP '(?<=<JellyfinTargetAbi>)[^<]+' Directory.Build.props | head -1)"
PLUGIN_NAME="SessionProvisioning"
PROJECT="Jellyfin.Plugin.SessionProvisioning/Jellyfin.Plugin.SessionProvisioning.csproj"
OUT="artifacts"

rm -rf "$OUT"
mkdir -p "$OUT"

dotnet test --nologo -v q

# The archive contains a single top-level folder, which is what Jellyfin's
# installer extracts into the plugins directory.
folder="${PLUGIN_NAME}_${VERSION}_jellyfin-${SERVER}"
staging="$OUT/staging/$folder"

echo
echo "==> Jellyfin $SERVER (abi $ABI)"

dotnet publish "$PROJECT" -c Release -o "$staging" -p:Version="$VERSION" --nologo -v q

# Anything from the host is a packaging bug, not a dependency: a second copy of a
# Jellyfin assembly would give the plugin its own copy of types the server owns.
strays="$(find "$staging" -maxdepth 1 -name '*.dll' ! -name 'Jellyfin.Plugin.SessionProvisioning.dll')"
if [ -n "$strays" ]; then
    echo "ERROR: host assemblies leaked into the plugin archive:" >&2
    echo "$strays" >&2
    exit 1
fi

if [ ! -f "$staging/meta.json" ]; then
    echo "ERROR: meta.json was not generated." >&2
    exit 1
fi

# meta.json is assembled from MSBuild items, where a semicolon in any value is an
# item separator: it splits the line in two and produces JSON Jellyfin refuses to
# deserialize. The archive is the only place that is visible, so check the archive.
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$staging/meta.json" || {
    echo "ERROR: meta.json is not valid JSON." >&2
    cat "$staging/meta.json" >&2
    exit 1
}

grep -q "\"targetAbi\": \"$ABI\"" "$staging/meta.json" || {
    echo "ERROR: meta.json targetAbi is not $ABI." >&2
    cat "$staging/meta.json" >&2
    exit 1
}

grep -q "\"version\": \"$VERSION\"" "$staging/meta.json" || {
    echo "ERROR: meta.json version is not $VERSION." >&2
    cat "$staging/meta.json" >&2
    exit 1
}

find "$staging" -maxdepth 1 ! -name '*.dll' ! -name 'meta.json' ! -path "$staging" -delete

archive="${folder}.zip"
(cd "$OUT/staging" && zip -qr "../$archive" "$folder")
rm -rf "$OUT/staging"

echo "    $OUT/$archive"
unzip -l "$OUT/$archive" | sed 's/^/    /'

echo
echo "Install: unzip into <jellyfin data>/plugins/ and restart."
