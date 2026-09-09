param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo "artifacts\app-$Runtime"
$running = Get-Process -Name 'Tedd.Defrag*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Close the published application and stop its idle worker before replacing this distribution, or retain the existing version.' }
Push-Location $repo
try {
    dotnet publish src/Tedd.Defrag.Worker -c $Configuration -r $Runtime --self-contained true -o "$output/worker"
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    dotnet publish src/Tedd.Defrag.Cli -c $Configuration -r $Runtime --self-contained true -o "$output/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    dotnet publish src/Tedd.Defrag.Desktop -c $Configuration -r $Runtime --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $output -Recurse -Force
    Write-Output "Ready: $output\Tedd.Defrag.Desktop.exe"
} finally { Pop-Location }
