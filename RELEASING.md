# Releasing CaseKeeper

How to cut a new version and publish it on GitHub so people can download and install it.

## One-off setup

You need three things on the build machine:

| Tool | Why | Install |
|---|---|---|
| .NET 8 SDK | builds the app | `winget install Microsoft.DotNet.SDK.8` |
| Inno Setup 6 | builds the installer | `winget install JRSoftware.InnoSetup` |
| Git | pushes to GitHub | `winget install Git.Git` |

Optionally `winget install GitHub.cli` for the `gh` command, which makes releases a one-liner.

## Cutting a release

1. **Bump the version** in `CaseKeeper/CaseKeeper.csproj`. There are four lines to change and
   they must agree:

   ```xml
   <Version>0.2.0</Version>
   <AssemblyVersion>0.2.0.0</AssemblyVersion>
   <FileVersion>0.2.0.0</FileVersion>
   <InformationalVersion>0.2.0</InformationalVersion>
   ```

   Use `0.1.1` for a fix, `0.2.0` for new features, `1.0.0` when you consider it finished.

2. **Write the patch notes.** Add a section at the top of `CHANGELOG.md`:

   ```markdown
   ## v0.2.0 — 3 November 2026

   ### Added
   - ...

   ### Fixed
   - ...
   ```

   The build warns you if the changelog has no entry for the version you're building.

3. **Build it:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File build-release.ps1
   ```

   This runs `dotnet publish` (the app ships with its own copy of .NET), builds the installer,
   writes the privacy-scanned source zip and `SHA256SUMS.txt` in `dist\`, and finally publishes
   the sanitised source to GitHub as tag `v0.2.0` (see *The GitHub repository* below). Use
   `-NoPublish` to hold that last step back until you've tested the upgrade.

4. **Test the upgrade** before publishing: install the new exe on a machine that already has the
   previous version and check your documents are still there. Upgrades keep the same install
   folder and leave `%LOCALAPPDATA%\CaseKeeper` untouched.

5. **Commit** in this private repository (it never goes online):

   ```powershell
   git add -A
   git commit -m "Release v0.2.0"
   ```

6. **Publish the release** on github.com: *Releases* → *Draft a new release* → choose the
   `v0.2.0` tag → paste that version's changelog section and the contents of `SHA256SUMS.txt` into
   the description → attach `CaseKeeper-Setup-v0.2.0.exe`, `CaseKeeper-src-v0.2.0.zip` and
   `SHA256SUMS.txt` → *Publish release*. Put the same files and checksums on the website, so the
   hashes match everywhere.

## The GitHub repository

There are two git repositories, and only one of them ever goes online:

| Folder | What it is |
|---|---|
| `CaseKeeper\` (this one) | The private working repository. It has **no GitHub remote**: its history never leaves this PC. |
| `..\CaseKeeper-public\` | A clone of `github.com/DarkCipher0x/CaseKeeper`. It only ever receives the sanitised source of a release. |

`build-release.ps1` ends by publishing the release to the public repository:

1. `tools\write-public-source.ps1` copies every tracked file (minus `dist\`, `publish\`, `_archive\`
   and local files) and scans each text file for local user paths, private e-mail addresses, keys,
   tokens and the private terms in `tools\privacy-terms.local.txt`. That file is git-ignored and never
   leaves this PC. Any hit stops the build, and the result is `dist\CaseKeeper-src-v<ver>.zip`.
2. `tools\publish-public.ps1` unpacks that zip into `..\CaseKeeper-public` and checks it:
   - every image must be listed by SHA-256 in `tools\approved-images.txt`, meaning someone has
     looked at it, because text scans can't see pixels;
   - certificates, keys, debug symbols and packages are refused.

   It then makes one commit, `CaseKeeper <ver> - sanitised public source`, and a tag `v<ver>`,
   authored as `DarkCipher0x <DarkCipher0x@users.noreply.github.com>`, and pushes both.

Use `-NoPublish` to build without publishing, then run `tools\publish-public.ps1 -Version <ver>` later.
Add a new image to `tools\approved-images.txt` only after viewing it:

```powershell
(Get-FileHash assets\new-image.png -Algorithm SHA256).Hash.ToLower()
```

The public tree is replaced wholesale on every release, so edit the README and `.github\` here,
never on GitHub.

First-time setup of the public clone, which signs in to GitHub once in the browser:

```powershell
git clone https://github.com/DarkCipher0x/CaseKeeper.git ..\CaseKeeper-public
```

## The SmartScreen warning

The installer is not code-signed, so the first people to download it will see
*"Windows protected your PC"* and have to click **More info → Run anyway**. This is normal for
independent software and is not a sign anything is wrong, but it does put people off.

Options:

- **Do nothing.** Explain the warning in your README and release notes. The warning fades as more
  people download and run the same file.
- **Buy a code-signing certificate.** An OV certificate is roughly £200–400 a year and still
  needs reputation to build. Azure Artifact Signing is about $9.99 a month, if you're eligible
  (see [docs/STORE.md](docs/STORE.md#signing-the-website-installer)). Once you have one, add a
  `SignTool` line to `installer\CaseKeeper.iss`.
- **Publish in the Microsoft Store** as well. It's free, Microsoft signs the package, and there's
  no warning. See [docs/STORE.md](docs/STORE.md).

Publishing the SHA-256 checksum alongside each release lets careful users verify the file
independently, which is worth doing either way.

## The Microsoft Store

CaseKeeper can also be published in the Microsoft Store. There, Microsoft signs it for free and
there is no SmartScreen warning. `build-store.ps1` builds the `.msix` package next to the installer.
**[docs/STORE.md](docs/STORE.md)** covers the developer account, the identity file, testing the
package, the submission form (with ready-written answers) and later updates.

## GitHub Actions

`.github/workflows/ci.yml` builds and runs the tests on every push to `main` and every pull
request. On a private repository, Windows runners use the free Actions minutes at twice the normal
rate.

`.github/workflows/release.yml` is a cloud build of the full package (installer, source zip and
`SHA256SUMS.txt`) that only runs when started by hand from the *Actions* tab. It isn't triggered by
tags, because the release of record is the local build: a second, cloud-built installer would have
different checksums. It creates a draft release only when none exists for the tag.