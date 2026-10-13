$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & dotnet build src/DnsDesk.Cli -c Release --nologo
    if ($LASTEXITCODE) { throw 'Build failed.' }
    & dotnet run --project tests/DnsDesk.Core.Tests
    if ($LASTEXITCODE) { throw 'Core checks failed.' }
    & pwsh -NoProfile -File tests/Test-Backend.ps1
    if ($LASTEXITCODE) { throw 'PowerShell 7 backend checks failed.' }
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File tests/Test-Backend.ps1
    if ($LASTEXITCODE) { throw 'Windows PowerShell backend checks failed.' }
    & pwsh -NoProfile -File tests/Test-Cli.ps1
    if ($LASTEXITCODE) { throw 'CLI integration checks failed.' }
} finally { Pop-Location }
