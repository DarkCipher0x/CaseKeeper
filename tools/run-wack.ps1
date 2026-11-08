<#
    Runs the Windows App Certification Kit (the automated tests Microsoft runs on every Store submission)
    on a CaseKeeper Store package, and prints a plain pass/fail summary.

        powershell -ExecutionPolicy Bypass -File tools\run-wack.ps1                 newest dist\<version>\*.msix
        powershell -ExecutionPolicy Bypass -File tools\run-wack.ps1 -Package <msix>

    It must run as administrator (the kit installs, launches and removes the package). It:
      1. trusts dist\CaseKeeper-package-test.cer for the run (the package's test signature),
      2. runs the kit's Store tests and writes the full report to dist\<version>\wack-report.xml,
      3. removes the test trust again, and uninstalls anything the kit left installed,
      4. removes the empty library the kit's launch test creates in %USERPROFILE%\CaseKeeper, but only if
         that folder didn't exist before the run (a real Store library is never touched).
    Needs the Windows SDK's App Certification Kit (winget install Microsoft.WindowsSDK.10.0.26100).
#>
param([string]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this from an administrator PowerShell (right-click Terminal -> Run as administrator).' }

$kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
if (-not (Test-Path $kit)) { throw "The App Certification Kit isn't installed ($kit). Install the Windows SDK: winget install Microsoft.WindowsSDK.10.0.26100" }

if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $root 'dist') -Recurse -Filter 'CaseKeeper-v*-x64.msix' |
        Where-Object { $_.Name -notmatch 'TEST' } | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Package) { throw 'No Store package found in dist\. Run build-release.ps1 first.' }
}
$Package = (Resolve-Path $Package).Path
$report = Join-Path (Split-Path -Parent $Package) 'wack-report.xml'
$cerPath = Join-Path $root 'dist\CaseKeeper-package-test.cer'
$library = Join-Path $env:USERPROFILE 'CaseKeeper'
$libraryExisted = Test-Path $library
Write-Host "Package: $Package"

$added = $null
try {
    if (Test-Path $cerPath) {
        $cer = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cerPath
        $already = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cer.Thumbprint
        if (-not $already) { Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null; $added = $cer.Thumbprint }
    } else { Write-Warning "No ${cerPath}: the kit may not be able to install the package." }

    if (Test-Path $report) { Remove-Item $report -Force }
    & $kit reset | Out-Null
    Write-Host 'Running the certification tests (about 5-15 minutes; the app opens and closes by itself)...'
    & $kit test -appxpackagepath $Package -reportoutputpath $report
    if (-not (Test-Path $report)) { throw "The kit didn't write a report (exit code $LASTEXITCODE)." }
} finally {
    if ($added) { Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $added | Remove-Item }
    # Only the test-signed copy the kit installed; a copy installed from the Store (SignatureKind Store) is left alone.
    Get-AppxPackage -AllUsers 'DarkCipherSystems.CaseKeeper*' -ErrorAction SilentlyContinue |
        Where-Object { $_.SignatureKind -ne 'Store' } | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
    # The launch test's empty library only: never a folder that was there before, never one holding documents.
    if (-not $libraryExisted -and (Test-Path $library)) {
        $docs = Get-ChildItem (Join-Path $library 'Files') -Recurse -File -ErrorAction SilentlyContinue
        if ($docs) { Write-Warning "$library now holds documents, so it was left in place." }
        else { Remove-Item -LiteralPath $library -Recurse -Force }
    }
}

[xml]$x = Get-Content -Raw $report
$overall = $x.REPORT.OVERALL_RESULT
$failed = @()
foreach ($t in $x.SelectNodes('//TEST')) {
    $node = $t.SelectSingleNode('RESULT')
    $result = if ($node) { $node.InnerText.Trim() } else { '' }
    if ($result -notmatch '^(PASS|NOT ?RUN|NA)$') {
        $msgs = foreach ($m in $t.SelectNodes('.//MESSAGE')) { if ($m.GetAttribute('TEXT')) { $m.GetAttribute('TEXT') } else { $m.InnerText.Trim() } }
        $failed += [pscustomobject]@{ Requirement = $t.ParentNode.GetAttribute('TITLE'); Test = $t.GetAttribute('NAME'); Result = $result; Detail = ($msgs -join ' | ') }
    }
}
Write-Host ''
Write-Host "Overall result: $overall"
if ($failed) { $failed | Format-List } else { Write-Host 'Every test passed.' }
Write-Host "Full report: $report"
if ($overall -ne 'PASS') { exit 1 }
