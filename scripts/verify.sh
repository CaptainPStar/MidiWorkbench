#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore MidiWorkbench.slnx
dotnet build MidiWorkbench.slnx -c Release --no-restore
dotnet run --project tests/MidiWorkbench.Specs -c Release --no-build
