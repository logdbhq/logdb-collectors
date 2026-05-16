# LogDB Event Viewer Exporter - Service Uninstallation Script
# Run this script as Administrator

param(
    [string]$ServiceName = "LogDBEventViewerExporter"
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "LogDB Event Viewer Exporter Uninstaller" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator!" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

# Check if service exists
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' not found. Nothing to uninstall." -ForegroundColor Yellow
    exit 0
}

Write-Host "Service found: $ServiceName" -ForegroundColor Green
Write-Host "  Status: $($service.Status)" -ForegroundColor Gray
Write-Host ""

# Stop service if running
if ($service.Status -eq "Running") {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
    
    $service.Refresh()
    if ($service.Status -eq "Running") {
        Write-Host "WARNING: Service is still running. Attempting to force stop..." -ForegroundColor Yellow
        sc.exe stop $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }
}

# Delete service
Write-Host "Removing service..." -ForegroundColor Yellow
$result = sc.exe delete $ServiceName
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service removed successfully!" -ForegroundColor Green
} else {
    Write-Host "ERROR: Failed to remove service" -ForegroundColor Red
    Write-Host $result -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Uninstallation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Note: Configuration files (appsettings.json) were not removed." -ForegroundColor Gray
Write-Host "      You can manually delete them if needed." -ForegroundColor Gray





