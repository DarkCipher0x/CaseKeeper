<#
    Builds a CaseKeeper release installer.

    To cut a new version:
      1. Change <Version> in CaseKeeper\CaseKeeper.csproj  (e.g. 0.1.0 -> 0.2.0)
      2. Add a section for it at the top of CHANGELOG.md
      3. Run:  powershell -ExecutionPolicy Bypass -File build-release.ps1

    The full release package lands in dist\:
      CaseKeeper-Setup-v<version>.exe   the installer
      CaseKeeper-src-v<version>.zip     the sanitised, privacy-scanned source (tools\write-public-source.ps1)
      SHA256SUMS.txt                    checksums for both

    Last step: the same sanitised source is committed, tagged v<version> and pushed to
    https://github.com/DarkCipher0x/CaseKeeper by tools\publish-public.ps1. -NoPublish skips it
    (run that script by hand later).
#>
[CmdletBinding()]
param(
    # Skip the dotnet publish step and just repackage what is already in publish\
    [switch]$SkipBuild,
    # Build the package but don't publish the source to GitHub
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'CaseKeeper\CaseKeeper.csproj'
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

# --- version comes from the project file, and nowhere else -------------------
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "Building CaseKeeper v$version" -ForegroundColor Cyan

# --- refuse to build a version that isn't written up ------------------------
$changelog = Join-Path $root 'CHANGELOG.md'
if ((Test-Path $changelog) -and -not (Select-String -Path $changelog -Pattern ([regex]::Escape("v$version")) -Quiet)) {
    Write-Warning "CHANGELOG.md has no entry for v$version. Add one so the release has patch notes."
}

# --- publish ----------------------------------------------------------------
if (-not $SkipBuild) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host 'Publishing (self-contained, so users need nothing else installed)...' -ForegroundColor Cyan
    & dotnet publish $project -c Release -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

$exe = Join-Path $publishDir 'CaseKeeper.exe'
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }
$stamped = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Published exe reports version $stamped" -ForegroundColor DarkGray

# --- package ----------------------------------------------------------------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"   # winget installs here for a single user
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup"
}

New-Item -ItemType Directory -Force $distDir | Out-Null
Write-Host 'Packaging installer...' -ForegroundColor Cyan
& $iscc /Q (Join-Path $root 'installer\CaseKeeper.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $distDir "CaseKeeper-Setup-v$version.exe"
if (-not (Test-Path $setup)) { throw "Expected installer not found at $setup" }

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)

# --- the sanitised source and the checksums ----------------------------------
# On CI there's no local term list or public clone; the generic privacy rules still run.
& (Join-Path $root 'tools\write-public-source.ps1') -Version $version
$source = Join-Path $distDir "CaseKeeper-src-v$version.zip"
$sums = Join-Path $distDir 'SHA256SUMS.txt'
Get-Item $setup, $source |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content $sums -Encoding ascii

Write-Host ''
Write-Host "Done: $setup ($sizeMb MB)" -ForegroundColor Green
Get-Content $sums | ForEach-Object { Write-Host "  $_" }
Write-Host ''
Write-Host 'Attach the .exe, the source zip and SHA256SUMS.txt to the GitHub release, and publish the'
Write-Host 'SHA-256s on the website, so people can check the download has not been tampered with.'

# --- the public source on GitHub -------------------------------------------------
if ($NoPublish -or $env:CI) {
    Write-Host "Public source not published: .\tools\publish-public.ps1 -Version $version" -ForegroundColor Yellow
}
else {
    & (Join-Path $root 'tools\publish-public.ps1') -Version $version
}
