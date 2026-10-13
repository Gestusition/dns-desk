#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Read', 'Apply')][string]$Mode, [string]$Payload)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$logs = New-Object 'Collections.Generic.List[string]'
$adapterUndo = @()
$dohUndo = @()
$changedAdapters = New-Object 'Collections.Generic.List[int]'
$changedServers = New-Object 'Collections.Generic.List[string]'

function Get-PhysicalAdapters {
    @(Get-NetAdapter -Physical | Where-Object { [int]$_.NdisPhysicalMedium -in @(1, 9, 14) })
}

function Test-Automatic($adapter) {
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\' + $adapter.InterfaceGuid
    $manual = Get-ItemPropertyValue -LiteralPath $key -Name NameServer -ErrorAction SilentlyContinue
    [string]::IsNullOrWhiteSpace([string]$manual)
}

function Get-Snapshot {
    $adapters = @(foreach ($adapter in (Get-PhysicalAdapters)) {
        $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4
        [pscustomobject]@{
            Index = [int]$adapter.InterfaceIndex
            Name = [string]$adapter.Name
            Status = [string]$adapter.Status
            Kind = $(if ([int]$adapter.NdisPhysicalMedium -eq 14) { 'Ethernet' } else { 'Wi-Fi' })
            Servers = @($dns.ServerAddresses)
            Automatic = (Test-Automatic $adapter)
        }
    })
    $doh = @(Get-DnsClientDohServerAddress | Select-Object ServerAddress, DohTemplate, AutoUpgrade, AllowFallbackToUdp)
    [pscustomobject]@{ Adapters = $adapters; DohServers = $doh; Engine = ('PowerShell ' + $PSVersionTable.PSVersion) }
}

function Test-Ipv4([string]$address) {
    $ip = $null
    $address -match '^\d{1,3}(\.\d{1,3}){3}$' -and
        [Net.IPAddress]::TryParse($address, [ref]$ip) -and
        $ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        $ip.ToString() -ceq $address -and $address -notin @('0.0.0.0', '255.255.255.255')
}

function Test-Https([string]$template) {
    $uri = $null
    [Uri]::TryCreate($template, [UriKind]::Absolute, [ref]$uri) -and
        $uri.Scheme -eq 'https' -and $uri.Host -and -not $uri.UserInfo -and -not $uri.Fragment
}

function Flush-Dns {
    $output = & "$env:SystemRoot\System32\ipconfig.exe" /flushdns 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('ipconfig /flushdns failed: ' + ($output -join ' ')) }
    $logs.Add('ipconfig /flushdns completed successfully.')
}

