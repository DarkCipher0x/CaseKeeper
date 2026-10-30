# Publishes a release's sanitised source to the GitHub repository DarkCipher0x/CaseKeeper. build-release.ps1 runs it
# twice: with -NoPush in step 5 (commit and tag here only, so the release's source zip can be git archive of the tag),
# then without it in step 7, which pushes that same tag (skip with -NoPublish).
#
# Source of truth: the zip write-public-source.ps1 wrote and privacy-scanned (-Zip, or dist\<ver>\ or dist\). Nothing
# else from this folder is ever copied: not its local git history, not dist\ or publish\, not the local term list.
#
# The public clone lives next to this folder (..\CaseKeeper-public, or CK_PUBLIC_REPO) with origin
# https://github.com/DarkCipher0x/CaseKeeper.git. Each release becomes one commit "CaseKeeper <ver>" and a tag v<ver>,
# authored as DarkCipher0x with the GitHub no-reply address. The public tree is replaced wholesale, so edit the README
# and .github\ here, never on GitHub (an edit made there is overwritten by the next release).
#
# Gates, before anything is committed:
#   - every image must be listed by SHA-256 in tools\approved-images.txt (someone has looked at it; scans can't see pixels);
#   - no debug symbols, certificates, key files or packages (*.pdb, *.pfx, *.p12, *.snk, *.key, *.pem, *.cer, *.msix);
#   - the clone must be clean and on main, and the tag must not be on GitHub yet (a version is published once).
# A tag that already exists here but not on GitHub (made by -NoPush) is pushed as it is, never remade.
#
# Usage: .\tools\publish-public.ps1 -Version 0.4.1 [-Zip <path>] [-NoPush]
param([Parameter(Mandatory)][string]$Version, [string]$Zip, [switch]$NoPush)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$git = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $git)) { $git = 'git' }
$repo = if ($env:CK_PUBLIC_REPO) { $env:CK_PUBLIC_REPO } else { Join-Path (Split-Path $root -Parent) 'CaseKeeper-public' }
$name = "CaseKeeper-src-v$Version"
$zip = $Zip
if (-not $zip) { $zip = @((Join-Path $root "dist\$Version\$name.zip"), (Join-Path $root "dist\$name.zip")) | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $zip) { $zip = Join-Path $root "dist\$Version\$name.zip" }
$identity = @('-c', 'user.name=DarkCipher0x', '-c', 'user.email=DarkCipher0x@users.noreply.github.com')

function Git { & $git -C $repo @args; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed in $repo" } }

function Push-Tag {
    Git push -q origin main
    Git push -q origin "v$Version"
    $count = (& $git -C $repo ls-files | Measure-Object).Count
    Write-Host "Public source $Version pushed to GitHub ($count files, tag v$Version)." -ForegroundColor Green
}

if (-not (Test-Path (Join-Path $repo '.git'))) { throw "no public clone at $repo - clone https://github.com/DarkCipher0x/CaseKeeper.git there first" }

# 1. The clone: clean, on main, and this version not yet on GitHub.
if (& $git -C $repo status --porcelain) { throw "the public clone $repo has local changes - commit or discard them first" }
Git checkout -q main
$remoteTag = & $git -C $repo ls-remote --tags origin "refs/tags/v$Version"
if ($LASTEXITCODE) { throw "can't reach GitHub from $repo" }
if ($remoteTag) { throw "v$Version is already published on GitHub" }
if (& $git -C $repo tag -l "v$Version") {
    # Tagged by an earlier -NoPush run, and the release's source zip is git archive of exactly this tag: push it as it is.
    if ($NoPush) { throw "v$Version is already tagged in $repo, not pushed yet. To push it: .\tools\publish-public.ps1 -Version $Version" }
    if ((& $git -C $repo rev-parse "v$Version^{commit}") -ne (& $git -C $repo rev-parse HEAD)) { throw "the tag v$Version in $repo is not the tip of main" }
    Push-Tag
    return
}
if (-not (Test-Path $zip)) { throw "no sanitised source for $Version ($zip): run tools\write-public-source.ps1 -Version $Version first" }
Git pull -q --ff-only origin main

# 2. Unpack the archive to a private staging folder.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('ck-public-' + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
try {
    $top = Join-Path $stage $name
    if (-not (Test-Path (Join-Path $top 'README.md'))) { throw "the archive has no $name\README.md" }

    # 3. Gates.
    $approved = Get-Content (Join-Path $PSScriptRoot 'approved-images.txt') | Where-Object { $_ -match '^[0-9a-f]{64}\s' } | ForEach-Object { $_.Substring(0, 64) }
    $images = Get-ChildItem $top -Recurse -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|gif|bmp|ico|svg|webp|tiff?)$' }
    $unseen = $images | Where-Object { $approved -notcontains (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ($unseen) { throw "image(s) nobody has looked at yet - view each, then add its SHA-256 to tools\approved-images.txt:`n  " + (($unseen | ForEach-Object { $_.FullName.Substring($top.Length + 1) }) -join "`n  ") }
    $forbidden = Get-ChildItem $top -Recurse -File | Where-Object { $_.Extension -match '^\.(pdb|pfx|p12|snk|key|pem|cer|pvk|msix|appx)$' }
    if ($forbidden) { throw "files that must never be published: " + (($forbidden | ForEach-Object { $_.FullName.Substring($top.Length + 1) }) -join ', ') }

    # 4. Replace the public tree with the release's (everything but .git).
    Get-ChildItem $repo -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
    Get-ChildItem $top -Force | Move-Item -Destination $repo
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

# 5. One commit and a tag per release.
Git -c core.safecrlf=false add -A
if (& $git -C $repo status --porcelain) { Git @identity commit -q -m "CaseKeeper $Version - sanitised public source" }
else { Write-Host "The public tree already matches $Version; tagging only." }
Git @identity tag -a "v$Version" -m "CaseKeeper $Version"

# 6. Push (skipped with -NoPush: the commit and tag wait in the clone for a later push).
if ($NoPush) {
    $count = (& $git -C $repo ls-files | Measure-Object).Count
    Write-Host "Public source $Version committed and tagged in $repo ($count files), not pushed yet."
    return
}
Push-Tag
