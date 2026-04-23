Write-Host "Restarting WinFsp.Launcher..." -ForegroundColor Cyan
Restart-Service WinFsp.Launcher -Force
Start-Sleep 3

Write-Host "Mounting K:..." -ForegroundColor Cyan
net use K: /delete /y 2>$null | Out-Null
net use K: \\sshfs.k\claude@vm-golden /persistent:yes
Start-Sleep 2

if (Test-Path K:\) {
    Write-Host "`nK: MOUNTED" -ForegroundColor Green
    Get-ChildItem K:\ | Select-Object -First 5 Name
} else {
    Write-Host "`nK: STILL NOT MOUNTED" -ForegroundColor Red
}

Write-Host "`nPress Enter to close this window"
Read-Host
