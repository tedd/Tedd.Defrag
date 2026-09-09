# Explicit opt-in: installs a per-user, elevated logon task; never runs as SYSTEM.
param([Parameter(Mandatory)][string]$WorkerPath, [switch]$Remove)
$ErrorActionPreference = 'Stop'
$taskName = 'Tedd.Defrag Worker - ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name.Replace('\','_')
if ($Remove) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false; return }
$worker = (Resolve-Path -LiteralPath $WorkerPath).Path
if ([IO.Path]::GetFileName($worker) -ne 'Tedd.Defrag.Worker.exe') { throw 'Select the published Tedd.Defrag.Worker.exe.' }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $worker -Argument '--broker' -WorkingDirectory ([IO.Path]::GetDirectoryName($worker))
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
Write-Output 'The broker will start at sign-in. Each job retains its own idle, power, CPU, memory and bandwidth policy.'
