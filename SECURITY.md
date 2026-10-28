# Security policy

## Supported versions

Pinboard is currently pre-1.0 software. Security fixes are provided for the
latest published version only.

## Reporting a vulnerability

Please use GitHub's **Report a vulnerability** private reporting form in the
Security tab. Do not include credentials, private board contents, screenshots,
or other sensitive data in a public issue.

Include the affected version, Windows version, reproduction steps, and the
security impact. A maintainer will acknowledge a complete report within seven
days. Publication and remediation timing will be coordinated with the reporter.

## Local-data boundary

Pinboard is designed to work locally and does not require an account. Board
files and copied-text clips are not encrypted by Pinboard. Clipboard privacy
filters and application exclusions are best-effort safeguards, not guarantees.
Protect the Windows account and storage volume when board contents are sensitive.

AI-session backup is an optional, separately authorized feature. Only selected
conversations are uploaded to an app-owned private Google Drive folder using
`drive.file`. Raw records may contain sensitive messages or tokens; the backups
are not additionally encrypted. OAuth configuration and refresh tokens are
protected with Windows user-level DPAPI outside the archive. Source credentials,
browser caches, and unselected shared-database rows are excluded. Restores use a
new directory and verify content hashes before confirming individual files.
