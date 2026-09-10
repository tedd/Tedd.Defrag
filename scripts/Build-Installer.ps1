param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Runtime,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$WixPath = $env:WIX
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($WixPath)) {
    $command = Get-Command wix -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw 'WiX 5.0.2 is required. Set WIX to wix.exe or add wix to PATH.' }
    $WixPath = $command.Source
}
$WixPath = [IO.Path]::GetFullPath($WixPath)
if (!(Test-Path -LiteralPath $WixPath -PathType Leaf)) { throw "WiX was not found at $WixPath." }
if (!(Test-Path -LiteralPath $payload -PathType Container)) { throw "Installer payload was not found at $payload." }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$requiredFiles = @('Tedd.Defrag.Desktop.exe','Tedd.Defrag.Cli.exe','Tedd.Defrag.Worker.exe','release-manifest.json','README.md','LICENSE')
foreach ($required in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Installer payload is missing $required." }
}
$unexpected = Get-ChildItem -LiteralPath $payload -File | Where-Object Name -NotIn $requiredFiles
if ($unexpected) { throw "Installer payload contains files not declared in Product.wxs: $($unexpected.Name -join ', ')." }

$manifestPath = Join-Path $payload 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.runtime -ne $Runtime) {
    throw "Release manifest does not match installer version $Version and runtime $Runtime."
}
[ordered]@{
    version = $manifest.version
    runtime = $manifest.runtime
    assetName = "Tedd.Defrag-Setup-$Runtime.exe"
    installType = 'installer'
} | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$numericVersion = ($Version -split '[-+]')[0]
$architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$runtimeLabel = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$productUpgradeCode = if ($Runtime -eq 'win-arm64') { 'E3D6144B-6246-43A1-BD8F-AF2F02702F1D' } else { '8CFE88AD-B3EB-448C-9208-A3AB744DCD26' }
$bundleUpgradeCode = if ($Runtime -eq 'win-arm64') { 'B4101807-536E-412E-858B-F38B85D3E3CB' } else { '9805DD39-C9FB-4C52-BEB2-2106691398DA' }
$msiName = "Tedd.Defrag-$Runtime.msi"
$setupName = "Tedd.Defrag-Setup-$Runtime.exe"
$msiPath = Join-Path $output $msiName
$setupPath = Join-Path $output $setupName
$intermediate = Join-Path $output "wix-$Runtime"
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null

& $WixPath build (Join-Path $repo 'installer\Product.wxs') `
    -arch $architecture `
    -d "Version=$numericVersion" `
    -d "UpgradeCode=$productUpgradeCode" `
    -d "PayloadDir=$payload" `
    -intermediatefolder (Join-Path $intermediate 'msi') `
    -pdbtype none `
    -out $msiPath
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
& $WixPath msi validate $msiPath
if ($LASTEXITCODE -ne 0) { throw 'MSI validation failed.' }

& $WixPath build (Join-Path $repo 'installer\Bundle.wxs') `
    -arch $architecture `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d "Version=$numericVersion" `
    -d "RuntimeLabel=$runtimeLabel" `
    -d "BundleUpgradeCode=$bundleUpgradeCode" `
    -d "MsiPath=$msiPath" `
    -intermediatefolder (Join-Path $intermediate 'bundle') `
    -pdbtype none `
    -out $setupPath
if ($LASTEXITCODE -ne 0) { throw 'EXE installer build failed.' }

foreach ($asset in $msiPath, $setupPath) {
    $assetName = Split-Path -Leaf $asset
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName" | Set-Content -LiteralPath "$asset.sha256" -Encoding ascii
    Write-Output $asset
    Write-Output "$asset.sha256"
}
