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
  optional AI talks only to the Ollama server you choose, normally your own PC.
- **Optional encryption.** A random 256-bit master key encrypts documents (AES-256-GCM) and the
  database (SQLCipher). The key is stored only in wrapped form, protected by a key derived from your
  password (PBKDF2-HMAC-SHA256, 600,000 iterations). There is deliberately no recovery path: a
  lost password means the data can't be recovered by anyone.
- **Auto-lock** after a period of inactivity and when Windows locks.
- **Tamper-evident audit trail**: every change is chained with SHA-256 fingerprints.
- **Logs contain no case content and no personal paths.**

## Known limitations

These are documented so you can judge the risk for yourself:

- While the library is unlocked, the key is in the app's memory. Anyone who already controls your
  logged-in Windows account (malware, or someone at your unlocked PC) could read it, just as they
  could read your screen. Lock the library or Windows when you step away.
- The website installer isn't code-signed yet, so Windows SmartScreen shows a warning. Check the
  SHA-256 published with each release, or install from the Microsoft Store, where Microsoft signs
  the package.
