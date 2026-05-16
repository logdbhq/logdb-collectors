param(
    [string]$ServiceName = "LogDBWindowsCollector"
)

$ErrorActionPreference = "Stop"
sc.exe start $ServiceName | Out-Host
sc.exe query $ServiceName | Out-Host
