#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-win-x64}"
case "$rid" in win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) echo "Unsupported runtime: $rid" >&2; exit 2;; esac
while IFS= read -r lock; do
    project_dir="$(dirname "$lock")"
    mkdir -p "$project_dir/obj"
    cp "$lock" "$project_dir/obj/packages.$rid.lock.json"
done < <(find src -name packages.lock.json -type f)
dotnet publish src/MidiWorkbench.Desktop -c Release -r "$rid" --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$rid/desktop"
dotnet publish src/MidiWorkbench.Cli -c Release -r "$rid" --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$rid/cli"
cp README.md THIRD_PARTY.md "artifacts/$rid/"
cp -r docs "artifacts/$rid/"
echo "artifacts/$rid"
