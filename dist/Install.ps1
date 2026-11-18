# FakeGPS Windows 11 Automated Installer
# Requires Administrator privileges

$ErrorActionPreference = "Stop"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "   FakeGPS Installer for Windows 11      " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Please run this script as Administrator!"
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$driverDir = Join-Path $scriptDir "driver"
$certPath = Join-Path $driverDir "FakeGPS.cer"
$infPath = Join-Path $driverDir "FakeGPS.inf"

Write-Host "[1/4] Installing certificate to Trusted Root and Publishers..." -ForegroundColor Yellow
certutil.exe -addstore -f "Root" $certPath | Out-Null
certutil.exe -addstore -f "TrustedPublisher" $certPath | Out-Null
Write-Host "      Certificate installed successfully." -ForegroundColor Green

Write-Host "[2/4] Adding driver package to Windows DriverStore..." -ForegroundColor Yellow
$pnpAdd = pnputil.exe /add-driver "$infPath"
Write-Host "      $pnpAdd"

Write-Host "[3/4] Creating and installing Sensor root device..." -ForegroundColor Yellow
# Locate devcon if available or check existing device
$dev = Get-PnpDevice -InstanceId "ROOT\SENSOR\0000" -ErrorAction SilentlyContinue
if (-not $dev) {
    $devcon = (Get-ChildItem -Path "C:\" -Filter "devcon.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if ($devcon) {
        & "$devcon" install "$infPath" "Sensors\FakeGPS"
    } else {
        Write-Host "      Devcon not found; please add legacy device in Device Manager or use existing node." -ForegroundColor Yellow
    }
} else {
    Write-Host "      Device node ROOT\SENSOR\0000 already present." -ForegroundColor Green
}

Write-Host "[4/4] Restarting device node..." -ForegroundColor Yellow
pnputil.exe /restart-device "ROOT\SENSOR\0000" | Out-Null
$status = Get-PnpDevice -InstanceId "ROOT\SENSOR\0000" -ErrorAction SilentlyContinue
if ($status -and $status.Status -eq "OK") {
    Write-Host "=========================================" -ForegroundColor Green
    Write-Host " FakeGPS successfully installed and running!" -ForegroundColor Green
    Write-Host " Launch FakeGPS.UI.exe to manage location!" -ForegroundColor Green
    Write-Host "=========================================" -ForegroundColor Green
} else {
    Write-Host "Device status: $($status.Status)" -ForegroundColor Yellow
}