try {
    foreach ($name in @('Get-NetAdapter', 'Get-DnsClientDohServerAddress', 'Set-DnsClientDohServerAddress', 'Add-DnsClientDohServerAddress', 'Remove-DnsClientDohServerAddress', 'Get-DnsClientServerAddress', 'Set-DnsClientServerAddress')) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "Windows DNS-over-HTTPS support is required. Missing command: $name" }
    }
    if ($Mode -eq 'Read') {
        [pscustomobject]@{ Success = $true; Message = 'Network settings loaded.'; Logs = @(); Snapshot = (Get-Snapshot) } | ConvertTo-Json -Depth 10 -Compress
        return
    }

    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run DNS Desk as administrator to apply settings.' }
    $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Payload)) | ConvertFrom-Json
    if ($null -eq $request -or $request.Automatic -isnot [bool] -or $request.UseDoh -isnot [bool] -or $request.AllowFallback -isnot [bool]) { throw 'Invalid configuration request.' }
    $indexes = @($request.AdapterIndexes | ForEach-Object { [int]$_ })
    if (-not $indexes.Count -or @($indexes | Select-Object -Unique).Count -ne $indexes.Count) { throw 'Select distinct physical adapters.' }
    $selected = @(Get-PhysicalAdapters | Where-Object { [int]$_.InterfaceIndex -in $indexes })
    if ($selected.Count -ne $indexes.Count) { throw 'An adapter is no longer available. Refresh and try again.' }
    $resolvers = @($request.Resolvers)
    # Arrays expose an Address() method in PowerShell 7; enumerate resolver properties explicitly.
    $addresses = @($resolvers | ForEach-Object { [string]$_.Address })
    if (-not $request.Automatic) {
        if ($resolvers.Count -lt 1 -or $resolvers.Count -gt 2) { throw 'One or two IPv4 DNS servers are required.' }
        foreach ($resolver in $resolvers) {
            if (-not (Test-Ipv4 ([string]$resolver.Address))) { throw 'Invalid IPv4 DNS address.' }
            if ($request.UseDoh -and -not (Test-Https ([string]$resolver.Template))) { throw 'Each resolver requires a valid HTTPS endpoint.' }
        }
        if (@($addresses | Select-Object -Unique).Count -ne $resolvers.Count) { throw 'DNS server addresses must be distinct.' }
    }

    # Capture every affected setting before the first change, including DHCP provenance.
    $adapterUndo = @(foreach ($adapter in $selected) {
        $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4
        if ($null -eq $dns) { throw ('IPv4 is unavailable on ' + $adapter.Name) }
        [pscustomobject]@{ Adapter = $adapter; Servers = @($dns.ServerAddresses); Automatic = (Test-Automatic $adapter) }
    })
    $known = @(Get-DnsClientDohServerAddress)
    if (-not $request.Automatic) {
        $dohUndo = @(foreach ($resolver in $resolvers) {
            $previous = $known | Where-Object ServerAddress -eq $resolver.Address | Select-Object -First 1
            [pscustomobject]@{ Address = [string]$resolver.Address; Previous = $previous }
        })
        foreach ($resolver in $resolvers) {
            $previous = $known | Where-Object ServerAddress -eq $resolver.Address | Select-Object -First 1
            if ($request.UseDoh -or $null -ne $previous) {
                $changedServers.Add([string]$resolver.Address)
                $options = @{ ServerAddress = [string]$resolver.Address; AutoUpgrade = [bool]$request.UseDoh; AllowFallbackToUdp = [bool]($request.UseDoh -and $request.AllowFallback) }
                if ($request.UseDoh) { $options.DohTemplate = [string]$resolver.Template }
                if ($previous) { Set-DnsClientDohServerAddress @options } else { Add-DnsClientDohServerAddress @options }
                $actual = Get-DnsClientDohServerAddress -ServerAddress $resolver.Address
                if ([bool]$actual.AutoUpgrade -ne [bool]$request.UseDoh -or [bool]$actual.AllowFallbackToUdp -ne $options.AllowFallbackToUdp -or ($request.UseDoh -and $actual.DohTemplate -ne $resolver.Template)) { throw ('DoH verification failed for ' + $resolver.Address) }
            }
        }
    }
    foreach ($adapter in $selected) {
        $changedAdapters.Add([int]$adapter.InterfaceIndex)
        $ipv4 = Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4
        if ($request.Automatic) {
            $ipv4 | Set-DnsClientServerAddress -ResetServerAddresses
            if (-not (Test-Automatic $adapter)) { throw ('Automatic DNS verification failed on ' + $adapter.Name) }
        } else {
            $ipv4 | Set-DnsClientServerAddress -ServerAddresses $addresses
            $actual = Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4
            if (($actual.ServerAddresses -join ',') -ne ($addresses -join ',')) { throw ('DNS verification failed on ' + $adapter.Name) }
        }
        $logs.Add(('Applied and verified: ' + $adapter.Name))
    }
    Flush-Dns
    [pscustomobject]@{ Success = $true; Message = 'DNS settings saved and the DNS cache flushed.'; Logs = @($logs.ToArray()); Snapshot = (Get-Snapshot) } | ConvertTo-Json -Depth 10 -Compress
    return
} catch {
    $failure = $_.Exception.Message
    # Restore both adapter assignments and the resolver definitions touched by this operation.
    foreach ($undo in $adapterUndo) {
        if ([int]$undo.Adapter.InterfaceIndex -notin $changedAdapters) { continue }
        try {
            $ipv4 = Get-DnsClientServerAddress -InterfaceIndex $undo.Adapter.InterfaceIndex -AddressFamily IPv4
            if ($undo.Automatic) { $ipv4 | Set-DnsClientServerAddress -ResetServerAddresses }
            else { $ipv4 | Set-DnsClientServerAddress -ServerAddresses $undo.Servers }
            $logs.Add(('Restored adapter: ' + $undo.Adapter.Name))
        } catch { $logs.Add(('RESTORE FAILED for ' + $undo.Adapter.Name + ': ' + $_.Exception.Message)) }
    }
    foreach ($undo in $dohUndo) {
        if ($undo.Address -notin $changedServers) { continue }
        try {
            if ($null -eq $undo.Previous) { Remove-DnsClientDohServerAddress -ServerAddress $undo.Address -Confirm:$false }
            else {
                Set-DnsClientDohServerAddress -ServerAddress $undo.Address -DohTemplate $undo.Previous.DohTemplate -AutoUpgrade $undo.Previous.AutoUpgrade -AllowFallbackToUdp $undo.Previous.AllowFallbackToUdp
            }
            $logs.Add(('Restored DoH definition: ' + $undo.Address))
        } catch { $logs.Add(('RESTORE FAILED for ' + $undo.Address + ': ' + $_.Exception.Message)) }
    }
    if ($changedAdapters.Count -or $changedServers.Count) {
        try { Flush-Dns } catch { $logs.Add($_.Exception.Message) }
    }
    [pscustomobject]@{ Success = $false; Message = $failure; Logs = @($logs.ToArray()); Snapshot = $null } | ConvertTo-Json -Depth 10 -Compress
    return
}
