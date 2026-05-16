# LogDB Event Viewer Exporter - Service Installation Script
# Run this script as Administrator

param(
    [string]$ServiceName = "LogDBEventViewerExporter",
    [string]$DisplayName = "LogDB Event Viewer Exporter",
    [string]$Description = "Exports Windows Event Viewer logs to LogDB platform",
    [string]$InstallPath = "",
    [string]$ServiceAccount = "NT AUTHORITY\SYSTEM"
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "LogDB Event Viewer Exporter Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator!" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

# Determine installation path
if ([string]::IsNullOrEmpty($InstallPath)) {
    $InstallPath = Join-Path $PSScriptRoot "publish"
    if (-not (Test-Path $InstallPath)) {
        $InstallPath = $PSScriptRoot
    }
}

$exePath = Join-Path $InstallPath "com.logdb.eventviewer.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Service executable not found at: $exePath" -ForegroundColor Red
    Write-Host "Please build the service first: dotnet publish -c Release" -ForegroundColor Yellow
    exit 1
}

# Check if appsettings.json exists
$configPath = Join-Path $InstallPath "appsettings.json"
if (-not (Test-Path $configPath)) {
    Write-Host "WARNING: appsettings.json not found at: $configPath" -ForegroundColor Yellow
    Write-Host "Please configure appsettings.json before starting the service" -ForegroundColor Yellow
    Write-Host ""
}

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Service '$ServiceName' already exists." -ForegroundColor Yellow
    $response = Read-Host "Do you want to remove and reinstall? (Y/N)"
    if ($response -eq "Y" -or $response -eq "y") {
        Write-Host "Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        
        Write-Host "Removing service..." -ForegroundColor Yellow
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    } else {
        Write-Host "Installation cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# Install service
Write-Host "Installing service..." -ForegroundColor Green
Write-Host "  Service Name: $ServiceName" -ForegroundColor Gray
Write-Host "  Display Name: $DisplayName" -ForegroundColor Gray
Write-Host "  Executable: $exePath" -ForegroundColor Gray
Write-Host "  Account: $ServiceAccount" -ForegroundColor Gray
Write-Host ""

$result = sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "$DisplayName"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create service" -ForegroundColor Red
    Write-Host $result -ForegroundColor Red
    exit 1
}

# Configure service account
Write-Host "Configuring service account..." -ForegroundColor Green
sc.exe config $ServiceName obj= $ServiceAccount | Out-Null

# Set description
sc.exe description $ServiceName "$Description" | Out-Null

# Configure recovery options (restart on failure)
Write-Host "Configuring recovery options..." -ForegroundColor Green
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host ""
Write-Host "Service installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Configure appsettings.json with your database connection and API key" -ForegroundColor White
Write-Host "2. Start the service: sc.exe start $ServiceName" -ForegroundColor White
Write-Host "3. Check status: sc.exe query $ServiceName" -ForegroundColor White
Write-Host "4. View logs: Get-EventLog -LogName Application -Source 'LogDB Event Viewer Exporter'" -ForegroundColor White
Write-Host ""

$startNow = Read-Host "Do you want to start the service now? (Y/N)"
if ($startNow -eq "Y" -or $startNow -eq "y") {
    Write-Host "Starting service..." -ForegroundColor Green
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    
    $service = Get-Service -Name $ServiceName
    if ($service.Status -eq "Running") {
        Write-Host "Service started successfully!" -ForegroundColor Green
    } else {
        Write-Host "Service started but status is: $($service.Status)" -ForegroundColor Yellow
        Write-Host "Check Windows Event Viewer for details" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green





