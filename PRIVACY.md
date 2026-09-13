# JustDownload Browser Extension — Privacy Policy

_Last updated: 2026-09-13_

JustDownload is free and open source. The browser extension has **no account,
no telemetry, and no analytics**. This page explains the only data the
extension touches and where it goes.

## What the extension reads

To find media (like videos) on the page you are viewing and hand them to the
JustDownload desktop app, the extension needs to read:

- The URL and content of the page you have open.
- Cookies for the site you are on — needed so the desktop app can download
  files that require you to be signed in (e.g. a video behind a login).
- Browser request headers, such as the referrer and user-agent — needed so
  the desktop app's download request looks like it came from your browser,
  the same way the page itself would have requested it.

## Where that data goes

- **To the JustDownload desktop app on your own computer** — over Chrome's
  Native Messaging, a direct local connection between the extension and the
  app. This never leaves your machine over the internet.
- **To the website itself** — for example, to read a video's playlist file
  (`.m3u8`) so the app knows what to download, the extension may request that
  file directly from the site you are already on.

**The extension never sends your data to any JustDownload server.** We do not
run a server that collects browsing data, cookies, or download history. We do
not sell or share data with anyone.

## Why each permission is requested

| Permission | Why |
|---|---|
| `<all_urls>` (host permission) | So the extension can detect media on any site you visit, not a fixed list. |
| `cookies` | To pass your sign-in session to the desktop app for authenticated downloads. |
| `webRequest` | To detect video/audio network requests the page makes (many sites hide the real video link from the page itself). |
| `downloads` | To optionally take over browser downloads and hand them to the desktop app. |
| `nativeMessaging` | To talk to the JustDownload desktop app. |
| `storage` | To remember your settings (e.g. sites you've blocked) locally in the browser. |
| `contextMenus` | To add a right-click "Download with JustDownload" option. |

## Data retention

The extension keeps a short-lived, in-memory list of media it has detected
per open tab, and your blocked-sites list in local browser storage
(`chrome.storage`/`browser.storage`). Nothing is uploaded or backed up by us.

## Changes

If this policy changes, this file will be updated and the date above will
change.

## Contact

Questions or concerns: open an issue at
https://github.com/sarfraznawaz2005/jd/issues
