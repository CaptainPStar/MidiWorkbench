$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore MidiWorkbench.slnx --locked-mode --disable-parallel -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build MidiWorkbench.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/MidiWorkbench.Specs -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Specifications failed.' }
} finally { Pop-Location }
