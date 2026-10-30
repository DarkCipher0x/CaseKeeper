# Creates the GitHub release for a version that tools\publish-public.ps1 has already tagged, and attaches the
# downloadable files. build-release.ps1 runs it straight after publishing the source.
#
#   Release   "CaseKeeper v<ver>" on tag v<ver>, marked as the latest release
#   Notes     that version's section of CHANGELOG.md, the Store link and the SHA-256s
#   Files     CaseKeeper-Setup-v<ver>.exe, CaseKeeper-src-v<ver>.zip and SHA256SUMS.txt (for those two),
#             all from dist\<ver>\ - the same files, byte for byte, as the website gets
#
# The .msix is not attached: it is signed only with the local test certificate, so it installs nowhere
# else. It goes to Partner Center, and Microsoft signs it for the Store.
#
# Sign-in: the GitHub sign-in Git Credential Manager already holds for pushing (no token is stored or printed
# here). A release that already exists is never changed, so published checksums never move.
#
# Usage: .\tools\publish-github-release.ps1 -Version 0.4.1 [-Draft]
param([Parameter(Mandatory)][string]$Version, [switch]$Draft)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
$repoApi = 'https://api.github.com/repos/DarkCipher0x/CaseKeeper'
$out = Join-Path $root "dist\$Version"
$tag = "v$Version"
$files = "CaseKeeper-Setup-v$Version.exe", "CaseKeeper-src-v$Version.zip" | ForEach-Object { Join-Path $out $_ }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "missing $f - run build-release.ps1 first" } }

# --- the sign-in Git Credential Manager holds for github.com ----------------------
function Get-GitHubToken {
    $git = 'C:\Program Files\Git\cmd\git.exe'
    if (-not (Test-Path $git)) { $git = 'git' }
    # The request goes in through a file redirect: Windows PowerShell's pipes and .NET's redirected stdin both
    # start with a byte-order mark, which git rejects ("missing protocol field"). ASCII, LF line ends.
    $req = Join-Path ([IO.Path]::GetTempPath()) ('ck-cred-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllBytes($req, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    $before = $env:GCM_INTERACTIVE; $env:GCM_INTERACTIVE = 'auto'   # opens the sign-in window only if nothing is cached
    try { $answer = cmd /c "`"$git`" credential fill < `"$req`"" }
    finally { Remove-Item $req -ErrorAction SilentlyContinue; $env:GCM_INTERACTIVE = $before }
    $line = $answer | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw 'No GitHub sign-in available. Run "git -C ..\CaseKeeper-public pull" once to sign in, then try again.' }
    $line.Substring(9).Trim()
}
$h = @{ Authorization = "Bearer $(Get-GitHubToken)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'CaseKeeper-release' }

# --- never touch a release that already exists -----------------------------------
$existing = $null
try { $existing = Invoke-RestMethod "$repoApi/releases/tags/$tag" -Headers $h } catch { if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw } }
if ($existing) { Write-Host "GitHub release $tag already exists, left unchanged: $($existing.html_url)" -ForegroundColor Yellow; return }
try { Invoke-RestMethod "$repoApi/git/ref/tags/$tag" -Headers $h | Out-Null }
catch { throw "tag $tag is not on GitHub yet - run tools\publish-public.ps1 -Version $Version first" }

# --- notes: the changelog section and the checksums ------------------------------
$sums = $files | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower(), (Split-Path $_ -Leaf) }
$sumsFile = Join-Path ([IO.Path]::GetTempPath()) ("ck-sums-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $sumsFile | Out-Null
$sumsFile = Join-Path $sumsFile 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsFile, ($sums -join "`n") + "`n")

$log = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
$m = [regex]::Match($log, "(?ms)^## v$([regex]::Escape($Version))\b[^\n]*\n(.*?)(?=^---|^## v|\z)")
if (-not $m.Success) { throw "CHANGELOG.md has no section for v$Version" }
$fence = '```'
$body = @"
**Download** ``CaseKeeper-Setup-v$Version.exe`` below, or get CaseKeeper from the [Microsoft Store](https://apps.microsoft.com/detail/9PG5PJPHCXXF), where Microsoft signs it. Windows SmartScreen may warn about the installer because it isn't code-signed yet: check the SHA-256 below, then choose **More info → Run anyway**.

Upgrading over any earlier version keeps your library exactly as it is.

$($m.Groups[1].Value.Trim())

### SHA-256

$fence
$($sums -join "`n")
$fence

These are the same files, with the same checksums, as on [darkciphersystems.com](https://darkciphersystems.com/software.html).
"@

# --- create the release and upload ------------------------------------------------
$json = @{ tag_name = $tag; name = "CaseKeeper $tag"; body = $body; draft = [bool]$Draft; prerelease = $false; make_latest = 'true' } | ConvertTo-Json
$r = Invoke-RestMethod "$repoApi/releases" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
$up = $r.upload_url -replace '\{.*\}$', ''
try {
    foreach ($f in @($files) + $sumsFile) {
        $n = Split-Path $f -Leaf
        $ct = if ($n -like '*.txt') { 'text/plain' } elseif ($n -like '*.zip') { 'application/zip' } else { 'application/octet-stream' }
        $a = Invoke-RestMethod "$up`?name=$n" -Method Post -Headers $h -ContentType $ct -InFile $f -TimeoutSec 1800
        # GitHub reports its own SHA-256 of what it stored: it must equal ours
        $local = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
        if ($a.digest -and $a.digest -ne "sha256:$local") { throw "GitHub stored $n with a different SHA-256 ($($a.digest))" }
        Write-Host ("  uploaded {0} ({1:N1} MB), SHA-256 confirmed" -f $n, ($a.size / 1MB))
    }
}
catch {
    Write-Host "Upload failed; the release was left as created: $($r.html_url). Delete it on GitHub and run this script again." -ForegroundColor Red
    throw
}
finally { Remove-Item (Split-Path $sumsFile) -Recurse -Force -ErrorAction SilentlyContinue }
$state = if ($Draft) { 'draft' } else { 'published' }
Write-Host "GitHub release $tag $state`: $($r.html_url)" -ForegroundColor Green
