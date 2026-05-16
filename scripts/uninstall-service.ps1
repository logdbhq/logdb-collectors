param(
    [string]$ServiceName = "LogDBWindowsCollector"
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script as Administrator."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "Service '$ServiceName' does not exist."
    exit 0
}

try {
    sc.exe stop $ServiceName | Out-Host
    Start-Sleep -Seconds 1
}
catch {
    Write-Warning "Failed to stop service (may already be stopped): $_"
}

sc.exe delete $ServiceName | Out-Host
Write-Host "Service '$ServiceName' removed."
