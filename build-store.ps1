<#
    Builds CaseKeeper's Microsoft Store package (.msix).

      powershell -ExecutionPolicy Bypass -File build-store.ps1

    The result is dist\CaseKeeper-v<version>-x64.msix, ready to upload in Partner Center.
    docs\STORE.md explains the whole process, from opening the developer account to submission.

    Identity: the package name and publisher must match what Partner Center reserved for the app.
    Put them in packaging\store\store-identity.json (copy the .example file beside it). Without that
    file the script builds a TEST package (file name ends in -TEST) that you can install on this PC
    to try out, but that the Store would reject.

    Signing: the Store re-signs every package with Microsoft's own certificate, so a Store upload
    needs no certificate you have to buy. The script still signs the package, with a self-signed
    certificate whose name matches the publisher, because Windows will only install a signed
    package and that is what lets you test it locally. To sign for distribution outside the Store
    instead, pass -CertThumbprint with a real code-signing certificate from your certificate store.

    Tools: MakeAppx, MakePri and SignTool come from the Windows SDK. If the SDK isn't installed,
    pass -FetchTools to download Microsoft's Microsoft.Windows.SDK.BuildTools package from nuget.org
    into obj\ (nothing is installed on the PC).
#>
[CmdletBinding()]
param(
    # Repackage what is already in publish\ instead of running dotnet publish again
    [switch]$SkipBuild,
    # Download the SDK packaging tools from nuget.org if the Windows SDK isn't installed
    [switch]$FetchTools,
    # Sign with this certificate (from Cert:\CurrentUser\My) instead of a self-signed test one
    [string]$CertThumbprint
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'CaseKeeper\CaseKeeper.csproj'
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'
$storeDir = Join-Path $root 'packaging\store'
$work = Join-Path $root 'obj\store'
$layout = Join-Path $work 'layout'

# --- version: the Store wants four parts, and the last one must be 0 --------------------------
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> found in $project" }
$parts = @($version.Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 3) { $parts += 0 }
$packageVersion = '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]
Write-Host "Building the CaseKeeper v$version Store package ($packageVersion)" -ForegroundColor Cyan

# --- identity ----------------------------------------------------------------------------------
$identityFile = Join-Path $storeDir 'store-identity.json'
$isTest = -not (Test-Path $identityFile)
if ($isTest) {
    Write-Warning ("No packaging\store\store-identity.json, so this is a TEST package. It installs on this PC " +
                   "for trying out; the Store would reject it. See docs\STORE.md, step 2.")
    $identity = [pscustomobject]@{
        IdentityName         = 'DarkCipherSystems.CaseKeeper.Test'
        Publisher            = 'CN=DarkCipher Systems Test'
        PublisherDisplayName = 'DarkCipher Systems'
    }
} else {
    $identity = Get-Content $identityFile -Raw | ConvertFrom-Json
    foreach ($field in 'IdentityName', 'Publisher', 'PublisherDisplayName') {
        if (-not $identity.$field) { throw "$identityFile has no $field" }
    }
    if ($identity.Publisher -eq 'CN=00000000-0000-0000-0000-000000000000') {
        throw "$identityFile still holds the example values. Copy the real ones from Partner Center."
    }
}

# --- tools -------------------------------------------------------------------------------------
function Find-SdkTool([string]$name) {
    $candidates = @()
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $candidates += Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' }
    }
    foreach ($nuget in (Join-Path $work 'tools'), (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools')) {
        if (Test-Path $nuget) {
            $candidates += Get-ChildItem $nuget -Recurse -Filter $name -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'x64' }
        }
    }
    # Newest SDK first (folder names look like 10.0.26100.0)
    $candidates | Sort-Object {
        $v = $null
        foreach ($dir in $_.Directory.Parent, $_.Directory.Parent.Parent) {
            if ($dir -and [version]::TryParse($dir.Name, [ref]$v)) { break }
        }
        if ($v) { $v } else { [version]'0.0' }
    } -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Get-BuildTools {
    # Microsoft's own NuGet package of the SDK's packaging tools, unpacked into obj\store\tools
    $index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json'
    $latest = $index.versions | Where-Object { $_ -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object { [version]$_ } -Descending | Select-Object -First 1
    if (-not $latest) { throw 'Could not find a release of Microsoft.Windows.SDK.BuildTools on nuget.org' }
    $tools = Join-Path $work 'tools'
    $nupkg = Join-Path $work "buildtools.$latest.zip"
    Write-Host "Downloading Microsoft.Windows.SDK.BuildTools $latest from nuget.org..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $work | Out-Null
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$latest/microsoft.windows.sdk.buildtools.$latest.nupkg" -OutFile $nupkg -UseBasicParsing
    if (Test-Path $tools) { Remove-Item $tools -Recurse -Force }
    Expand-Archive $nupkg -DestinationPath $tools
    Remove-Item $nupkg
}

$makeAppx = Find-SdkTool 'makeappx.exe'
if (-not $makeAppx -and $FetchTools) { Get-BuildTools; $makeAppx = Find-SdkTool 'makeappx.exe' }
if (-not $makeAppx) {
    throw ("MakeAppx.exe not found. Either install the Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100) " +
           "or run this script again with -FetchTools.")
}
$makePri = Find-SdkTool 'makepri.exe'
$signTool = Find-SdkTool 'signtool.exe'
if (-not $makePri -or -not $signTool) { throw "MakePri.exe or SignTool.exe is missing next to $makeAppx" }
Write-Host "Using the packaging tools in $(Split-Path $makeAppx)" -ForegroundColor DarkGray

# --- publish -----------------------------------------------------------------------------------
if (-not $SkipBuild) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host 'Publishing (self-contained)...' -ForegroundColor Cyan
    & dotnet publish $project -c Release -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
$exe = Join-Path $publishDir 'CaseKeeper.exe'
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }

# --- package layout ----------------------------------------------------------------------------
if (Test-Path $work) { Get-ChildItem $work -Exclude 'tools' | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Write-Host 'Laying out the package...' -ForegroundColor Cyan

# The same files the ordinary installer ships: the program, the patch notes and the user guide
robocopy $publishDir $layout /E /XF *.pdb *.xml *.log /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the published files failed (robocopy exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0
Copy-Item (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'USER-GUIDE.md') $layout

# Tile and taskbar images, drawn from the app icon at every size Windows asks for
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\casekeeper-icon.png'))
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Save-Image([string]$name, [int]$width, [int]$height, [double]$fill = 1.0) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [int][math]::Round([math]::Min($width, $height) * $fill)
    $g.DrawImage($icon, [int](($width - $side) / 2), [int](($height - $side) / 2), $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
try {
    foreach ($scale in 100, 200, 400) {
        $f = $scale / 100
        Save-Image "StoreLogo.scale-$scale.png" (50 * $f) (50 * $f)
        Save-Image "Square44x44Logo.scale-$scale.png" (44 * $f) (44 * $f)
        Save-Image "Square150x150Logo.scale-$scale.png" (150 * $f) (150 * $f) 0.66
        Save-Image "Wide310x150Logo.scale-$scale.png" (310 * $f) (150 * $f) 0.66
    }
    # Taskbar, Start list and File Explorer sizes; "unplated" = drawn without a coloured backing
    foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        Save-Image "Square44x44Logo.targetsize-$size.png" $size $size
        Save-Image "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size
        Save-Image "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size
    }
} finally { $icon.Dispose() }

# The manifest, with this build's identity and version filled in
$manifest = (Get-Content (Join-Path $storeDir 'AppxManifest.xml') -Raw).
    Replace('$IDENTITY_NAME$', [Security.SecurityElement]::Escape($identity.IdentityName)).
    Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($identity.Publisher)).
    Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
    Replace('$DISPLAY_NAME$', 'CaseKeeper').
    Replace('$VERSION$', $packageVersion)
if ($manifest -match '\$[A-Z_]+\$') { throw "Unfilled token $($Matches[0]) in the manifest" }
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

# resources.pri lets Windows pick the right image size for each screen
$priConfig = Join-Path $work 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq en-GB /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MakePri createconfig failed' }
# The default config splits the index by screen scale, which only works in a bundle. This is a
# single package, so keep every size in the one resources.pri or the sharper images are ignored.
[xml]$cfg = Get-Content $priConfig
foreach ($node in @($cfg.SelectNodes('//packaging'))) { [void]$node.ParentNode.RemoveChild($node) }
$cfg.Save($priConfig)
& $makePri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MakePri failed' }

# --- privacy check: nothing about this PC or its user may end up in the package ----------------
Write-Host 'Checking the package holds nothing personal...' -ForegroundColor Cyan
$needles = @(
    $env:USERPROFILE                        # this user's profile folder
    "\Users\$env:USERNAME\"
    "/Users/$env:USERNAME/"
    $env:COMPUTERNAME
) | Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique
if (Get-Command git -ErrorAction SilentlyContinue) {
    $email = (& git config --global user.email 2>$null)
    if ($email) { $needles += $email }
    $global:LASTEXITCODE = 0
}
# Our own files: everything that isn't the .NET runtime or a third-party library
$ours = Get-ChildItem $layout -Recurse -File | Where-Object {
    $_.Name -like 'CaseKeeper.*' -or $_.Extension -in '.md', '.json', '.xml', '.pri', '.config', '.txt'
}
$leaks = foreach ($file in $ours) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $text = [Text.Encoding]::UTF8.GetString($bytes) + "`n" + [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($n in $needles) {
        if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { "$($file.Name) contains a personal detail" }
    }
}
if ($leaks) { $leaks | Select-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; throw 'Privacy check failed: the package would identify this PC or its user.' }

# --- pack --------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $distDir | Out-Null
$suffix = if ($isTest) { '-TEST' } else { '' }
$msix = Join-Path $distDir "CaseKeeper-v$version-x64$suffix.msix"
Write-Host 'Packing...' -ForegroundColor Cyan
& $makeAppx pack /d $layout /p $msix /o /h SHA256 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed' }

# --- sign --------------------------------------------------------------------------------------
if ($CertThumbprint) {
    $cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "No certificate $CertThumbprint in Cert:\CurrentUser\My" }
    if ($cert.Subject -ne $identity.Publisher) {
        throw "The certificate's subject ($($cert.Subject)) must equal the package publisher ($($identity.Publisher))"
    }
    Write-Host "Signing with $($cert.Subject)..." -ForegroundColor Cyan
    & $signTool sign /q /fd SHA256 /sha1 $CertThumbprint /tr 'http://timestamp.digicert.com' /td SHA256 $msix
} else {
    # Self-signed, named after the publisher. Kept in your personal certificate store for next time.
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $identity.Publisher -and $_.NotAfter -gt (Get-Date).AddDays(7) -and $_.HasPrivateKey -and
        ($_.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3')
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating a self-signed test certificate for $($identity.Publisher)..." -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type Custom -Subject $identity.Publisher -KeyUsage DigitalSignature `
            -FriendlyName 'CaseKeeper package test signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    & $signTool sign /q /fd SHA256 /sha1 $cert.Thumbprint $msix
    # The public half, so this PC (or a tester's) can be told to trust the test package
    $cer = Join-Path $distDir "CaseKeeper-package-test$suffix.cer"
    Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed' }

$sizeMb = [math]::Round((Get-Item $msix).Length / 1MB, 1)
$sha = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLower()
Write-Host ''
Write-Host "Done: $msix" -ForegroundColor Green
Write-Host "  version   $packageVersion"
Write-Host "  identity  $($identity.IdentityName)"
Write-Host "  publisher $($identity.Publisher)"
Write-Host "  size      $sizeMb MB"
Write-Host "  SHA-256   $sha"
Write-Host ''
if ($isTest) {
    Write-Host 'TEST package: install it on this PC to try it out (docs\STORE.md, "Trying the package").'
} else {
    Write-Host 'Upload that .msix in Partner Center: CaseKeeper > Submission > Packages.'
}
