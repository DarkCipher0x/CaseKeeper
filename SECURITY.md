# Security policy

CaseKeeper holds some of the most sensitive papers a person has, so security reports are taken
seriously and dealt with first.

## Supported versions

Only the latest release gets security fixes. Upgrading is safe: installing a newer version over an
older one leaves your library untouched.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Anything older | No – please upgrade |

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately instead:

- on GitHub: the **Security** tab → **Report a vulnerability** (private advisory), or
- through the report form at <https://darkciphersystems.com/report.html>.

Please include the CaseKeeper version (*Settings → About*), your Windows version, and the steps to
reproduce it. **Never send real case documents or personal details.** If the problem only shows up
with a particular file, describe it or make a harmless copy that shows the same thing.

What to expect:

- an acknowledgement within 7 days;
- an assessment and a plan within 14 days;
- a fixed release as soon as one is ready, with credit in the changelog if you'd like it.

## How CaseKeeper protects data

- **Local only.** Documents, the database and logs stay on your computer. Nothing is uploaded. The
  optional AI talks only to the Ollama server you choose, normally your own PC. *Check for updates*
  only downloads, and only when you click it or switch on automatic checks.
- **Optional encryption.** A random 256-bit master key encrypts documents (AES-256-GCM) and the
  database (SQLCipher). The key is stored only in wrapped form, protected by a key derived from your
  password (PBKDF2-HMAC-SHA256, 600,000 iterations). There is deliberately no recovery path: a
  lost password means the data can't be recovered by anyone.
- **Auto-lock** after a period of inactivity and when Windows locks.
- **Tamper-evident audit trail**: every change is chained with SHA-256 fingerprints.
- **Logs contain no case content and no personal paths.**
- **Signed updates** (website version, *Settings → Updates*). The update information (`update.json`)
  is trusted only when it carries two valid signatures from the CaseKeeper release keys pinned in the
  app: ECDSA P-256 (`update.json.sig`) and post-quantum ML-DSA-65 (`update.json.pq.sig`). The private
  keys never leave the release PC; the website and GitHub only carry files. The app then requires a
  strictly newer version (no downgrade or replay), the setup program on the same https host, the
  signed size and SHA-256, a Windows program whose version information says CaseKeeper by DarkCipher
  Systems at exactly that version, and checks it all again just before it runs. Requests carry no
  cookies or identifiers, redirects are followed only within GitHub, and an optional GitHub token is
  DPAPI-encrypted and sent only to `api.github.com`. Installing always asks you, offers a backup, and
  confirms afterwards that the new version is running. The Microsoft Store copy is updated by the
  Store instead.

## Known limitations

These are documented so you can judge the risk for yourself:

- While the library is unlocked, the key is in the app's memory. Anyone who already controls your
  logged-in Windows account (malware, or someone at your unlocked PC) could read it, just as they
  could read your screen. Lock the library or Windows when you step away.
- The website installer isn't code-signed yet, so Windows SmartScreen shows a warning when it is run by
  hand. (In-app updates don't rely on that: they're checked against the release keys.) Check the
  SHA-256 published with each release, or install from the Microsoft Store, where Microsoft signs
  the package.
