#!/usr/bin/env pwsh
# Build a Velopack release for CSUploader.
#
# Usage:
#   ./publish.ps1                          # uses <Version> from csproj
#   ./publish.ps1 -Version 1.2.3           # override version
#   ./publish.ps1 -Push                    # also create a GitHub Release via `gh`
#
# Output: ./releases/CSUploader-{version}-full.nupkg + RELEASES + Setup.exe

[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Push,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Repo       = "CSUploader/CSUploader"
$PackId     = "CSUploader"
$Project    = "src/CSUploader.csproj"
$MainExe    = "CSUploader.exe"
$PublishDir = "publish"
$ReleaseDir = "releases"

if (-not $Version) {
    [xml]$proj = Get-Content $Project
    $Version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ })[0].ToString().Trim()
    if (-not $Version) { throw "Could not read <Version> from $Project — pass -Version explicitly." }
}

Write-Host "==> CSUploader $Version ($Runtime, $Configuration)"

# 1. vpk CLI
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing Velopack CLI (vpk)"
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw "vpk install failed" }
}

# 2. Clean output
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }

# 3. Publish self-contained
Write-Host "==> dotnet publish"
dotnet publish $Project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# 4. Pack via Velopack
Write-Host "==> vpk pack"
vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe $MainExe `
    --outputDir $ReleaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "==> Built artifacts in $ReleaseDir"
Get-ChildItem $ReleaseDir | ForEach-Object { Write-Host "    $($_.Name)" }

# 5. Optionally push a GitHub Release
if ($Push) {
    $tag = "v$Version"
    Write-Host "==> Creating GitHub Release $tag on $Repo"

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found — install it or run vpk upload github manually."
    }

    $assets = Get-ChildItem $ReleaseDir | ForEach-Object { $_.FullName }
    gh release create $tag $assets `
        --repo $Repo `
        --title "CSUploader $Version" `
        --notes "Automated release — see commit history for changes." `
        --verify-tag:$false
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Write-Host "==> Released $tag"
}
else {
    Write-Host "==> Skipping push (use -Push to create a GitHub Release)"
}
