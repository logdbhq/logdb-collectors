# LogDB Windows Firewall Service - Installation Script
# Run as Administrator

param(
    [Parameter(Mandatory=$false)]
    [string]$ServiceName = "LogDB.WindowsFirewall",

    [Parameter(Mandatory=$false)]
    [string]$DisplayName = "LogDB Windows Firewall",

    [Parameter(Mandatory=$false)]
    [string]$Description = "Syncs threat intelligence blocklists into Windows Firewall rules",

    [Parameter(Mandatory=$false)]
    [string]$InstallPath = "C:\LogDB\WindowsFirewall",

    [Parameter(Mandatory=$false)]
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

function Write-ColorOutput($ForegroundColor) {
    $fc = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = $ForegroundColor
    if ($args) {
        Write-Output $args
    }
    $host.UI.RawUI.ForegroundColor = $fc
}

function Test-Administrator {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($user)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Check if running as admin
if (-not (Test-Administrator)) {
    Write-ColorOutput Red "ERROR: This script must be run as Administrator"
    Write-Host "Right-click PowerShell and select 'Run as Administrator'"
    exit 1
}

Write-Host ""
Write-Host "================================================================"
Write-Host "   LogDB Windows Firewall - Service Installer"
Write-Host "================================================================"
Write-Host ""

if ($Uninstall) {
    Write-Host "Uninstalling service: $ServiceName"

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -eq "Running") {
            Write-Host "Stopping service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }

        Write-Host "Removing service..."
        sc.exe delete $ServiceName
        Write-ColorOutput Green "Service uninstalled successfully"
    } else {
        Write-ColorOutput Yellow "Service not found: $ServiceName"
    }

    # Offer to remove firewall rules
    $fwRules = Get-NetFirewallRule | Where-Object { $_.DisplayName -like "LogDB Firewall*" }
    if ($fwRules) {
        $confirm = Read-Host "Remove $($fwRules.Count) managed firewall rules? (y/n)"
        if ($confirm -eq "y") {
            $fwRules | Remove-NetFirewallRule
            Write-ColorOutput Green "Firewall rules removed"
        }
    }

    if (Test-Path $InstallPath) {
        $confirm = Read-Host "Delete installation folder $InstallPath? (y/n)"
        if ($confirm -eq "y") {
            Remove-Item -Path $InstallPath -Recurse -Force
            Write-ColorOutput Green "Installation folder deleted"
        }
    }

    exit 0
}

# Install mode
Write-Host "Installing service..."
Write-Host "  Service Name: $ServiceName"
Write-Host "  Display Name: $DisplayName"
Write-Host "  Install Path: $InstallPath"
Write-Host ""

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-ColorOutput Yellow "Service already exists. Stopping and updating..."
    if ($existingService.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Build the project
Write-Host "Building project..."
$projectPath = Join-Path $ScriptDir "LogDB.Windows.Firewall.csproj"
if (-not (Test-Path $projectPath)) {
    Write-ColorOutput Red "ERROR: Project file not found at $projectPath"
    exit 1
}

$publishPath = Join-Path $ScriptDir "bin\Release\net10.0-windows\win-x64\publish"

Write-Host "Publishing for win-x64..."
dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $publishPath

if ($LASTEXITCODE -ne 0) {
    Write-ColorOutput Red "ERROR: Build failed"
    exit 1
}

# Create install directory
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Write-Host "Created directory: $InstallPath"
}

# Copy files
Write-Host "Copying files to $InstallPath..."
Copy-Item -Path "$publishPath\*" -Destination $InstallPath -Recurse -Force

# Check for appsettings.json
$appSettingsPath = Join-Path $InstallPath "appsettings.json"
$exampleSettingsPath = Join-Path $InstallPath "appsettings.Example.json"

if (-not (Test-Path $appSettingsPath)) {
    if (Test-Path $exampleSettingsPath) {
        Copy-Item -Path $exampleSettingsPath -Destination $appSettingsPath
        Write-ColorOutput Yellow "Created appsettings.json from example - please configure before starting!"
    } else {
        Write-ColorOutput Red "ERROR: No appsettings.json or appsettings.Example.json found"
        exit 1
    }
}

# Get executable path
$exePath = Join-Path $InstallPath "LogDB.Windows.Firewall.exe"

if (-not (Test-Path $exePath)) {
    Write-ColorOutput Red "ERROR: Executable not found at $exePath"
    exit 1
}

# Create the service
Write-Host "Creating Windows service..."
$binPath = "`"$exePath`""

New-Service -Name $ServiceName `
    -BinaryPathName $binPath `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType Automatic

# Configure recovery options (restart on failure)
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000

Write-Host ""
Write-ColorOutput Green "================================================================"
Write-ColorOutput Green "   Installation Complete!"
Write-ColorOutput Green "================================================================"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Edit $appSettingsPath"
Write-Host "     - Enable/disable blocklist sources"
Write-Host "     - Set DryRun=true for first test run"
Write-Host "     - Optionally configure LogDB:ApiKey for custom blocklist"
Write-Host ""
Write-Host "  2. Start the service:"
Write-Host "     Start-Service $ServiceName"
Write-Host ""
Write-Host "  3. Check service status:"
Write-Host "     Get-Service $ServiceName"
Write-Host ""
Write-Host "  4. View managed firewall rules:"
Write-Host "     Get-NetFirewallRule | Where { `$_.DisplayName -like 'LogDB Firewall*' }"
Write-Host ""

$startNow = Read-Host "Start service now? (y/n)"
if ($startNow -eq "y") {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    $status = (Get-Service -Name $ServiceName).Status
    if ($status -eq "Running") {
        Write-ColorOutput Green "Service is running"
    } else {
        Write-ColorOutput Yellow "Service status: $status"
    }
}
