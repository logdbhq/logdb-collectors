param(
    [string]$ServiceName = "LogDBWindowsCollector"
)

$ErrorActionPreference = "Stop"
sc.exe stop $ServiceName | Out-Host
sc.exe query $ServiceName | Out-Host
