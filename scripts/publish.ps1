param([ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet publish src/MidiWorkbench.Desktop -c Release -r $Runtime --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$Runtime/desktop"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    dotnet publish src/MidiWorkbench.Cli -c Release -r $Runtime --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$Runtime/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    Copy-Item README.md,THIRD_PARTY.md "artifacts/$Runtime/"
    Copy-Item docs "artifacts/$Runtime/" -Recurse -Force
    Write-Output "artifacts/$Runtime"
} finally { Pop-Location }
