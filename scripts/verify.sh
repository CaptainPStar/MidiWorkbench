#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore MidiWorkbench.slnx --locked-mode --disable-parallel -m:1
dotnet build MidiWorkbench.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project tests/MidiWorkbench.Specs -c Release --no-build
