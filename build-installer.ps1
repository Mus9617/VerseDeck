param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root "artifacts\publish\win-x64"
$installerDir = Join-Path $root "artifacts\installer"
$issPath = Join-Path $root "src\VerseDeck.Installer\VerseDeckCompanion.iss"

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

dotnet publish (Join-Path $root "src\VerseDeck.App\VerseDeck.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDir

$isccCandidates = @(@(
    "iscc.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and ((Get-Command $_ -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $_)) })

if (-not $isccCandidates) {
    throw "No encuentro Inno Setup Compiler (ISCC.exe). Instala Inno Setup 6 y vuelve a ejecutar: .\build-installer.ps1 -Version $Version"
}

$iscc = if (Test-Path -LiteralPath $isccCandidates[0]) { $isccCandidates[0] } else { (Get-Command $isccCandidates[0]).Source }
& $iscc "/DMyAppVersion=$Version" $issPath

$setupPath = Join-Path $installerDir "VerseDeckCompanionSetup-$Version.exe"
Write-Host "Instalador creado: $setupPath"
