param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$ReleaseDir = "",
    [string]$LocalFeedPath = "",
    [switch]$SelfContained = $true
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

function Assert-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$InstallHint
    )

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found. $InstallHint"
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$uiProject = Join-Path $repoRoot "com.logdb.windows.collector.ui\com.logdb.windows.collector.ui.csproj"
$serviceProject = Join-Path $repoRoot "com.logdb.windows.collector\com.logdb.windows.collector.csproj"

if (-not (Test-Path $uiProject)) {
    throw "UI project not found: $uiProject"
}
if (-not (Test-Path $serviceProject)) {
    throw "Service project not found: $serviceProject"
}

if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $repoRoot "releases\collector-ui-velopack"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$uiXml = Get-Content -Path $uiProject
    $v = $uiXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($v)) {
        throw "Version not found in $uiProject. Pass -Version explicitly."
    }
    $Version = $v
}

Assert-CommandAvailable -CommandName "vpk" -InstallHint "Install Velopack CLI with: dotnet tool install -g vpk"

$publishDir = Join-Path $repoRoot "artifacts\collector-ui-velopack\publish\$Runtime"
$serviceSubDir = Join-Path $publishDir "service"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $serviceSubDir -Force | Out-Null
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

# UI publishes to the package ROOT so com.logdb.windows.collector.ui.exe is the
# Velopack --mainExe. Service publishes to <root>/service/. The Velopack-managed
# install dir layout ends up as:
#   <install>/current/com.logdb.windows.collector.ui.exe
#   <install>/current/service/com.logdb.windows.collector.exe
# The service's binPath in the SCM registration points at the latter path; in-place
# updates rewrite both directories atomically when Velopack swaps the current/
# junction. UI's OnBeforeUpdate / OnAfterUpdate hooks stop/start the service so
# the swap doesn't fail on file locks.
Write-Host "Publishing collector UI for Velopack (root of package)..."
Invoke-CheckedCommand -Description "UI publish" -Command {
    dotnet publish $uiProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained:$SelfContained `
        -p:PublishSingleFile=false `
        -o $publishDir
}

Write-Host "Publishing collector service into <package>/service/..."
Invoke-CheckedCommand -Description "Service publish" -Command {
    dotnet publish $serviceProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained:$SelfContained `
        -p:PublishSingleFile=false `
        -o $serviceSubDir
}

Write-Host "Packing Velopack release..."
Invoke-CheckedCommand -Description "Velopack pack" -Command {
    vpk pack `
        --packId "com.logdb.windows.collector.ui" `
        --packVersion $Version `
        --packDir $publishDir `
        --runtime $Runtime `
        --outputDir $ReleaseDir `
        --mainExe "com.logdb.windows.collector.ui.exe" `
        --packTitle "LogDB Windows Collector Controller" `
        --packAuthors "LogDB"
}

if (-not [string]::IsNullOrWhiteSpace($LocalFeedPath)) {
    New-Item -ItemType Directory -Path $LocalFeedPath -Force | Out-Null
    Write-Host "Publishing release metadata to local feed..."
    Invoke-CheckedCommand -Description "Velopack local upload" -Command {
        vpk upload local --outputDir $ReleaseDir --channel win --path $LocalFeedPath --regenerate
    }
    Write-Host "Local feed updated: $LocalFeedPath"
}

Write-Host "Velopack release ready: $ReleaseDir"
