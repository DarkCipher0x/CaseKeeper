<#
    Builds and publishes a CaseKeeper release, start to finish, in one command.

    To cut a new version (or a point release):
      1. Change <Version> in CaseKeeper\CaseKeeper.csproj  (e.g. 0.4.0 -> 0.4.1); RELEASING.md lists the four lines
      2. Add a "## v0.4.1 — <date>" section at the top of CHANGELOG.md
      3. Run:  powershell -ExecutionPolicy Bypass -File build-release.ps1

    What it does, stopping at the first problem:
      1. Checks the version is new (never overwrites a release) and has a changelog section
      2. Runs the three test programs
      3. Publishes the app (self-contained .NET) and builds the installer with Inno Setup
      4. Builds the Microsoft Store package (.msix) from the same build      (build-store.ps1)
      5. Privacy-scans the source (tools\write-public-source.ps1), commits and tags it in the public clone without
         pushing (tools\publish-public.ps1 -NoPush), and writes the source zip as git archive of that tag
      6. Keeps the full package in dist\<version>\ with SHA256SUMS.txt:
           CaseKeeper-Setup-v<ver>.exe, CaseKeeper-src-v<ver>.zip, CaseKeeper-v<ver>-x64.msix,
           CHANGELOG.md, USER-GUIDE.md, STORE.md
      7. Pushes that tag to GitHub                                           (tools\publish-public.ps1)
      8. Creates the GitHub release and attaches the installer, source zip and checksums
                                                                             (tools\publish-github-release.ps1)

    Options:
      -NoPublish    stop after step 6 (nothing leaves this PC); run steps 7-8 later with -PublishOnly
      -PublishOnly  only steps 7-8, for a version already built into dist\<version>\
      -Draft        create the GitHub release as a draft, to read over before publishing
      -NoStore      skip the .msix
      -SkipTests    skip step 2 (not for real releases)
      -SkipBuild    reuse what is already in publish\ instead of running dotnet publish
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$NoStore,
    [switch]$NoPublish,
    [switch]$PublishOnly,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'CaseKeeper\CaseKeeper.csproj'
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'
$onCi = [bool]$env:CI

# --- version comes from the project file, and nowhere else -------------------
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> found in $project" }
$out = Join-Path $distDir $version
$setupName = "CaseKeeper-Setup-v$version.exe"

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Publish-Release {
    Step "Publishing v$version to GitHub"
    & (Join-Path $root 'tools\publish-public.ps1') -Version $version
    & (Join-Path $root 'tools\publish-github-release.ps1') -Version $version -Draft:$Draft
}

if ($PublishOnly) {
    if (-not (Test-Path (Join-Path $out $setupName))) { throw "dist\$version\ has no $setupName - build it first" }
    Publish-Release
    return
}

Write-Host "Building CaseKeeper v$version" -ForegroundColor Cyan

# --- 1. a new version, written up -----------------------------------------------
if (Test-Path (Join-Path $out $setupName)) {
    throw "v$version is already built in dist\$version\. Bump <Version> in CaseKeeper.csproj for a new release (published versions are never overwritten), or use -PublishOnly to publish it."
}
$publicRepo = Join-Path (Split-Path $root -Parent) 'CaseKeeper-public'
if (-not $onCi -and -not (Test-Path (Join-Path $publicRepo '.git'))) {
    throw "no public clone at $publicRepo - clone https://github.com/DarkCipher0x/CaseKeeper.git there first (the source zip is made from it)"
}
if (-not $onCi -and (& git -C $publicRepo tag -l "v$version")) {
    throw "v$version is already tagged in $publicRepo. If it is on GitHub, bump <Version> in CaseKeeper.csproj. If an unfinished build left it and it was never pushed, remove it first:  git -C $publicRepo tag -d v$version; git -C $publicRepo reset --hard origin/main"
}
$changelog = Join-Path $root 'CHANGELOG.md'
if (-not (Select-String -Path $changelog -Pattern "^## v$([regex]::Escape($version))\b" -Quiet)) {
    throw "CHANGELOG.md has no '## v$version' section. Add the patch notes first: they become the release notes."
}

