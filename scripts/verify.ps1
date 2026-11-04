$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore MidiWorkbench.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build MidiWorkbench.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/MidiWorkbench.Specs -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Specifications failed.' }
} finally { Pop-Location }
