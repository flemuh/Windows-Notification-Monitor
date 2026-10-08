$ErrorActionPreference = "Stop"

$pkg = Get-AppxPackage -Name "Fernando.ChromeNotificationWatcher"

if (-not $pkg) {
    Write-Host "Chrome Notification Watcher não está instalado."
    exit 0
}

$pkg | Remove-AppxPackage
Write-Host "Chrome Notification Watcher removido."
