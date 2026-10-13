$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & dotnet publish src/DnsDesk.Cli -c Release -r win-x64 --self-contained true -o artifacts/publish `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    $hash = (Get-FileHash -LiteralPath artifacts/publish/dns-desk.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path (Get-Location) 'artifacts/publish/SHA256SUMS.txt'), "$hash  dns-desk.exe`n", [Text.UTF8Encoding]::new($false))
} finally { Pop-Location }
