# Render a documentation image from real CLI output; only dry-run is invoked.
$ErrorActionPreference = 'Stop'
$rootPath = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $rootPath 'src\DnsDesk.Cli\bin\Release\net8.0-windows\dns-desk.dll'
$output = @(& dotnet $assemblyPath set --provider google --adapters all --doh on --dry-run)
if ($LASTEXITCODE) { throw 'Build the CLI and ensure at least one physical adapter is present before generating the preview.' }
Add-Type -AssemblyName System.Drawing
$lines = @('PS> dns-desk set --provider google --adapters all --doh on --dry-run') + $output
$width = 1280
$height = 132 + $lines.Count * 25
$bitmap = New-Object Drawing.Bitmap($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$background = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#111D27'))
$panel = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#1B2E3B'))
$text = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#D6E5EE'))
$accent = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#6AE2BC'))
$muted = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#8199AA'))
$font = New-Object Drawing.Font('Consolas', 13)
$titleFont = New-Object Drawing.Font('Segoe UI', 15, [Drawing.FontStyle]::Bold)
try {
    $graphics.FillRectangle($background, 0, 0, $width, $height)
    $graphics.FillRectangle($panel, 0, 0, $width, 65)
    $graphics.DrawString('DNS DESK CLI', $titleFont, $accent, 32, 18)
    $graphics.DrawString('WINDOWS / IPv4 + DoH', $font, $muted, 947, 23)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $brush = $(if ($index -eq 0 -or $lines[$index] -like 'DNS DESK*' -or $lines[$index] -like 'Dry run*') { $accent } else { $text })
        $graphics.DrawString([string]$lines[$index], $font, $brush, 32, (88 + $index * 25))
    }
    $imageDirectory = Join-Path $rootPath 'docs\images'
    $null = New-Item -ItemType Directory -Path $imageDirectory -Force
    $bitmap.Save((Join-Path $imageDirectory 'terminal-preview.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    foreach ($resource in @($font,$titleFont,$background,$panel,$text,$accent,$muted,$graphics,$bitmap)) { $resource.Dispose() }
}
