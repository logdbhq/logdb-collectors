param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [switch]$SelfContained = $true,
    [switch]$CreateZip = $true
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    $global:LASTEXITCODE = 0
    & $Command

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$serviceProject = Join-Path $repoRoot "com.logdb.windows.collector\com.logdb.windows.collector.csproj"
$uiProject = Join-Path $repoRoot "com.logdb.windows.collector.ui\com.logdb.windows.collector.ui.csproj"
$readmePath = Join-Path $repoRoot "com.logdb.windows.collector\README.md"

if (-not (Test-Path $serviceProject)) { throw "Service project not found: $serviceProject" }
if (-not (Test-Path $uiProject)) { throw "UI project not found: $uiProject" }

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "releases\windows-collector"
}

$uiVersion = "1.0.0"
try {
    [xml]$uiXml = Get-Content -Path $uiProject
    $v = $uiXml.Project.PropertyGroup.Version | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($v)) {
        $uiVersion = $v
    }
}
catch {
    Write-Warning "Unable to parse UI version from csproj. Using $uiVersion"
}

$bundleName = "LogDB-Windows-Collector-v$uiVersion-$Runtime"
$bundleDir = Join-Path $OutputRoot $bundleName
$serviceOut = Join-Path $bundleDir "service"
$uiOut = Join-Path $bundleDir "ui"
$scriptsOut = Join-Path $bundleDir "scripts"

if (Test-Path $bundleDir) {
    Remove-Item -Recurse -Force $bundleDir
}
New-Item -ItemType Directory -Path $serviceOut -Force | Out-Null
New-Item -ItemType Directory -Path $uiOut -Force | Out-Null
New-Item -ItemType Directory -Path $scriptsOut -Force | Out-Null

Write-Host "Publishing service..."
Invoke-CheckedCommand -Description "Service publish" -Command {
    dotnet publish $serviceProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained:$SelfContained `
        -p:PublishSingleFile=false `
        -o $serviceOut
}

Write-Host "Publishing UI..."
Invoke-CheckedCommand -Description "UI publish" -Command {
    dotnet publish $uiProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained:$SelfContained `
        -p:PublishSingleFile=false `
        -o $uiOut
}

$scriptNames = @(
    "install-service.ps1",
    "uninstall-service.ps1",
    "start-service.ps1",
    "stop-service.ps1",
    "apply-firewall.ps1",
    "publish-windows-collector.ps1",
    "package-collector-ui-velopack.ps1"
)

foreach ($scriptName in $scriptNames) {
    $source = Join-Path $repoRoot ("scripts\" + $scriptName)
    if (Test-Path $source) {
        Copy-Item $source -Destination $scriptsOut -Force
    }
}

if (Test-Path $readmePath) {
    Copy-Item $readmePath -Destination (Join-Path $bundleDir "README.md") -Force
}

$installBat = @"
@echo off
powershell -ExecutionPolicy Bypass -File ".\scripts\install-service.ps1" -ExecutablePath ".\service\com.logdb.windows.collector.exe" -Start
pause
"@
$installBat | Set-Content -Path (Join-Path $bundleDir "install-collector.bat") -Encoding ASCII

$runControllerBat = @"
@echo off
start "" ".\ui\com.logdb.windows.collector.ui.exe"
"@
$runControllerBat | Set-Content -Path (Join-Path $bundleDir "run-controller.bat") -Encoding ASCII

if ($CreateZip) {
    $zipPath = Join-Path $OutputRoot ($bundleName + ".zip")
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $bundleDir "*") -DestinationPath $zipPath -Force
    Write-Host "Created zip: $zipPath"
}

Write-Host "Bundle ready: $bundleDir"
