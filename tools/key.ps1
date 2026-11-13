# Brings the game to the front, then sends keys (dev helper). Usage: key.ps1 "{ENTER}" [waitSeconds]
param([string]$keys, [int]$wait = 3)
$w = New-Object -ComObject WScript.Shell
$null = $w.AppActivate((Get-Process "HEBEREKE BUNNY GARDEN").Id)
Start-Sleep -Milliseconds 300
$w.SendKeys($keys)
Start-Sleep $wait
