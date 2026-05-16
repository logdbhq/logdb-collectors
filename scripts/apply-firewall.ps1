param(
    [string]$RuleName = "LogDB Windows Collector",
    [int[]]$Ports = @(),
    [ValidateSet("Inbound", "Outbound")] [string]$Direction = "Inbound",
    [string]$ProgramPath = "",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script as Administrator."
}

if ($Ports.Count -eq 0) {
    throw "Specify one or more ports using -Ports."
}

foreach ($port in ($Ports | Sort-Object -Unique)) {
    $name = "$RuleName ($Direction:$port)"

    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule | Out-Null

    if (-not $Remove) {
        if ([string]::IsNullOrWhiteSpace($ProgramPath)) {
            New-NetFirewallRule -DisplayName $name -Direction $Direction -Action Allow -Protocol TCP -LocalPort $port -Profile Any | Out-Null
        }
        else {
            New-NetFirewallRule -DisplayName $name -Direction $Direction -Action Allow -Protocol TCP -LocalPort $port -Program $ProgramPath -Profile Any | Out-Null
        }
    }
}

if ($Remove) {
    Write-Host "Removed firewall rules for $RuleName."
}
else {
    Write-Host "Applied firewall rules for $RuleName."
}
