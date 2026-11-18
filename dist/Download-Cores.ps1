# Downloads sing-box and amneziawg-tools cores into dist/cores/
$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$coresDir = Join-Path $scriptDir "cores"
New-Item -ItemType Directory -Path $coresDir -Force | Out-Null

Write-Host "[1/2] Downloading latest sing-box (Windows amd64)..." -ForegroundColor Cyan
$sb = Invoke-RestMethod -Uri "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
$sbAsset = $sb.assets | Where-Object { $_.name -like "*windows-amd64.zip" } | Select-Object -First 1
$sbZip = Join-Path $coresDir "sing-box.zip"
Invoke-WebRequest -Uri $sbAsset.browser_download_url -OutFile $sbZip
Expand-Archive -Path $sbZip -DestinationPath (Join-Path $coresDir "sb_tmp") -Force
Get-ChildItem (Join-Path $coresDir "sb_tmp") -Recurse -Filter "sing-box.exe" | Copy-Item -Destination (Join-Path $coresDir "sing-box.exe") -Force
Remove-Item (Join-Path $coresDir "sb_tmp"), $sbZip -Recurse -Force

Write-Host "[2/2] Downloading amneziawg-tools..." -ForegroundColor Cyan
$awg = Invoke-RestMethod -Uri "https://api.github.com/repos/amnezia-vpn/amneziawg-tools/releases/latest"
$awgAsset = $awg.assets | Where-Object { $_.name -like "*windows*" } | Select-Object -First 1
$awgZip = Join-Path $coresDir "awg.zip"
Invoke-WebRequest -Uri $awgAsset.browser_download_url -OutFile $awgZip
Expand-Archive -Path $awgZip -DestinationPath (Join-Path $coresDir "awg_tmp") -Force
Get-ChildItem (Join-Path $coresDir "awg_tmp") -Recurse -Filter "*.exe" | Copy-Item -Destination $coresDir -Force
Remove-Item (Join-Path $coresDir "awg_tmp"), $awgZip -Recurse -Force

Write-Host "Done! Cores installed to $coresDir" -ForegroundColor Green
