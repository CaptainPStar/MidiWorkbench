param([ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    # Seed separate runtime locks from the reviewed portable dependency graph.
    foreach ($lockFile in Get-ChildItem src -Filter packages.lock.json -Recurse) {
        $runtimeLockDirectory = Join-Path $lockFile.DirectoryName 'obj'
        New-Item -ItemType Directory -Path $runtimeLockDirectory -Force | Out-Null
        Copy-Item -LiteralPath $lockFile.FullName -Destination (Join-Path $runtimeLockDirectory "packages.$Runtime.lock.json") -Force
    }
    dotnet publish src/MidiWorkbench.Desktop -c Release -r $Runtime --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$Runtime/desktop"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    dotnet publish src/MidiWorkbench.Cli -c Release -r $Runtime --self-contained true -m:1 -p:UseSharedCompilation=false -o "artifacts/$Runtime/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    Copy-Item README.md,THIRD_PARTY.md "artifacts/$Runtime/"
    Copy-Item docs "artifacts/$Runtime/" -Recurse -Force
    Write-Output "artifacts/$Runtime"
} finally { Pop-Location }
