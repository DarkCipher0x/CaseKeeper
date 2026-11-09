# Publishing CaseKeeper in the Microsoft Store

This guide covers everything from opening a developer account to a live Store listing. It sits
alongside the website installer, which stays as it is (see [RELEASING.md](../RELEASING.md)).

## How it works

| | Website installer | Microsoft Store |
|---|---|---|
| File | `dist\<version>\CaseKeeper-Setup-v<version>.exe` (Inno Setup) | `dist\<version>\CaseKeeper-v<version>-x64.msix` |
| Built by | `build-release.ps1` | `build-store.ps1` |
| Signed by | Nobody yet, so SmartScreen warns ([below](#signing-the-website-installer)) | **Microsoft**, automatically, after certification |
| Certificate cost | £0 unsigned; roughly $10 a month or £200–400 a year signed | **£0** |
| Updates | *Settings → Updates*: signed in-app updates (from 0.5.0), or the new installer by hand | The Store updates it automatically; the in-app updater is switched off |
| Where the library lives | `%LOCALAPPDATA%\CaseKeeper` | `%USERPROFILE%\CaseKeeper` |
| Settings and logs | `%APPDATA%\CaseKeeper`, `%LOCALAPPDATA%\CaseKeeper\Logs` | The package's own folder (Windows removes it on uninstall) |
| Removing the library | The uninstaller asks (default: keep) | *Settings → Factory reset → Remove all CaseKeeper data* |

You don't buy a certificate for the Store. Microsoft re-signs every package it accepts with its own
certificate, so people installing from the Store get no warnings at all. `build-store.ps1` still
signs the package, with a free self-signed certificate. Windows only installs signed packages, and
that signature is what lets you try the package on your own PC. The Store throws it away and
replaces it.

### What the package changes, and why

The Store copy is the same program as the website copy, packaged differently. The package asks for
**no restricted capability except `runFullTrust`**, which every packaged desktop program needs.

1. **The library is kept outside AppData.** In a packaged app, new files under AppData go into a
   hidden per-package folder that Windows **deletes when the app is uninstalled**. That is right
   for settings and logs, so the Store copy keeps those in its own package folder and lets Windows
   clean them up. It would be wrong for someone's case, so the Store copy's library lives in
   `%USERPROFILE%\CaseKeeper`: an ordinary folder people can see and back up, which an uninstall or
   reinstall never deletes. (Not Documents: OneDrive often syncs Documents to the cloud without
   asking, and a live database in a syncing folder can be damaged.) `CaseKeeper\Services\AppPaths.cs`
   holds all of this, and the Store copy refuses to move a library into AppData.

   CaseKeeper 0.4.0 used the `unvirtualizedResources` restricted capability to share
   `%LOCALAPPDATA%\CaseKeeper` with the website copy. Microsoft declined it (policy 10.6.3): it's
   meant for games and similar, and could stop an uninstall cleaning up. 0.4.1 removed it, and
   0.4.2 was the version resubmitted.
2. **Moving from the website copy.** The first time the Store copy opens on a PC with a website
   library and no Store library, it offers to copy it across. Every file is checked by SHA-256
   after copying, nothing is ever overwritten, and the original isn't changed, so the website copy
   keeps working. A failed copy is taken back completely.
3. **Removing data.** The Store copy has no uninstaller of its own. *Settings → Factory reset →
   Remove all CaseKeeper data* deletes every local library CaseKeeper knows about plus its settings,
   logs and temporary copies (`CaseKeeper\Services\DataRemoval.cs`). The website uninstaller runs
   the same thing (`CaseKeeper.exe --remove-data`) when the person says yes.
4. **Restarting goes through Windows.** Restoring a backup, a factory reset and turning encryption
   on or off all restart the app. A Store copy has to restart through its Start-menu entry so it
   keeps its package identity (`CaseKeeper\Services\AppPackage.cs`). The website copy restarts
   exactly as before.
5. **Windows 10 2004 or newer is required.** Every supported Windows 10 is newer than that anyway.

Temporary readable copies for **Open** and **Share** go to `%TEMP%\CaseKeeper-Share` in both copies.
Windows doesn't redirect `%TEMP%` for packaged apps (checked on 30 October 2026 with a test package
and no restricted capability), so other programs can open them.

Nothing else differs. Encryption, the database, text recognition, the optional Ollama AI and the
optional cloud AI all work the same way, with one exception: *Install Ollama* in the website version
runs Windows Package Manager; the Store version shows *Get Ollama*, which only opens ollama.com in
the browser, so a Store copy never starts an installer (policy 10.2.2).

### Privacy

Version 0.6.0 adds an optional cloud AI. In Partner Center's privacy questions and on the privacy
page, say: no telemetry and no data collected by the developer; data stays on the PC unless the user
chooses a cloud AI provider in Settings → AI, in which case the text of documents they analyse is
sent to that provider (Anthropic, OpenAI or the user's own Microsoft Foundry resource) under the
provider's terms, masked by default and only after consent. The privacy policy URL must cover this
before 0.6.0 or later is submitted (the website's privacy page does, from 8 November 2026).

- The package contains only the program, the user guide and the patch notes.
- `build-store.ps1` refuses to produce a package if any of CaseKeeper's own files contain this PC's
  user folder, its name, or your git e-mail address.
- The only names in the package are the brand ("DarkCipher Systems") and the publisher ID that
  Partner Center gives you (a `CN=` code, not your name).
- When you open the developer account, Microsoft checks your identity. Those details stay with
  Microsoft. The Store shows only the **publisher display name** you choose, which should be
  `DarkCipher Systems`.
- The listing asks for a support contact. Use a brand e-mail address or the website's
  report page, not a personal address.

---

## Step 1: Open a developer account (once)

1. Go to <https://storedeveloper.microsoft.com> and sign in with the Microsoft account you want to
   own the app. A brand account is better than a personal one.
2. Choose **Individual** (no registration fee under the current onboarding) or **Company** (needs a
   registered business that Microsoft verifies).
3. For **Publisher display name**, enter `DarkCipher Systems`. This is the only name customers see.
4. Complete the identity check. It can take a few days.

## Step 2: Reserve the name and copy the identity (once)

1. In [Partner Center](https://partner.microsoft.com/dashboard), go to **Apps and games → New
   product → MSIX or PWA app** and reserve the name **CaseKeeper**. If someone already has it, try
   something like *CaseKeeper – Court Documents*. Whatever you reserve is the name shown in the
   Store.
2. Open the product, then **Product management → Product identity**. Copy these three values:
   - **Package/Identity/Name** (looks like `12345DarkCipherSystems.CaseKeeper`)
   - **Package/Identity/Publisher** (looks like `CN=1A2B3C4D-…`)
   - **Package/Properties/PublisherDisplayName** (`DarkCipher Systems`)
3. Copy `packaging\store\store-identity.example.json` to `packaging\store\store-identity.json` and
   paste the values in. They aren't secret, because they appear in every installed copy, but the
   file is git-ignored anyway, so the account's identifiers stay out of the public repository.
   Keep your own copy of it with your release files.

> If you reserved a name other than "CaseKeeper", change the `$DISPLAY_NAME$` value near the end of
> `build-store.ps1` (`Replace('$DISPLAY_NAME$', 'CaseKeeper')`) to match it exactly.

## Step 3: Build the package

```powershell
powershell -ExecutionPolicy Bypass -File build-store.ps1
```

This publishes the app, lays out the package, generates every tile and taskbar image size from
`assets\casekeeper-icon.png`, runs the privacy check, packs `dist\CaseKeeper-v<version>-x64.msix`
and signs it. At the end it prints the size and SHA-256.

The packaging tools (MakeAppx, MakePri, SignTool) come from the Windows SDK. If the SDK isn't
installed, add `-FetchTools`. That downloads Microsoft's `Microsoft.Windows.SDK.BuildTools`
package from nuget.org (about 21 MB) into `obj\store\tools`, and installs nothing. If the tools are
already in your NuGet cache, the script uses those. Add `-SkipBuild` to repackage the existing
`publish\` folder without rebuilding.

Without `store-identity.json`, the script builds a **TEST** package (`…-TEST.msix`) with a made-up
identity. That's fine for trying the package out, but the Store rejects it.

The first build creates a self-signed certificate named after the publisher in your personal
certificate store (**Manage user certificates → Personal → Certificates**, friendly name *CaseKeeper
package test signing*). Later builds reuse it. Its public half is saved as
`dist\CaseKeeper-package-test.cer`. It's only used for local testing.

## Step 4: Try the package before submitting it

Windows only installs a package whose certificate it trusts. Pick **one** of these:

**A. Developer Mode** (simplest, nothing to trust). Turn on **Settings → System → For developers →
Developer Mode**, then in PowerShell:

```powershell
Add-AppxPackage -Register .\obj\store\layout\AppxManifest.xml
```

This installs straight from the build folder.

**B. Trust the test certificate.** In an **administrator** PowerShell:

```powershell
Import-Certificate -FilePath .\dist\CaseKeeper-package-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then double-click the `.msix`. Afterwards, remove the trust again in an administrator PowerShell:

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=DarkCipher Systems Test' | Remove-Item
```

Use your real `CN=` publisher instead if you built with `store-identity.json`.

**What to check.** The Store copy uses `%USERPROFILE%\CaseKeeper`. If a website library exists, it
offers to copy it (the original is only read). To test from scratch, use a Windows account with no
library.

- [ ] It starts from the Start menu and shows the CaseKeeper icon in Start and on the taskbar.
- [ ] With a website library present, the first start offers to copy it, and the copy opens with
      everything in it.
- [ ] Adding a document puts it in `%USERPROFILE%\CaseKeeper\Files`, not under
      `%LOCALAPPDATA%\Packages\…`.
- [ ] **Open** and **Share** on a document work, so the other program can see the copy.
- [ ] Turning encryption on or off restarts the app and it comes back.
- [ ] The user guide and "What's new" open.
- [ ] **Settings → Logs → Open log folder** opens the package's `LocalState\Logs` folder.
- [ ] After uninstalling (**Settings → Apps**), `%USERPROFILE%\CaseKeeper` is still there with
      everything in it, and `%LOCALAPPDATA%\Packages\<package>` has gone.
- [ ] **Remove all CaseKeeper data** lists the library, deletes it after DELETE is typed, and
      closes the app.

To remove the test copy, uninstall it from **Settings → Apps → Installed apps**, or run:

```powershell
Get-AppxPackage *CaseKeeper* | Remove-AppxPackage
```

Neither touches the library. The website copy can stay installed alongside the Store copy; each
has its own library unless you point one at the other's (*Settings → Your library*).

**Run the Windows App Certification Kit before every submission.** It runs the same automated tests
the Store runs: package and manifest checks, blocked programs, launch and crash tests, and binary
security checks. It comes with the Windows SDK (`winget install Microsoft.WindowsSDK.10.0.26100`).
From an **administrator** PowerShell in the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File tools\run-wack.ps1
```

It tests the newest `dist\<version>\CaseKeeper-v<version>-x64.msix`. It trusts the test certificate
only for the run, then removes the trust, the test install and the empty library its launch test
creates. A real Store copy and a library holding documents are never touched. It prints each failed
test with Microsoft's message, and the full report goes to `dist\<version>\wack-report.xml`. Don't
submit unless it says **Overall result: PASS**.

## Step 5: Submit

In Partner Center, open CaseKeeper and click **Start your submission**.

**Pricing and availability**
- Markets: **United Kingdom** is enough, because the forms and guidance are for England and Wales.
  Add more if you like.
- Price: **Free**, unless you've decided otherwise.

**Properties**
- Category: **Productivity**. Subcategory: none, or **Business** if offered.
- Privacy policy URL: `https://darkciphersystems.com/privacy.html`. It's required, because the app
  stores personal data. That page already covers CaseKeeper: no telemetry, data stays on the PC.
- Website: `https://darkciphersystems.com/software.html`
- Support contact: a brand e-mail address or `https://darkciphersystems.com/report.html`
- System requirements: x64; about 300 MB of disk space plus the documents. Note that the optional
  AI needs [Ollama](https://ollama.com), installed separately, or the user's own account with
  Anthropic, OpenAI or Microsoft Foundry.

**Age ratings.** Answer the questionnaire honestly. The app has no user-to-user communication, no
purchases and no unrestricted web access. The *content* people store may involve sensitive family
matters, but the app itself shows none.

**Packages.** Upload `dist\CaseKeeper-v<version>-x64.msix`. The page should show
*Windows 10 Desktop, x64, min. 10.0.19041.0*.

**Store listing (English (United Kingdom))**
- Description: start from the top of `README.md`. Say clearly that it is **not legal advice**. End
  with where the data goes, for example:

  > Your case library is kept on your PC in your user folder (%USERPROFILE%\CaseKeeper), never
  > uploaded. Uninstalling keeps it, so reinstalling picks up where you left off. To delete it, use
  > Settings → Factory reset → Remove all CaseKeeper data before uninstalling.
- What's new: write it for **Store users**, from the newest section of `CHANGELOG.md`. Leave out
  everything about the website version's in-app updater (*Check for updates*, *Download and verify*,
  installing, update sources, GitHub, signing keys) and the *For developers* notes. A Store listing
  that describes downloading and installing updates from outside the Store invites a policy 10.2.2
  rejection, even though the Store copy can't do it. For 0.8.0, use:

  > - The Track sections fill themselves: what the AI finds in your documents is added to Case &
  >   hearings, the Timeline, Directions & tasks, the Contact log, Complaints, Parties and the Scott
  >   schedule, marked to check, and can be undone.
  > - Case memory: what the AI knows about your case, kept in your library (encrypted if protected);
  >   it learns from your decisions, and you can see and forget everything.
  > - Ask about your case, with every answer linked to the page it came from.
  > - Where your case stands, on the Dashboard.
  > - Documents: the latest version of each document, most important first.
  > - Reliability and security improvements.

  For 0.7.0, the text was:

  > - Check my library: organise everything already in CaseKeeper in one go, with AI or without:
  >   categories, parties and contacts, hearings, the timeline, complaints and the Scott schedule.
  >   You choose the sections, and approve every suggestion.
  > - Complaint letters and replies are filed in Complaints and filled in for you; a reply updates
  >   the complaint it answers.
  > - Duplicates & versions shows which copy is the latest edit, using the date saved inside the
  >   file, with side-by-side comparison and moving older copies to Deleted items.
  > - New Case map: your documents by category and time, with hearings, orders and deadlines, and
  >   an AI-written overview of the whole case.
  > - The sidebar shows when a cloud AI is in use.
  > - Reliability and security improvements.

  For 0.6.2 (or 0.6.1), the first 0.6 version in the Store (0.6.0 was never submitted), the text was:

  > - Choose your AI in Settings → AI: the local AI on this PC (Ollama), or Anthropic (Claude),
  >   OpenAI (GPT) or either on Microsoft Foundry with your own key. Pick the model and reasoning
  >   effort. Cloud AI is off until you choose it.
  > - Before anything is sent to a cloud AI, a review screen shows where it goes and what's
  >   sensitive. Names and personal details are masked by default.
  > - New Duplicates & versions review in Bulk Case Import: finds copies and drafts of the same
  >   document and shows which has the newest edits.
  > - Encrypted files are now bound to their own names, for stronger protection.
  > - Sharper text on high-resolution and mixed-scaling monitors.
  > - Reliability and security improvements.

  For 0.5.1 (and 0.5.0), the text was:

  > - Settings → Updates now explains that the Microsoft Store keeps CaseKeeper up to date
  >   (Microsoft Store → Library → Get updates).
  > - New Guide & FAQ article, *Updating CaseKeeper*, and a matching help-assistant answer.
  > - Remove all CaseKeeper data now also clears any temporary update files.
  > - Reliability and security improvements.
- Screenshots: at least one, ideally 4–6, at 1366×768 or larger. Use a **demo library with made-up
  names**. Never use real case material. A ready set (a fictional case, 1586×933) is in
  `assets\store-screenshots`. Don't use a Settings screenshot: it shows the PC's audit-trail code.
- Logos: the Store uses the package icons unless you upload others. The optional ones are in
  `assets\store-art`, drawn by `packaging\store\make-store-art.ps1` (run it again after changing the
  icon). Each file is named after its Partner Center box: 9:16 poster art (720×1080), 1:1 box art
  (1080×1080), 16:9 super hero art (1920×1080, no title, as Partner Center requires), and the
  300×300, 150×150 and 71×71 app tile icons.
- Keywords, for example: family court, C100, court bundle, documents, timeline, evidence,
  encrypted.

**Submission options → restricted capabilities.** The package declares only `runFullTrust`. If
Partner Center asks why, you can paste this:

> CaseKeeper is a desktop (WPF) application packaged with the Desktop Bridge, so it requires
> runFullTrust. It declares no other restricted capability.

If Partner Center still shows an old justification for `unvirtualizedResources` from an earlier
submission, delete it: that capability is no longer in the package.

**Notes for certification:**

> No account or sign-in is needed. On first run CaseKeeper creates an empty local library in
> %USERPROFILE%\CaseKeeper. To test: Documents → Add documents, choose any PDF or image. The AI
> features are optional and off by default. They use either a local Ollama server
> (https://ollama.com), which downloads AI models (data, not code) only when the person asks, or a
> cloud AI service the person sets up with their own API key (Anthropic, OpenAI or Microsoft
> Foundry, chosen in Settings → AI). Without either, the app says the AI is unavailable and
> everything else works. Nothing is sent over the internet unless the person chooses a cloud AI:
> then the text of the documents they ask it to analyse is sent over HTTPS to that service only,
> with names and personal details masked by default, and only after a consent screen that shows
> where it goes. The "Get Ollama" button only opens ollama.com in the browser; the app never runs
> an installer. Its Settings → Updates section is
> switched off in the Store version and only says that the Store delivers updates; the app never
> downloads or runs code from anywhere else.
>
> This version no longer uses the unvirtualizedResources capability. The user's library is kept in
> %USERPROFILE%\CaseKeeper so that uninstalling never destroys their legal evidence. Users are told
> this in the app (Settings → Factory reset) and in the user guide, and Settings → Factory reset →
> "Remove all CaseKeeper data" deletes it, along with the app's settings, logs and temporary files.

Then **Submit for certification**. Certification usually takes from a few hours to three working
days. Partner Center e-mails you the result, and a rejection report says exactly what to fix.

## Every later release

1. Bump the version in `CaseKeeper\CaseKeeper.csproj` and add the CHANGELOG entry, as in
   [RELEASING.md](../RELEASING.md).
2. Run `build-release.ps1`. It builds the website installer and the Store package from the same
   build, into `dist\<version>\`.
3. In Partner Center: **Update** the submission, replace the package, update *What's new*, submit.

Each Store upload needs a version higher than the last one. The fourth part of the version is
always `0`, as the Store requires. The Store updates installed copies automatically. The in-app
updater (*Settings → Updates*, from 0.5.0) is for the website version only: in a Store copy it is
switched off in code (it checks for a package identity), never contacts the internet, and only says
that the Store delivers updates. So nothing conflicts, and the package never downloads or runs code
from outside the Store, as Store policy requires.

## Signing the website installer

This is separate from the Store, and optional. The Store copy needs none of it.

The website installer is unsigned, so SmartScreen warns first-time downloaders. To sign it you need
a real code-signing certificate:

- **Azure Artifact Signing** (formerly Trusted Signing): about $9.99 a month, no hardware token,
  and Microsoft's recommended option. Check eligibility first: it covers organisations in the UK
  and EU, but individuals only in the USA and Canada.
- **An OV certificate** from a certificate authority such as Sectigo or DigiCert: roughly £200–400
  a year. Reputation with SmartScreen still builds up over time.

Once you have one, the same certificate can also sign an `.msix` for direct download outside the
Store:

```powershell
powershell -ExecutionPolicy Bypass -File build-store.ps1 -CertThumbprint <thumbprint>
```

For that, the certificate's subject must equal the publisher in `store-identity.json`. Use a
separate identity file for website MSIX builds if they differ.

Publishing in the Store is the free way to reach people with no warnings. It is also the reason
this guide exists.

## Troubleshooting

| Problem | Fix |
|---|---|
| `MakeAppx.exe not found` | Run with `-FetchTools`, or install the Windows SDK. |
| `Privacy check failed` | One of CaseKeeper's own files contains your user folder, PC name or e-mail. Find where it comes from (usually a path baked in by a build setting) and remove it. Don't ship around it. |
| Partner Center: *"The package identity doesn't match"* | `store-identity.json` doesn't match **Product identity**. Copy the values again, exactly. |
| Partner Center: *"Version must be higher"* | Bump `<Version>` in the csproj and rebuild. |
| Double-clicking the `.msix` says the publisher isn't trusted | That's expected for a test build. See [Step 4](#step-4-try-the-package-before-submitting-it). |
| The Store copy shows an empty library | Each copy has its own library: the Store copy uses `%USERPROFILE%\CaseKeeper`, the website copy `%LOCALAPPDATA%\CaseKeeper`. *Settings → Your library* shows which one is open, and **Use a different library…** switches to the other. The first start of a Store copy offers to copy a website library across. |
| `tools\run-wack.ps1` reports a failure | Fix what the message names, rebuild with `build-release.ps1`, and run it again. Don't submit a package that fails it. |
