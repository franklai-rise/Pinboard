# Pinboard website

Public URL: **https://franklai.com/Pinboard/**

The website lives in this repository, separate from the desktop application's `web/` canvas. It uses plain HTML, CSS, and JavaScript with no runtime dependencies or third-party fonts/analytics.

## Edit and preview

```powershell
node scripts/build-site.mjs
node scripts/check-site.mjs
node scripts/preview-site.mjs
```

Open the localhost URL printed by the preview command. After edits, rebuild and reload. The preview server serves `artifacts/site` with the correct MIME types and binds only to loopback.

Source: `site/index.html`, `site/styles.css`, `site/app.js`. Public screenshots are reused from `docs/images/`, not copied from a real user library. The interactive canvas is a small simulation with fake data; it does not access the clipboard, capture the screen, or store typed demo text. Only the language preference is kept in browser storage.

## Publish

The `Website` workflow builds the explicitly allowed static files into `artifacts/site`, verifies links and destinations, and publishes that directory using GitHub Pages. A push touching website files or public images redeploys it. A successful `Release` workflow also redeploys it, refreshing version and package size from **this repository's** latest release. The checked-in `site/release.json` is a validated fallback if the API is temporarily unavailable; update it when preparing a later release.

The account-level domain is `franklai.com`. This project inherits it at `/Pinboard/`. **Do not add a project CNAME for the apex domain** or change the account-level website.

All program download buttons must stay under `https://github.com/franklai-rise/Pinboard/releases/latest/download/`. Never point them to another product's release. The build uploads neither the repository root nor any `.pinboard`, user settings, recovery files, or desktop binary.

## Validation

Automated structural checks: `node scripts/check-site.mjs`.

Browser checks: Chinese/English switching, add screenshot and text samples, edit notes, drag or arrow-key move cards, zoom, jump to bottom, reset, screenshot tabs and image dialog, FAQ, 390 px and desktop layouts, and download destinations. The webpage demo is explicitly not a full browser version of Pinboard.
