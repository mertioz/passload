param(
    [Parameter(Mandatory)][string]$Binary,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$binaryPath = (Get-Item -LiteralPath $Binary).FullName
$outputPath = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $kits -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'MakeAppx est requis dans le SDK Windows.' }
$tool = Join-Path $makeAppx.FullName 'x64\makeappx.exe'

$staging = Join-Path $outputPath ('msix-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $staging 'Assets')
try {
    Copy-Item -LiteralPath $binaryPath -Destination (Join-Path $staging 'Passload.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../LICENSE') -Destination $staging
    $manifest = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml'))
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Save((Join-Path $staging 'AppxManifest.xml'))

    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot '../unix/site.aymen.Passload.png'))
    try {
        foreach ($logo in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
            $size = [int]$logo[1]
            $bitmap = [Drawing.Bitmap]::new($size, $size)
            try {
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.DrawImage($icon, 0, 0, $size, $size)
                } finally { $graphics.Dispose() }
                $bitmap.Save((Join-Path $staging ('Assets/' + $logo[0])), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
        }
    } finally { $icon.Dispose() }

    $package = Join-Path $outputPath 'Passload-Store-x64.msix'
    & $tool pack /o /h SHA256 /d $staging /p $package
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    $resolved = (Get-Item -LiteralPath $staging).FullName
    if ([IO.Path]::GetDirectoryName($resolved) -eq $outputPath) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
