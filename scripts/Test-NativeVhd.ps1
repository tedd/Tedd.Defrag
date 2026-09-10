# Administrator + Hyper-V PowerShell module required. This test operates only on its newly created VHDX.
param([Parameter(Mandatory)][string]$WorkerPath, [string]$ClientPath,
    [ValidateSet('MinimumWrite','Pack')][string]$Operation = 'MinimumWrite',
    [ValidateRange(1,16)][int]$MoveQueueDepth = 1, [ValidateRange(1,32)][int]$ScanWorkers = 1,
    [ValidatePattern('^[D-Z]$')][string]$DriveLetter = 'R', [switch]$KeepDisk)
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) { throw 'Run this integration test in an elevated PowerShell session.' }
Import-Module Hyper-V -ErrorAction Stop
if (Get-Volume -DriveLetter $DriveLetter -ErrorAction SilentlyContinue) { throw 'The requested test drive letter is already in use.' }
$worker = (Resolve-Path -LiteralPath $WorkerPath).Path
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\integration-tests'))
New-Item -ItemType Directory -Path $ownedRoot -Force | Out-Null
$run = [Guid]::NewGuid().ToString('N')
$vhd = [IO.Path]::GetFullPath((Join-Path $ownedRoot "$run.vhdx"))
if (!$vhd.StartsWith($ownedRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $vhd)) { throw 'Invalid disposable disk path.' }
$mounted = $false
try {
    New-VHD -Path $vhd -Dynamic -SizeBytes 512MB | Out-Null
    $disk = Mount-VHD -Path $vhd -PassThru | Get-Disk
    $mounted = $true
    if ($disk.IsBoot -or $disk.IsSystem -or $disk.PartitionStyle -ne 'RAW') { throw 'Refusing to initialize an unexpected disk.' }
    $disk | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize -DriveLetter $DriveLetter | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Tedd disposable test' -Confirm:$false | Out-Null
    $fixture = "${DriveLetter}:\fixtures"
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $gap = Join-Path $fixture 'leading-gap.bin'
    [IO.File]::WriteAllBytes($gap, [byte[]]::new(32MB))
    $random = [Random]::new(731)
    $block = [byte[]]::new(1MB)
    $streams = @()
    try {
        for ($i = 0; $i -lt 12; $i++) { $streams += [IO.File]::Open((Join-Path $fixture "$i.bin"), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read) }
        for ($pass = 0; $pass -lt 8; $pass++) { foreach ($stream in $streams) { $random.NextBytes($block); $stream.Write($block); $stream.Flush($true) } }
    } finally { foreach ($stream in $streams) { $stream.Dispose() } }
    Remove-Item -LiteralPath $gap
    $before = @{}
    Get-ChildItem -LiteralPath $fixture -File | ForEach-Object { $before[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    if ($ClientPath) {
        $client = (Resolve-Path -LiteralPath $ClientPath).Path
        $previousWorker = $env:TEDD_DEFRAG_WORKER
        try {
            $env:TEDD_DEFRAG_WORKER = $worker
            # Exercise submission, dispatch, child launch, and status polling too.
            $output = & $client optimize "${DriveLetter}:" --policy $Operation --path $fixture --exclude (Join-Path $fixture '0.bin') --min-fragments 2 --allow-ssd --budget-mib 256 --minutes 10 --memory 1024 --cpu 50 --io 0 --move-queue $MoveQueueDepth --scan-workers $ScanWorkers --allow-battery --execute --wait --json --no-update-check
            if ($LASTEXITCODE -notin 0,3) { throw "Native broker job failed: $output" }
            $result = ($output -join "`n") | ConvertFrom-Json
        } finally { $env:TEDD_DEFRAG_WORKER = $previousWorker }
    } else {
        $id = [Guid]::NewGuid()
        $jobDir = Join-Path $env:LOCALAPPDATA ('Tedd.Defrag\jobs\' + $id.ToString('N'))
        New-Item -ItemType Directory -Path $jobDir | Out-Null
        $request = @{ Id=$id.ToString(); Volume="${DriveLetter}:\"; Operation=$Operation; Preview=$false; SelectedPaths=@($fixture); Exclusions=@((Join-Path $fixture '0.bin')); MinimumFragments=2; AllowSsdRelocation=$true; MaxMoveBytes=256MB; MaxMinutes=10; Resources=@{MemoryMiB=1024;CpuPercent=50;IoMiBPerSecond=0;AcOnly=$false;Background=$true;MoveQueueDepth=$MoveQueueDepth;ScanWorkers=$ScanWorkers} }
        $request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $jobDir 'request.json') -Encoding utf8NoBOM
        & $worker --execute $id.ToString()
        $result = Get-Content -LiteralPath (Join-Path $jobDir 'snapshot.json') -Raw | ConvertFrom-Json
    }
    if ($result.State -notin 'Completed','Partial') { throw "Native job failed: $($result.Message)" }
    Get-ChildItem -LiteralPath $fixture -File | ForEach-Object { if ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne $before[$_.Name]) { throw "Content changed: $($_.Name)" } }
    & chkdsk "${DriveLetter}:"
    if ($LASTEXITCODE -ne 0) { throw 'Filesystem consistency check failed.' }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ownedRoot "$run-result.json")
    Write-Output "Hashes and filesystem checks passed. Verified relocation: $($result.BytesMoved) bytes. Result: $ownedRoot\$run-result.json"
    if ($result.BytesMoved -eq 0) { throw 'Fixture did not exercise a successful relocation. Review its allocation before treating this as an execution test.' }
} finally {
    if ($mounted) { Dismount-VHD -Path $vhd }
    if (!$KeepDisk -and (Test-Path -LiteralPath $vhd)) {
        $resolved = (Resolve-Path -LiteralPath $vhd).Path
        if (!$resolved.StartsWith($ownedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target escaped the disposable test directory.' }
        Remove-Item -LiteralPath $resolved
    }
}
