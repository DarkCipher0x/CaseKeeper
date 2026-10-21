# Writes the sanitised public source archive of one release, so every release carries the source it was built from
# in a form fit for publication:
#   dist\CaseKeeper-src-v<ver>.zip
#
# What goes in: every file git tracks, minus release output, local tool caches and anything .gitignore'd. Before
# zipping, every text file is scanned for things that must never be published: local user paths, private e-mail
# addresses, keys and tokens, and the terms listed in tools\privacy-terms.local.txt (one per line; that file is ignored
# by git and never leaves this PC). A hit fails with the file and line, and no archive is written.
#
# Usage: .\tools\write-public-source.ps1 -Version 0.4.1
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$git = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $git)) { $git = 'git' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. The file list: tracked files (committed or staged). The repository is the source of truth for what is public.
$files = & $git ls-files --cached --others --exclude-standard | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) }
if ($LASTEXITCODE) { throw 'git ls-files failed' }
$exclude = '^dist/', '^publish/', '^_archive/', '^\.claude/', '_wpftmp\.csproj$', '\.local\.txt$', '/(bin|obj)/'
$files = @($files | Where-Object { $f = $_; -not ($exclude | Where-Object { $f -match $_ }) })
if ($files.Count -lt 100) { throw "only $($files.Count) files listed - is this the repository root?" }

# 2. The privacy scan. Generic rules first; the local term list adds names that must not appear (kept out of the repo).
# Test fixtures under tests\ use synthetic keys and paths, so the key and path rules skip them; the term list still
# applies there. E-mail: documentation domains, the brand domain and GitHub's no-reply addresses are allowed.
$rules = @(
    @{ Name = 'a local user path'; Rx = '(?i)[A-Z]:\\Users\\(?!Public\\|demo\\|<)[^\\\s"''`]+'; SkipTests = $true },
    @{ Name = 'a private e-mail address'; Rx = '(?i)\b[A-Z0-9._%+-]+@(?!example\.|[a-z0-9.-]*\.(test|example|invalid|local|lan)\b|darkciphersystems\.com|anthropic\.com|users\.noreply\.github\.com)[A-Z0-9.-]+\.[A-Z]{2,}\b'; SkipTests = $false },
    @{ Name = 'an API key or token'; Rx = '\b(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b'; SkipTests = $true },
    @{ Name = 'a private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----'; SkipTests = $true }
)
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
$terms = @()
if (Test-Path $termsFile) { $terms = Get-Content $termsFile -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() } }
else { Write-Warning "tools\privacy-terms.local.txt not found: only the generic privacy rules ran" }
# A term matches as a whole word (so a name doesn't match an ordinary word that contains it); a term with its own
# punctuation (a domain, a path) matches anywhere.
$termRx = $terms | ForEach-Object { if ($_ -match '^\w+$') { '(?i)\b' + [regex]::Escape($_) + '\b' } else { '(?i)' + [regex]::Escape($_) } }
$textExt = '.cs', '.xaml', '.ps1', '.md', '.json', '.html', '.js', '.css', '.txt', '.props', '.csproj', '.sln', '.xml', '.yml', '.yaml', '.config', '.gitignore', '.gitattributes', '.editorconfig', '.cmd', '.bat', '.sh', '.resx', '.iss'
$hits = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $ext = [IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($ext -notin $textExt -and $ext -ne '') { continue }
    $isTest = $f -match '^tests/'
    $lines = [IO.File]::ReadAllLines((Join-Path $root $f))
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        foreach ($r in $rules) { if (-not ($isTest -and $r.SkipTests) -and $l -match $r.Rx) { $hits.Add("$f($($i + 1)): $($r.Name): $($Matches[0])") } }
        foreach ($t in $termRx) { if ($l -match $t) { $hits.Add("$f($($i + 1)): a private term") } }
    }
}
if ($hits.Count -gt 0) {
    $hits | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "privacy scan: $($hits.Count) line(s) must not be published (above). Fix the source; the public archive was not written."
}
# Key material and signing files have no place in the source: refuse them.
$binaryBad = $files | Where-Object { $_ -match '\.(pfx|p12|pem|key|snk|pvk|cer|kdbx|ppk|msix|appx)$' }
if ($binaryBad) { throw "key material or packages in the tree: $($binaryBad -join ', ')" }

# 3. The archive, with the folder name as its top level.
$name = "CaseKeeper-src-v$Version"
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('ck-source-' + [guid]::NewGuid().ToString('N'))
$top = Join-Path $stage $name
foreach ($f in $files) {
    $dest = Join-Path $top $f
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $root $f) $dest
}
[IO.File]::WriteAllText((Join-Path $top 'SOURCE-ARCHIVE.txt'), @"
DarkCipher Systems
CaseKeeper $Version - source archive

This is the source tree the $Version release was built from, as published. Build it with
build-release.ps1 (RELEASING.md). Licence: MIT (LICENSE). Third-party components: THIRD-PARTY-NOTICES.md.
Scanned before publication for local paths, private addresses and keys (tools\write-public-source.ps1).
"@, (New-Object Text.UTF8Encoding $false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force
Write-Host ("Source: {0} ({1} files, {2:N1} MB), privacy scan clean ({3} rule(s), {4} private term(s))" -f $zip, $files.Count, ((Get-Item $zip).Length / 1MB), $rules.Count, $terms.Count)
