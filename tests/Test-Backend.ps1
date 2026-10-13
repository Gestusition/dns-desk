# The real transactional backend runs against in-memory DNS cmdlet substitutes.
# No network adapter, resolver definition, or DNS cache on this machine is changed.
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '..\src\DnsDesk.Cli\Backend.ps1'
$source = [IO.File]::ReadAllText($sourcePath)
$guard = "if (-not `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
if (-not $source.Contains($guard)) { throw 'The expected administrator guard was not found.' }
$source = $source.Replace($guard, 'if ($false)')
$flushStart = $source.IndexOf('function Flush-Dns {')
$flushEnd = $source.IndexOf("`ntry {", $flushStart)
if ($flushStart -lt 0 -or $flushEnd -lt 0) { throw 'The expected cache-flush boundary was not found.' }
$source = $source.Substring(0, $flushStart) + 'function Flush-Dns { $script:flushCount++; $logs.Add("Cache flushed (test)") }' + "`n" + $source.Substring($flushEnd)
$testsPassed = 0
function Assert([string]$name, [bool]$condition) {
    if (-not $condition) { throw ($name + ': ' + $script:result.Message + ' / ' + ($script:result.Logs -join '; ')) }
    Write-Output "PASS $name"
    $script:testsPassed++
}
function Initialize-State {
    $script:state = @{
        81 = @{ Servers = @('192.0.2.10'); Automatic = $true }
        82 = @{ Servers = @('192.0.2.20'); Automatic = $false }
    }
    $script:definitions = @{ '8.8.8.8' = [pscustomobject]@{ ServerAddress = '8.8.8.8'; DohTemplate = 'https://example.org/original'; AutoUpgrade = $false; AllowFallbackToUdp = $true } }
    $script:failIndex = -1
    $script:failedOnce = $false
    $script:flushCount = 0
}
function Get-NetAdapter {
    param([switch]$Physical)
    @(
        [pscustomobject]@{ InterfaceIndex = 81; Name = 'Test Wi-Fi'; Status = 'Disconnected'; NdisPhysicalMedium = 9; InterfaceGuid = '{81}' }
        [pscustomobject]@{ InterfaceIndex = 82; Name = 'Test Ethernet'; Status = 'Up'; NdisPhysicalMedium = 14; InterfaceGuid = '{82}' }
    )
}
function Get-ItemPropertyValue {
    param($LiteralPath, $Name)
    $index = $(if ($LiteralPath.EndsWith('{81}')) { 81 } else { 82 })
    if ($script:state[$index].Automatic) { '' } else { $script:state[$index].Servers -join ',' }
}
function Get-DnsClientServerAddress {
    param([int]$InterfaceIndex, $AddressFamily)
    [pscustomobject]@{ InterfaceIndex = $InterfaceIndex; ServerAddresses = @($script:state[$InterfaceIndex].Servers) }
}
function Set-DnsClientServerAddress {
    param([Parameter(ValueFromPipeline)]$InputObject, $ServerAddresses, [switch]$ResetServerAddresses)
    process {
        $index = [int]$InputObject.InterfaceIndex
        if ($index -eq $script:failIndex -and -not $script:failedOnce) { $script:failedOnce = $true; throw 'Injected adapter failure' }
        $script:state[$index].Automatic = [bool]$ResetServerAddresses
        $script:state[$index].Servers = $(if ($ResetServerAddresses) { @('192.0.2.10') } else { @($ServerAddresses) })
    }
}
function Get-DnsClientDohServerAddress {
    param($ServerAddress)
    if ($ServerAddress) { $script:definitions[[string]$ServerAddress] } else { @($script:definitions.Values) }
}
function Set-DnsClientDohServerAddress {
    param($ServerAddress, $DohTemplate, [bool]$AutoUpgrade, [bool]$AllowFallbackToUdp)
    if (-not $DohTemplate) { $DohTemplate = $script:definitions[[string]$ServerAddress].DohTemplate }
    $script:definitions[[string]$ServerAddress] = [pscustomobject]@{ ServerAddress = [string]$ServerAddress; DohTemplate = $DohTemplate; AutoUpgrade = $AutoUpgrade; AllowFallbackToUdp = $AllowFallbackToUdp }
}
function Add-DnsClientDohServerAddress {
    param($ServerAddress, $DohTemplate, [bool]$AutoUpgrade, [bool]$AllowFallbackToUdp)
    Set-DnsClientDohServerAddress @PSBoundParameters
}
function Remove-DnsClientDohServerAddress {
    param($ServerAddress, [switch]$Confirm)
    $script:definitions.Remove([string]$ServerAddress)
}
function Invoke-TestApply($request) {
    $json = $request | ConvertTo-Json -Depth 6 -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    & ([scriptblock]::Create($source)) -Mode Apply -Payload $encoded | ConvertFrom-Json
}
$request = @{ AdapterIndexes = @(81,82); Automatic = $false; UseDoh = $true; AllowFallback = $false; Resolvers = @(
    @{ Address = '8.8.8.8'; Template = 'https://dns.google/dns-query' },
    @{ Address = '8.8.4.4'; Template = 'https://dns.google/dns-query' }
) }
Initialize-State
$result = Invoke-TestApply $request
Assert 'save both selected adapters including disconnected Wi-Fi' ($result.Success -and ($state[81].Servers -join ',') -eq '8.8.8.8,8.8.4.4' -and ($state[82].Servers -join ',') -eq '8.8.8.8,8.8.4.4')
Assert 'enable DoH with fallback disabled' ($definitions['8.8.8.8'].AutoUpgrade -and -not $definitions['8.8.8.8'].AllowFallbackToUdp)
Assert 'flush cache after successful save' ($flushCount -eq 1)
Initialize-State
$failIndex = 82
$result = Invoke-TestApply $request
Assert 'report the injected failure' (-not $result.Success -and $result.Message -eq 'Injected adapter failure')
Assert 'restore DHCP provenance after partial failure' ($state[81].Automatic)
Assert 'restore previous static DNS' (-not $state[82].Automatic -and ($state[82].Servers -join ',') -eq '192.0.2.20')
Assert 'restore prior system DoH definition' ($definitions['8.8.8.8'].DohTemplate -eq 'https://example.org/original' -and -not $definitions['8.8.8.8'].AutoUpgrade -and $definitions['8.8.8.8'].AllowFallbackToUdp)
Assert 'remove newly created DoH definition on rollback' (-not $definitions.ContainsKey('8.8.4.4'))
Initialize-State
$invalid = $request.Clone()
$invalid.Resolvers = @(@{ Address = '8.8.8.8'; Template = 'http://example.org/query' })
$result = Invoke-TestApply $invalid
Assert 'reject HTTP endpoints before making changes' (-not $result.Success -and $state[81].Automatic -and $flushCount -eq 0)
Initialize-State
$off = $request.Clone()
$off.UseDoh = $false
$definitions['8.8.8.8'].AutoUpgrade = $true
$result = Invoke-TestApply $off
Assert 'DoH off disables existing automatic encryption' ($result.Success -and -not $definitions['8.8.8.8'].AutoUpgrade)
Initialize-State
$automatic = $request.Clone()
$automatic.Automatic = $true
$automatic.Resolvers = @()
$result = Invoke-TestApply $automatic
Assert 'restore automatic DNS on both adapters' ($result.Success -and $state[81].Automatic -and $state[82].Automatic)
Write-Output "$testsPassed backend checks passed. No real settings changed."
