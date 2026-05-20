param(
    [string]$ExecutablePath = "",
    [string]$ServiceName = "LogDBWindowsCollector",
    [string]$DisplayName = "LogDB Windows Collector",
    [switch]$Start
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script as Administrator."
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    # Candidate paths, in priority order:
    # 1. Velopack-managed install (machine-wide ProgramFiles): the service exe ships
    #    inside the Velopack package next to the UI, at <install>\current\service\.
    #    Registering the service with this path lets Velopack-driven UI updates also
    #    update the service in place (UI's Velopack hooks stop/start the service
    #    around the file swap — see com.logdb.windows.collector.ui/Program.cs).
    # 2. Velopack-managed install (per-user LocalAppData): same idea but for per-user
    #    Velopack installs. Note: a service running as LocalSystem cannot read files
    #    under another user's LocalAppData; prefer machine-wide Velopack install for
    #    multi-user / production scenarios.
    # 3. Same-folder fallback for the raw release zip layout (LogDB-Windows-Collector-vX.Y.Z-win-x64.zip
    #    extracts to a tree where install-service.ps1 lives in scripts/ and the exe in service/).
    # 4. The dev bin output.
    # 5. The legacy hard-coded path.
    $velopackUI = "com.logdb.windows.collector.ui"
    $candidates = @(
        (Join-Path $env:ProgramFiles "$velopackUI\current\service\com.logdb.windows.collector.exe"),
        (Join-Path $env:LOCALAPPDATA "$velopackUI\current\service\com.logdb.windows.collector.exe"),
        (Join-Path $PSScriptRoot "..\service\com.logdb.windows.collector.exe"),
        (Join-Path $PSScriptRoot "..\com.logdb.windows.collector.exe"),
        (Join-Path $PSScriptRoot "..\com.logdb.windows.collector\bin\Release\net10.0-windows\com.logdb.windows.collector.exe"),
        "C:\Program Files\LogDB\collector\com.logdb.windows.collector.exe"
    )

    foreach ($candidate in $candidates) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path $full) {
            $ExecutablePath = $full
            break
        }
    }
}

if (-not (Test-Path $ExecutablePath)) {
    throw "Collector executable not found. Pass -ExecutablePath with a valid path."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Write-Host "Service '$ServiceName' already exists. Recreating..."
    sc.exe stop $ServiceName | Out-Null
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

$binPath = '"' + $ExecutablePath + '"'
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName | Out-Host
sc.exe description $ServiceName "Unified LogDB Windows collector hosting Event Log, IIS, Metrics, and optional firewall utility." | Out-Host

$configDir = Join-Path $env:ProgramData "LogDB\collector"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$configPath = Join-Path $configDir "appsettings.json"
if (-not (Test-Path $configPath)) {
    @"
{
  "logDB": {
    "apiKey": "",
    "endpoint": "",
    "discoveryUrl": "https://discovery.logdb.site/resolve/grpc-logger",
    "protocol": "Native",
    "defaultApplication": "LogDB Collector",
    "defaultEnvironment": "Production",
    "defaultCollection": "windows",
    "retry": { "maxRetries": 3, "enableCircuitBreaker": true },
    "batch": { "enableBatching": false, "batchSize": 100, "flushIntervalSeconds": 5, "enableCompression": true }
  },
  "server": {
    "serverName": "$env:COMPUTERNAME",
    "serverEnvironment": "Production",
    "defaultLabels": [ "windows", "collector" ]
  },
  "modules": {
    "eventLog": {
      "enabled": true,
      "pollIntervalSeconds": 60,
      "sourcesChannels": [ "System", "Application" ],
      "levelFilters": [ "error", "warning", "information" ],
      "resetState": false
    },
    "iis": {
      "enabled": false,
      "pollIntervalSeconds": 60,
      "logDirectories": [],
      "stateFilePath": "",
      "siteName": "",
      "include4xx": true,
      "include5xx": true
    },
    "metrics": {
      "enabled": true,
      "pollIntervalSeconds": 60,
      "includeCpu": true,
      "includeMemory": true,
      "includeDisk": true,
      "includeNetwork": true,
      "tags": {}
    }
  },
  "firewall": {
    "enabled": false,
    "ruleName": "LogDB Windows Collector",
    "ports": [],
    "direction": "Inbound",
    "programPath": ""
  }
}
"@ | Set-Content -Path $configPath -Encoding UTF8
}

if ($Start) {
    sc.exe start $ServiceName | Out-Host
}

Write-Host "Installed service '$ServiceName' with executable: $ExecutablePath"
Write-Host "Config path: $configPath"
