# LogDB IIS Log Exporter - Service Installation Script
# Run as Administrator

param(
    [string]$ServiceName = "LogDBIISLogExporter",
    [string]$DisplayName = "LogDB IIS Log Exporter",
    [string]$Description = "Exports IIS W3C log files to the LogDB platform for centralized log management and analysis.",
    [string]$InstallPath = "C:\LogDB\IISExporter",
    [string]$ServiceAccount = "LocalSystem"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  LogDB IIS Log Exporter - Service Installation" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as administrator
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "ERROR: This script must be run as Administrator!" -ForegroundColor Red
    exit 1
}

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "WARNING: Service '$ServiceName' already exists." -ForegroundColor Yellow
    $response = Read-Host "Do you want to stop and reinstall it? (y/n)"
    if ($response -ne 'y') {
        Write-Host "Installation cancelled." -ForegroundColor Yellow
        exit 0
    }
    
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    
    Write-Host "Removing existing service..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create installation directory
Write-Host "Creating installation directory: $InstallPath" -ForegroundColor Green
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

# Copy files
$sourcePath = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "Copying files from: $sourcePath" -ForegroundColor Green

# Look for published files
$publishPath = Join-Path $sourcePath "bin\Release\net10.0\win-x64\publish"
if (-not (Test-Path $publishPath)) {
    $publishPath = Join-Path $sourcePath "bin\Release\net10.0\publish"
}
if (-not (Test-Path $publishPath)) {
    $publishPath = Join-Path $sourcePath "bin\Debug\net10.0"
}

if (Test-Path $publishPath) {
    Write-Host "Copying from: $publishPath" -ForegroundColor Gray
    Copy-Item -Path "$publishPath\*" -Destination $InstallPath -Recurse -Force
} else {
    Write-Host "WARNING: No published files found. Please run 'dotnet publish -c Release' first." -ForegroundColor Yellow
    Write-Host "Or copy the files manually to: $InstallPath" -ForegroundColor Yellow
}

# Find the executable
$exePath = Join-Path $InstallPath "com.logdb.iis.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Executable not found at: $exePath" -ForegroundColor Red
    Write-Host "Please ensure the application is built and published." -ForegroundColor Red
    exit 1
}

# Create the Windows service
Write-Host "Creating Windows service..." -ForegroundColor Green
$binPath = "`"$exePath`""

New-Service -Name $ServiceName `
    -BinaryPathName $binPath `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType Automatic | Out-Null

# Configure service recovery options (restart on failure)
Write-Host "Configuring service recovery options..." -ForegroundColor Green
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# Copy example config if real config doesn't exist
$configPath = Join-Path $InstallPath "appsettings.json"
$exampleConfigPath = Join-Path $InstallPath "appsettings.Example.json"
if (-not (Test-Path $configPath) -and (Test-Path $exampleConfigPath)) {
    Write-Host "Creating appsettings.json from example..." -ForegroundColor Yellow
    Copy-Item -Path $exampleConfigPath -Destination $configPath
    Write-Host "IMPORTANT: Please edit $configPath and set your API key!" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "================================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Service Name: $ServiceName" -ForegroundColor Cyan
Write-Host "Install Path: $InstallPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Edit the configuration file:" -ForegroundColor White
Write-Host "   $configPath" -ForegroundColor Gray
Write-Host ""
Write-Host "2. Set your LogDB API key in the configuration" -ForegroundColor White
Write-Host ""
Write-Host "3. Start the service:" -ForegroundColor White
Write-Host "   Start-Service -Name $ServiceName" -ForegroundColor Gray
Write-Host ""
Write-Host "4. Check the service status:" -ForegroundColor White
Write-Host "   Get-Service -Name $ServiceName" -ForegroundColor Gray
Write-Host ""
Write-Host "5. View Windows Event Viewer for service logs" -ForegroundColor White
Write-Host ""
