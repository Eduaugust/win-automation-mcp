<#
Registers (or removes) a Scheduled Task that runs the MCP server at logon IN THE INTERACTIVE SESSION of the current user.
Run it from an elevated or normal PowerShell as that same user (no password needed, task only runs while the user is logged on).

  .\install-task.ps1 -ExePath C:\Users\Eduardo\WinAutomationMcp_next\bin\Release\net8.0-windows\WinAutomationMcp.exe -Port 8766 -TaskName WinAutomationMcpTest -StartNow
  .\install-task.ps1 -TaskName WinAutomationMcpTest -Remove
#>
param(
  [string]$ExePath = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\WinAutomationMcp.exe'),
  [int]$Port = 8765,
  [string]$TaskName = 'WinAutomationMcp',
  [switch]$StartNow,
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if ($Remove) {
  $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($t) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    "removed task $TaskName (the running process, if any, was stopped through the task)"
  } else { "no task named $TaskName" }
  return
}
if (-not (Test-Path $ExePath)) { throw "exe not found: $ExePath" }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $ExePath -Argument "--port $Port" -WorkingDirectory (Split-Path $ExePath)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
"registered task $TaskName for $user : $ExePath --port $Port"
if ($StartNow) { Start-ScheduledTask -TaskName $TaskName; "started" }
