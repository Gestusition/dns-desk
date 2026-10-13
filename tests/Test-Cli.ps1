$ErrorActionPreference = 'Stop'
$rootPath = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $rootPath 'src\DnsDesk.Cli\bin\Release\net8.0-windows\dns-desk.dll'
function Run-Cli([string[]]$Arguments, [int]$ExpectedCode = 0) {
    $output = & dotnet $assemblyPath @Arguments
    if ($LASTEXITCODE -ne $ExpectedCode) { throw ('Unexpected exit code for ' + ($Arguments -join ' ') + ': ' + ($output -join ' ')) }
    $output -join "`n"
}
$version = Run-Cli @('--version')
if ($version -ne 'DNS Desk CLI 1.0.0') { throw 'Version output does not match.' }
$status = Run-Cli @('status','--json') | ConvertFrom-Json
if (-not $status.Engine.StartsWith('PowerShell ')) { throw 'Read-only status failed.' }
$providers = Run-Cli @('providers','--json') | ConvertFrom-Json
if ($providers.Count -ne 3) { throw 'Provider catalog failed.' }
$lookup = Run-Cli @('doh','8.8.8.8','--json') | ConvertFrom-Json
if (-not $lookup.Found -or -not $lookup.Template.StartsWith('https://')) { throw 'Automatic template lookup failed.' }
$unknown = Run-Cli -Arguments @('doh','192.0.2.1','--json') -ExpectedCode 1 | ConvertFrom-Json
if ($unknown.Found) { throw 'Unknown provider was guessed.' }
if ($status.Adapters.Count) {
    $preview = Run-Cli @('set','--provider','google','--dry-run','--json') | ConvertFrom-Json
    if (-not $preview.DryRun -or -not $preview.Settings.UseDoh -or $preview.Settings.AllowFallback) { throw 'Dry-run plan failed.' }
    $automatic = Run-Cli @('reset','--dry-run','--json') | ConvertFrom-Json
    if (-not $automatic.Settings.Automatic) { throw 'Automatic DNS preview failed.' }
    $refused = Run-Cli -Arguments @('set','--provider','google','--json') -ExpectedCode 2 | ConvertFrom-Json
    if ($refused.Success -ne $false -or -not $refused.Message.Contains('--yes')) { throw 'Unconfirmed JSON application was not refused.' }
}
$bad = Run-Cli -Arguments @('set','--provider','google','--doh','invalid','--dry-run','--json') -ExpectedCode 2 | ConvertFrom-Json
if ($bad.Success -ne $false) { throw 'Invalid options were not rejected.' }
Write-Output 'CLI integration checks passed. No DNS settings or cache changed.'
