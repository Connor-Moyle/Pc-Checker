<#
.SYNOPSIS
    Builds, tests, publishes and packages PC Checker on a local Windows machine.

.DESCRIPTION
    Reproduces the release workflow: a self-contained single-file win-x64 publish,
    a Velopack package (installer + update package), a portable zip and a
    SHA256SUMS.txt. Optionally captures demo-mode screenshots from the published exe.

.EXAMPLE
    .\build\publish.ps1 -Version 0.1.0

.EXAMPLE
    .\build\publish.ps1 -Version 0.2.0-beta.1 -SkipPack -Screenshots
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts",
    [switch]$SkipTests,
    [switch]$SkipPack,
    [switch]$Screenshots
)

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $Version) {
        $props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
        $Version = $props.Project.PropertyGroup.VersionPrefix
        Write-Host "No -Version given; using VersionPrefix $Version from Directory.Build.props."
    }

    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "'$Version' is not a valid semantic version (expected X.Y.Z or X.Y.Z-prerelease)."
    }

    $out = Join-Path $root $OutputRoot
    $publishDir = Join-Path $out "publish"
    $releasesDir = Join-Path $out "Releases"
    $zipPath = Join-Path $out "PcChecker-$Version-portable-$Runtime.zip"
    $sumsPath = Join-Path $out "SHA256SUMS.txt"
    $appProject = "src/PcChecker.App/PcChecker.App.csproj"

    Write-Host "== PC Checker $Version ($Configuration, $Runtime) -> $out" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Write-Host "== Restore and build" -ForegroundColor Cyan
    dotnet restore PcChecker.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
    dotnet build PcChecker.sln -c $Configuration --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    if (-not $SkipTests) {
        Write-Host "== Test" -ForegroundColor Cyan
        dotnet test PcChecker.sln -c $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
    }

    Write-Host "== Publish (self-contained, single file)" -ForegroundColor Cyan
    dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $exe = Join-Path $publishDir "PcChecker.exe"
    if (-not (Test-Path $exe)) { throw "Expected $exe after publish." }

    if (-not $SkipPack) {
        $vpk = Get-Command vpk -ErrorAction SilentlyContinue
        if ($null -eq $vpk) {
            Write-Warning "The Velopack CLI (vpk) is not installed; skipping packaging. Install it with: dotnet tool install -g vpk"
        }
        else {
            Write-Host "== Velopack pack" -ForegroundColor Cyan
            if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
            vpk pack --packId PcChecker --packVersion $Version --packDir $publishDir --mainExe PcChecker.exe `
                --packTitle "PC Checker" --packAuthors "PC Checker contributors" `
                --icon src/PcChecker.App/Assets/icon.ico --noPortable --outputDir $releasesDir
            if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
        }
    }

    Write-Host "== Portable zip" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "== Checksums" -ForegroundColor Cyan
    $files = @()
    if (Test-Path $releasesDir) { $files += Get-ChildItem $releasesDir -File }
    $files += Get-Item $zipPath
    $lines = foreach ($f in $files) {
        $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
        "$hash  $($f.Name)"
    }
    Set-Content -Path $sumsPath -Value ($lines -join "`n") -NoNewline
    Get-Content $sumsPath

    if ($Screenshots) {
        Write-Host "== Screenshots (demo mode)" -ForegroundColor Cyan
        $shotDir = Join-Path $out "screenshots"
        if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
        $p = Start-Process -FilePath $exe -ArgumentList @("--screenshots", $shotDir, "--demo") -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "The screenshot runner failed with exit code $($p.ExitCode)." }
        Get-ChildItem $shotDir | Format-Table Name, Length
    }

    Write-Host "== Done" -ForegroundColor Green
    Write-Host "  Publish:   $publishDir"
    if (Test-Path $releasesDir) { Write-Host "  Releases:  $releasesDir" }
    Write-Host "  Portable:  $zipPath"
    Write-Host "  Checksums: $sumsPath"
}
finally {
    Pop-Location
}
