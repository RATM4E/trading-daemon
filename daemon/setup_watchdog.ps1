# ============================================================
# Register Trading Daemon Watchdog in Task Scheduler
# Run this script once as Administrator
# ============================================================

$TaskName = "TradingDaemonWatchdog"
$WatchdogPath = "D:\trading-daemon\daemon\watchdog.bat"

# Remove existing task if any
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

# Trigger: every 2 minutes, indefinitely
$Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 2) `
    -RepetitionDuration ([TimeSpan]::MaxValue)

# Action: run watchdog.bat
$Action = New-ScheduledTaskAction -Execute $WatchdogPath `
    -WorkingDirectory "D:\trading-daemon\daemon"

# Settings
$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 1)

# Register (runs as current user, even when locked)
Register-ScheduledTask `
    -TaskName $TaskName `
    -Trigger $Trigger `
    -Action $Action `
    -Settings $Settings `
    -Description "Monitors Trading Daemon, restarts if crashed" `
    -RunLevel Highest

Write-Host "Task '$TaskName' registered successfully." -ForegroundColor Green
Write-Host "Watchdog runs every 2 minutes. If daemon is down — restarts it."
Write-Host ""
Write-Host "To check:  Get-ScheduledTask -TaskName $TaskName"
Write-Host "To remove: Unregister-ScheduledTask -TaskName $TaskName"
