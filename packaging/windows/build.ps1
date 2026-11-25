param(
    [Parameter(Mandatory)][string]$Binary,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$binaryPath = (Get-Item -LiteralPath $Binary).FullName
$outputPath = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$compilerFolder = Join-Path $outputPath 'inno-7.1.1'
$compiler = Join-Path $compilerFolder 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $setup = Join-Path $outputPath 'innosetup-7.1.1-x64.exe'
    $uri = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
    Invoke-WebRequest -Uri $uri -OutFile $setup -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0') {
        throw 'Empreinte SHA-256 inattendue pour Inno Setup.'
    }
    $arguments = @('/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="{0}"' -f $compilerFolder))
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installation portable d’Inno Setup impossible ($($process.ExitCode))." }
}

& $compiler "/DPassloadVersion=$Version" "/DPassloadBinary=$binaryPath" "/O$outputPath" (Join-Path $PSScriptRoot 'Passload.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
