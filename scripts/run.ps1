$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot '../src/MidiWorkbench.Desktop')
exit $LASTEXITCODE
