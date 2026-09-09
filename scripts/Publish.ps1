param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')][string]$Version = '0.1.0',
    [string]$SourceRevision = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "release-staging\$Runtime"))
$bundle = Join-Path $stagingRoot 'bundle'
$intermediate = Join-Path $stagingRoot 'build'
$dist = Join-Path $artifactsRoot 'dist'
$assetName = "Tedd.Defrag-$Runtime.zip"
$archive = Join-Path $dist $assetName
$checksum = "$archive.sha256"

function Remove-BuildPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the repository artifacts directory: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Invoke-Publish([string]$Project, [string]$Output, [string]$BuildArtifacts) {
    $arguments = @(
        'publish', $Project,
        '-c', $Configuration,
        '-f', 'net11.0-windows10.0.19041.0',
        '-r', $Runtime,
        '--self-contained', 'true',
        '--artifacts-path', $BuildArtifacts,
        '-o', $Output,
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion",
        "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$informationalVersion",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:ContinuousIntegrationBuild=true'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$Project publish failed." }
}

$numericVersion = ($Version -split '[-+]')[0]
$numericParts = @($numericVersion.Split('.') | ForEach-Object { [int]$_ })
while ($numericParts.Count -lt 4) { $numericParts += 0 }
$numericVersion = ($numericParts[0..3] -join '.')
$informationalVersion = if ([string]::IsNullOrWhiteSpace($SourceRevision)) { $Version } else { "$Version+$SourceRevision" }

Remove-BuildPath $stagingRoot
New-Item -ItemType Directory -Path $bundle, $intermediate, $dist -Force | Out-Null
Remove-BuildPath $archive
Remove-BuildPath $checksum

Push-Location $repo
try {
    Invoke-Publish 'src/Tedd.Defrag.Desktop/Tedd.Defrag.Desktop.csproj' $bundle (Join-Path $intermediate 'desktop')
    Invoke-Publish 'src/Tedd.Defrag.Cli/Tedd.Defrag.Cli.csproj' (Join-Path $stagingRoot 'cli') (Join-Path $intermediate 'cli')
    Invoke-Publish 'src/Tedd.Defrag.Worker/Tedd.Defrag.Worker.csproj' (Join-Path $stagingRoot 'worker') (Join-Path $intermediate 'worker')

    Copy-Item -LiteralPath (Join-Path $stagingRoot 'cli\Tedd.Defrag.Cli.exe') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $stagingRoot 'worker\Tedd.Defrag.Worker.exe') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $bundle

    $manifest = [ordered]@{ version = $Version; runtime = $Runtime; assetName = $assetName }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'release-manifest.json') -Encoding utf8NoBOM

    $unexpected = Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.xml', '.deps.json', '.runtimeconfig.json' }
    if ($unexpected) { throw "Single-file publish left unexpected build files: $($unexpected.Name -join ', ')" }
    foreach ($required in 'Tedd.Defrag.Desktop.exe','Tedd.Defrag.Cli.exe','Tedd.Defrag.Worker.exe','release-manifest.json','README.md','LICENSE') {
        if (!(Test-Path -LiteralPath (Join-Path $bundle $required))) { throw "Release is missing $required." }
    }

    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $assetName" | Set-Content -LiteralPath $checksum -Encoding ascii
    Write-Output $archive
    Write-Output $checksum
}
finally {
    Pop-Location
    Remove-BuildPath $stagingRoot
}
