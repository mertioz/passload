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
$makePri = Join-Path $makeAppx.FullName 'x64\makepri.exe'
if (-not (Test-Path -LiteralPath $makePri)) { throw 'MakePri est requis dans le SDK Windows.' }

$staging = Join-Path $outputPath ('msix-' + [guid]::NewGuid().ToString('N'))
$config = "$staging.priconfig.xml"
$assets = [IO.Directory]::CreateDirectory((Join-Path $staging 'Assets')).FullName
try {
    Copy-Item -LiteralPath $binaryPath -Destination (Join-Path $staging 'Passload.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../LICENSE') -Destination $staging
    $manifest = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml'))
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Save((Join-Path $staging 'AppxManifest.xml'))

    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot '../unix/site.aymen.Passload.png'))
    function Write-Logo([string]$name, [int]$size) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($icon, 0, 0, $size, $size)
            } finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $assets $name), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }

    try {
        Write-Logo 'StoreLogo.png' 50
        Write-Logo 'Square44x44Logo.png' 44
        Write-Logo 'Square150x150Logo.png' 150
        # Windows uses these transparent variants without adding the accent-colored tile.
        foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 96)) {
            Write-Logo "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size
        }
    } finally { $icon.Dispose() }

    & $makePri createconfig /cf $config /dq 'fr-FR'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $makePri new /pr $staging /cf $config /of (Join-Path $staging 'resources.pri')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $package = Join-Path $outputPath 'Passload-Store-x64.msix'
    & $tool pack /o /h SHA256 /d $staging /p $package
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    [IO.File]::Delete($config)
    $resolved = (Get-Item -LiteralPath $staging).FullName
    if ([IO.Path]::GetDirectoryName($resolved) -eq $outputPath) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
