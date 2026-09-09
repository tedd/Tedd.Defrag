# Out-of-process classic context menu. Appears under Show more options on Windows 11.
param([Parameter(Mandatory)][string]$CliPath, [switch]$Remove)
$ErrorActionPreference = 'Stop'
$cli = (Resolve-Path -LiteralPath $CliPath).Path
if ([IO.Path]::GetFileName($cli) -ne 'Tedd.Defrag.Cli.exe') { throw 'Select Tedd.Defrag.Cli.exe.' }
foreach ($class in '*','Directory') {
    foreach ($operation in 'Analyze','Preview') {
        $key = "HKCU:\Software\Classes\$class\shell\Tedd.Defrag.$operation"
        if ($Remove) { if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse }; continue }
        New-Item -Path $key -Force | Out-Null
        Set-Item -LiteralPath $key -Value $(if ($operation -eq 'Analyze') { 'Analyze fragmentation with Tedd Defrag' } else { 'Preview defragmentation with Tedd Defrag' })
        New-ItemProperty -LiteralPath $key -Name MultiSelectModel -Value Single -PropertyType String -Force | Out-Null
        New-Item -Path "$key\command" -Force | Out-Null
        $verb = if ($operation -eq 'Analyze') { 'analyze' } else { 'defrag' }
        Set-Item -LiteralPath "$key\command" -Value ('"' + $cli + '" ' + $verb + ' --path "%1" --wait')
    }
}
Write-Output 'Explorer commands registered for this user. Jobs open out of process and default to preview.'
