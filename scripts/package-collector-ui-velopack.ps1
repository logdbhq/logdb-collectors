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

if (-not (Test-Path $uiProject)) {
    throw "UI project not found: $uiProject"
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
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

Write-Host "Publishing collector UI for Velopack..."
Invoke-CheckedCommand -Description "UI publish" -Command {
    dotnet publish $uiProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained:$SelfContained `
        -p:PublishSingleFile=false `
        -o $publishDir
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
