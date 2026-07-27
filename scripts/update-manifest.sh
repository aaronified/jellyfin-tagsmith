#!/usr/bin/env bash
# Adds (or replaces) a version entry in manifest.json — the repository file Jellyfin
# polls to offer in-dashboard updates.
#
# Usage: scripts/update-manifest.sh <version> <zip-path> <source-url> [changelog]
set -euo pipefail

version="${1:?version required}"
zip_path="${2:?zip path required}"
source_url="${3:?source url required}"
changelog="${4:-Release ${version}}"

manifest="$(dirname "$0")/../manifest.json"
checksum="$(md5sum "$zip_path" | cut -d' ' -f1)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
target_abi="$(grep -oP '(?<=^targetAbi: ")[^"]+' "$(dirname "$0")/../build.yaml")"

jq --arg version "$version" \
   --arg changelog "$changelog" \
   --arg targetAbi "$target_abi" \
   --arg sourceUrl "$source_url" \
   --arg checksum "$checksum" \
   --arg timestamp "$timestamp" \
   '(.[0].versions) |= ([{
        version: $version,
        changelog: $changelog,
        targetAbi: $targetAbi,
        sourceUrl: $sourceUrl,
        checksum: $checksum,
        timestamp: $timestamp
    }] + map(select(.version != $version)))' \
   "$manifest" > "$manifest.tmp"

mv "$manifest.tmp" "$manifest"
echo "manifest.json updated: $version ($checksum)"
