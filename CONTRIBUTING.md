# Contributing to CaseKeeper

Thanks for helping. CaseKeeper is used by people in some of the hardest moments of their lives, so the
bar is: **private, stable, and plain English.**

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree the approach.
- Read [SECURITY.md](SECURITY.md). Security problems are reported privately, never in a pull request.

## Ground rules

1. **No personal data, ever.** Not in code, tests, screenshots, sample files, commit messages or
   issues. Test data must be obviously invented, like the fictional *Alex and Jordan Morgan* demo case.
2. **Nothing leaves the computer.** No telemetry, analytics or network calls, apart from the
   user-chosen Ollama server.
3. **Never lose user data.** Upgrades must keep old libraries working. Anything destructive needs a
   confirmation and, where possible, an undo or the Deleted items folder.
4. **Logs stay clean.** No case content, document names or user paths in logs or error messages that
   might be shared.
5. **Plain English** in the interface, written for a parent handling their own case. Legal terms are
   explained on first use.
6. **Not legal advice.** Guide content describes procedure and links to official sources. It never
   predicts outcomes or tells people what to do.

## Building and testing

You need the .NET 8 SDK on Windows.

```powershell
dotnet build CaseKeeper
dotnet run --project CaseKeeper
dotnet run --project tests\CaseKeeper.Tests.Docs -- "$env:TEMP\ck-test-output"
dotnet run --project tests\CaseKeeper.Tests.Data
dotnet run --project tests\CaseKeeper.Tests.BulkImport
```

All three test programs must pass (exit code 0). CI runs them on every pull request.

Set `CASEKEEPER_LIBRARY` to a scratch folder while developing, so you never touch a real library:

```powershell
$env:CASEKEEPER_LIBRARY = "$env:TEMP\ck-dev-library"
dotnet run --project CaseKeeper
```

## Pull requests

- Keep each pull request to one change, and match the style of the surrounding code.
- Add or update tests for behaviour changes, and add a line under **Unreleased** in
  [CHANGELOG.md](CHANGELOG.md).
- Update [USER-GUIDE.md](USER-GUIDE.md) when something a user sees changes.
- By contributing you agree that your work is released under the [MIT licence](LICENSE).
