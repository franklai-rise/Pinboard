# Contributing to Pinboard

Thanks for helping improve Pinboard.

## Before opening a change

- Search existing issues and keep each change focused.
- Never commit `.pinboard` files, screenshots, local settings, executable paths,
  personal folders, credentials, or generated release artifacts.
- For security reports, follow [SECURITY.md](SECURITY.md) instead of opening a
  public issue.

## Development setup

Pinboard builds on Windows with a supported .NET SDK, Node.js, and the WebView2
Runtime. From PowerShell, run:

```powershell
./scripts/build-release.ps1
```

For a quicker development cycle:

```powershell
cd web
npm ci
npm test
npm run build
cd ..
dotnet test PinboardApp.sln
```

Add tests for behavior changes. User-facing text must be present in both English
and Simplified Chinese resource dictionaries. Keep clipboard filters conservative
and describe their limitations honestly.

## Pull requests

Explain the user-visible change, testing performed, privacy implications, and
whether the `.pinboard` schema changes. By submitting a contribution, you agree
that it is licensed under the repository's MIT License.
