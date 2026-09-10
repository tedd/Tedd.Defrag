param([Parameter(Mandatory)][string]$WorkerPath, [string]$Volume = $env:SystemDrive)
$ErrorActionPreference = 'Stop'
$worker = (Resolve-Path -LiteralPath $WorkerPath).Path
$output = & $worker --probe $Volume 2>&1
if ($LASTEXITCODE -ne 0) { throw "Worker volume discovery failed in its deployment directory: $output" }
$probe = ($output -join "`n") | ConvertFrom-Json
$expectedBuild = (Get-Item -LiteralPath $worker).VersionInfo.ProductVersion
if ($probe.WorkerBuild -ne $expectedBuild -or !$probe.Volume.Root -or !$probe.Volume.FileSystem) {
    throw 'Worker startup probe did not return the expected build and volume information.'
}
Write-Output "Worker startup verified: $($probe.WorkerBuild), $($probe.Volume.Root), $($probe.Volume.FileSystem). No relocation requested."
