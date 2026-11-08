# Releasing CaseKeeper

How to cut a new version and publish it on GitHub so people can download and install it.

## One-off setup

You need three things on the build machine:

| Tool | Why | Install |
|---|---|---|
| .NET 10 SDK | builds the app | `winget install Microsoft.DotNet.SDK.10` |
| Inno Setup 6 | builds the installer | `winget install JRSoftware.InnoSetup` |
| Git | pushes to GitHub | `winget install Git.Git` |

No GitHub CLI or token is needed. The scripts use the GitHub sign-in Git Credential Manager already holds,
and the first push opens a sign-in window once.

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

   The heading must be exactly `## v<version>`. The build refuses to run without it, because that
   section becomes the GitHub release notes.

3. **Run one command**, which does everything else:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build-release.ps1
   ```

   It stops at the first problem, in this order:

   | Step | What happens |
   |---|---|
   | Checks | The version must be new: a built or published version is never overwritten. The changelog section must exist. |
   | Tests | The four test programs run; any failure stops the release. |
   | Installer | `dotnet publish` (self-contained .NET), then Inno Setup. |
   | Store package | `build-store.ps1` builds the `.msix` from the same build. |
   | Source | `tools\write-public-source.ps1` privacy-scans the source, `tools\publish-public.ps1 -NoPush` commits and tags it in `..\CaseKeeper-public` (not pushed yet), and the source zip is `git archive` of that tag. |
   | Update information | `update.json` (version, release notes from the changelog, the setup program's size and SHA-256) is signed with both release keys by `tools\sign-update.ps1`, then checked the way installed copies will check it, with the app's own code (`tests\CaseKeeper.Tests.Updates --verify`). See [Signed updates](#signed-updates). |
   | Local copy | Everything is kept in **`dist\<version>\`**: the installer, source zip, `.msix`, `update.json` with `update.json.sig` and `update.json.pq.sig`, `CHANGELOG.md`, `USER-GUIDE.md`, `STORE.md` and `SHA256SUMS.txt`. |
   | Private repo | This repository is committed as `Release v<version>` and tagged locally. |
   | GitHub source | `tools\publish-public.ps1` pushes that commit and tag `v<version>`. |
   | GitHub release | `tools\publish-github-release.ps1` creates *CaseKeeper v&lt;version&gt;* with the changelog notes, attaches the installer, source zip, `update.json` and its two signatures and `SHA256SUMS.txt`, and checks GitHub's own SHA-256 of each upload. |

   Useful switches:

   | Switch | Effect |
   |---|---|
   | `-NoPublish` | Stop before GitHub, so nothing leaves this PC. |
   | `-PublishOnly` | Publish a version that's already built in `dist\<version>\`. |
   | `-Draft` | Create the GitHub release as a draft. |
   | `-NoStore` | Skip the `.msix`. |

   To test the upgrade before anything goes online, run with `-NoPublish`, install
   `dist\<version>\CaseKeeper-Setup-v<version>.exe` over the previous version, check your documents
   are still there, then run `build-release.ps1 -PublishOnly`.

4. **By hand afterwards:** put the installer, source zip and SHA-256s on the website, with the
   installer, `update.json`, `update.json.sig` and `update.json.pq.sig` together in
   `/downloads/casekeeper/` (where installed copies look). For the Store, first run
   `tools\run-wack.ps1` from an administrator PowerShell (Microsoft's certification tests; submit
   only on **PASS**), then upload the `.msix` in Partner Center as a new Store submission, with
   Store-specific *What's new* text (see [docs/STORE.md](docs/STORE.md)). The script prints this
   reminder at the end.

## Signed updates

From 0.5.0 the website version updates itself (*Settings → Updates*), the same way as DarkCipher
ServerManagement. Installed copies trust only the two public keys pinned in
`CaseKeeper\Services\Updater.cs` (`ReleaseKeyX` / `ReleaseKeyY` for ECDSA P-256, `ReleasePqKey` for
ML-DSA-65). Both were created once, on 8 November 2026.

- **The private keys** are two files in `%LOCALAPPDATA%\DarkCipher\keys\`, encrypted by Windows (DPAPI)
  for the release account. They are never in a repository, a build or a backup of the library.
  `build-release.ps1` signs with them through `tools\sign-update.ps1`, which refuses to sign if they
  aren't the pinned keys.
- **Never replace them.** Every installed copy trusts only these keys; new keys would mean those copies
  can only be updated by hand.
- **Escrow them once**, and again only if they're restored somewhere new:

  ```powershell
  powershell -ExecutionPolicy Bypass -File tools\sign-update.ps1 -Escrow E:\casekeeper-update-keys.json
  ```

  It asks for a password of at least 16 characters and writes both keys encrypted with it
  (PBKDF2-HMAC-SHA256, 600,000 iterations, AES-256 with an HMAC-SHA256 check). Keep the file offline,
  on removable media, and the password somewhere else. Not in cloud storage, and never in a repository.
- **On a new PC or Windows account**, put them back with
  `tools\sign-update.ps1 -RestoreEscrow <file>`. It refuses unless the keys match the ones pinned in
  `Updater.cs`.
- **Run key commands from an ordinary PowerShell window** (Start menu → Windows PowerShell). Inside a
  packaged app, such as a terminal started by a Store-installed program, Windows silently moves new files
  under `%LOCALAPPDATA%` into that app's private folder and deletes them when the app is uninstalled.
  `-NewKeys` and `-RestoreEscrow` refuse to run there.
- `tools\sign-update.ps1 -ShowPublicKey` prints the public keys; `-Verify <update.json>` checks a signed
  file without any private key.
- The CI build has no keys, so it writes no `update.json`. Only the local release of record is signed.

`update.json` looks like this (the signatures cover its exact bytes, so never edit it after signing):

```json
{
  "product": "DarkCipher CaseKeeper",
  "version": "0.5.0",
  "label": "CaseKeeper 0.5.0",
  "released": "2026-11-08",
  "notes": "…the changelog section, as plain text…",
  "schemaChange": false,
  "signed": true,
  "packages": [ { "kind": "setup", "arch": "x64", "file": "CaseKeeper-Setup-v0.5.0.exe",
                  "url": "CaseKeeper-Setup-v0.5.0.exe", "bytes": 0, "sha256": "…" } ]
}
```

Set `schemaChange` to `true` by hand in `build-release.ps1` for a release that changes how the library is
stored: the Updates card then recommends a backup more strongly.

## The GitHub repository

There are two git repositories, and only one of them ever goes online:

| Folder | What it is |
|---|---|
| `CaseKeeper\` (this one) | The private working repository. It has **no GitHub remote**: its history never leaves this PC. |
| `..\CaseKeeper-public\` | A clone of `github.com/DarkCipher0x/CaseKeeper`. It only ever receives the sanitised source of a release. |

`build-release.ps1` prepares the public repository during the build and publishes it at the end:

1. `tools\write-public-source.ps1` copies every tracked file (minus `dist\`, `publish\`, `_archive\`
   and local files) and scans each text file for local user paths, private e-mail addresses, keys,
   tokens and the private terms in `tools\privacy-terms.local.txt`. That file is git-ignored and never
   leaves this PC. Any hit stops the build. The result is a zip in a temporary folder.
2. `tools\publish-public.ps1 -NoPush` unpacks that zip into `..\CaseKeeper-public` and checks it:
   - every image must be listed by SHA-256 in `tools\approved-images.txt`, meaning someone has
     looked at it, because text scans can't see pixels;
   - certificates, keys, debug symbols and packages are refused.

   It then makes one commit, `CaseKeeper <ver> - sanitised public source`, and a tag `v<ver>`,
   authored as `DarkCipher0x <DarkCipher0x@users.noreply.github.com>`, without pushing.
3. `dist\<ver>\CaseKeeper-src-v<ver>.zip` is `git archive --prefix=CaseKeeper-src-v<ver>/` of that tag:
   one top folder, forward slashes, byte-for-byte what anyone gets by archiving the tag themselves.
   The website only publishes source zips made this way.
4. At the end, `tools\publish-public.ps1` (without `-NoPush`) pushes the commit and the tag as they are.

Use `-NoPublish` to build without publishing, then `build-release.ps1 -PublishOnly` later. If a build
stops after step 2 and was never pushed, remove the local tag before building again: the build says how.
Add a new image to `tools\approved-images.txt` only after viewing it:

```powershell
(Get-FileHash assets\new-image.png -Algorithm SHA256).Hash.ToLower()
```

The public tree is replaced wholesale on every release, so edit the README and `.github\` here,
never on GitHub. That includes **Dependabot**: don't merge its pull requests on GitHub. Make the
same change here instead, and once the next release is published, Dependabot sees the update and
closes its pull request by itself.

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