# --- 2. tests --------------------------------------------------------------------
if (-not $SkipTests) {
    Step 'Running the tests'
    $testOut = Join-Path ([IO.Path]::GetTempPath()) ('ck-test-output-' + [guid]::NewGuid().ToString('N'))
    $tests = @(
        @{ Name = 'Document engine'; Args = @('run', '--project', (Join-Path $root 'tests\CaseKeeper.Tests.Docs'), '--', $testOut) },
        @{ Name = 'Data layer'; Args = @('run', '--project', (Join-Path $root 'tests\CaseKeeper.Tests.Data')) },
        @{ Name = 'Bulk Case Import'; Args = @('run', '--project', (Join-Path $root 'tests\CaseKeeper.Tests.BulkImport')) }
    )
    foreach ($t in $tests) {
        $log = & dotnet @($t.Args)
        $code = $LASTEXITCODE
        $fails = @($log | Where-Object { $_ -match '\bFAIL\b' })
        if ($code -ne 0) { $fails | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; throw "$($t.Name) tests failed ($code failure(s))" }
        Write-Host "  $($t.Name) tests passed"
    }
    Remove-Item $testOut -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 3. publish and installer -------------------------------------------------------
if (-not $SkipBuild) {
    Step 'Publishing the app (self-contained, so users need nothing else installed)'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & dotnet publish $project -c Release -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
$exe = Join-Path $publishDir 'CaseKeeper.exe'
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }
$stamped = (Get-Item $exe).VersionInfo.ProductVersion
if ($stamped -notlike "$version*") { throw "The published exe says $stamped, not $version - rebuild without -SkipBuild" }

Step 'Building the installer'
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"   # winget installs here for a single user
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup" }
New-Item -ItemType Directory -Force $distDir, $out | Out-Null
& $iscc /Q (Join-Path $root 'installer\CaseKeeper.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
$built = Join-Path $distDir $setupName
if (-not (Test-Path $built)) { throw "Expected installer not found at $built" }
Move-Item $built (Join-Path $out $setupName) -Force

# --- 4. Microsoft Store package -------------------------------------------------------
if (-not $NoStore -and -not $onCi) {
    Step 'Building the Microsoft Store package'
    & (Join-Path $root 'build-store.ps1') -SkipBuild
    $msix = Get-ChildItem $distDir -Filter "CaseKeeper-v$version-x64*.msix" | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $msix) { throw 'build-store.ps1 did not produce an .msix' }
    Move-Item $msix.FullName (Join-Path $out $msix.Name) -Force
}

# --- 5. sanitised source --------------------------------------------------------------
# The scanned source is committed and tagged in the public clone (not pushed yet), and the zip that ships is git archive
# of that tag: exactly what GitHub will hold, one top folder, forward slashes, and anyone can re-create it from the tag.
Step 'Writing the sanitised source'
$srcName = "CaseKeeper-src-v$version"
if ($onCi) {
    & (Join-Path $root 'tools\write-public-source.ps1') -Version $version -OutDir $out
}
else {
    $scanned = Join-Path ([IO.Path]::GetTempPath()) ('ck-source-' + [guid]::NewGuid().ToString('N'))
    try {
        & (Join-Path $root 'tools\write-public-source.ps1') -Version $version -OutDir $scanned
        & (Join-Path $root 'tools\publish-public.ps1') -Version $version -Zip (Join-Path $scanned "$srcName.zip") -NoPush
    }
    finally { Remove-Item $scanned -Recurse -Force -ErrorAction SilentlyContinue }
    & git -C $publicRepo archive --format=zip --prefix="$srcName/" -o (Join-Path $out "$srcName.zip") "v$version"
    if ($LASTEXITCODE) { throw "git archive of v$version failed in $publicRepo" }
    Write-Host "  $srcName.zip = git archive of the public tag v$version"
}

# --- 6. the local package --------------------------------------------------------------
Copy-Item $changelog, (Join-Path $root 'USER-GUIDE.md'), (Join-Path $root 'docs\STORE.md') $out
Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
    Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

Step "Release package: dist\$version\"
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-38} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ''
Get-Content (Join-Path $out 'SHA256SUMS.txt') | ForEach-Object { Write-Host "  $_" }

# --- the private repository: record exactly what this release was built from -----------------
if (-not $onCi -and (Test-Path (Join-Path $root '.git'))) {
    $ident = @('-c', 'user.name=DarkCipher0x', '-c', 'user.email=DarkCipher0x@users.noreply.github.com')
    & git -C $root add -A
    if (& git -C $root status --porcelain) { & git -C $root @ident commit -q -m "Release v$version" }
    if (-not (& git -C $root tag -l "v$version")) { & git -C $root @ident tag -a "v$version" -m "CaseKeeper $version" }
    Write-Host "Private repository: committed and tagged v$version (local only)"
}

# --- 7-8. GitHub ------------------------------------------------------------------------
if ($NoPublish -or $onCi) {
    Write-Host ''
    Write-Host "Not published to GitHub. When ready:  .\build-release.ps1 -PublishOnly" -ForegroundColor Yellow
}
else {
    Publish-Release
}

Write-Host ''
Write-Host 'Still to do by hand:' -ForegroundColor Cyan
Write-Host "  - put the installer, source zip and SHA-256s from dist\$version\ on the website"
if (Get-ChildItem $out -Filter '*.msix' -ErrorAction SilentlyContinue) {
    Write-Host '  - upload the .msix in Partner Center (a new Store submission; see docs\STORE.md)'
}
