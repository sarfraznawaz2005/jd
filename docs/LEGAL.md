# Legal & acceptable use

JustDownload is a general-purpose download manager. It is your responsibility to use it lawfully and in
accordance with the terms of the sites and services you download from. This document defines JustDownload's
legal posture and contains the exact user-facing notice copy shown in the app.

## Terms-of-Service notice (shown once before media downloads)

Before the first media download (HLS / DASH / detected video or audio from a web page), JustDownload shows
a **one-time** notice. The user must acknowledge it before media extraction proceeds. This implements the
legal posture in [`PRD.md`](../PRD.md) §3.2 / §5.

The exact copy is:

> **Before you download media**
>
> JustDownload can download video and audio that it detects on web pages. Downloading content from some
> websites may violate that site's **Terms of Service**, and some content may be protected by copyright.
>
> - You are responsible for ensuring you have the right to download and use any content.
> - JustDownload only downloads streams that are openly accessible. It does **not** bypass or remove any
>   DRM or copy protection, and it will not attempt to do so.
> - JustDownload is not affiliated with, endorsed by, or sponsored by any website you download from.
>
> By continuing, you confirm that you understand this and will use JustDownload responsibly.
>
> [ Don't show this again ]   [ Cancel ]   [ I understand — continue ]

This notice is shown once and can be re-enabled from Settings. It is informational; it does not grant any
rights and is not legal advice.

## No DRM circumvention

JustDownload does **not** circumvent digital rights management. Content protected by **Widevine**,
**PlayReady**, FairPlay, or any equivalent DRM scheme is explicitly **out of scope** and will not be
supported. JustDownload will not decrypt DRM-protected streams, extract decryption keys from protected
content, or implement features whose only purpose is to defeat such protection. (HLS AES-128 segment
decryption, where a key URI is openly served as part of a standard non-DRM playlist, is a normal part of
HLS playback and is not DRM circumvention.)

## Honest, best-effort extraction

In-house media extraction is **best-effort**. Site-specific players (for example YouTube and Facebook)
actively obfuscate and rotate their stream URLs, so coverage for those sites is **limited and brittle**.
When extraction fails, JustDownload degrades gracefully with a clear "couldn't extract" message — it never
crashes and never pretends a download succeeded when it did not. JustDownload does **not** bundle yt-dlp; an
optional, user-enabled yt-dlp fallback can be downloaded on demand (D3), invoked as a separate process the
same way ffmpeg is, and only as a last resort after in-house extraction declines. It builds no feature whose
sole purpose is to evade a site's protections.

## No affiliation

JustDownload is an independent, open-source project. It is not affiliated with, endorsed by, or sponsored
by any website, streaming service, browser vendor, or third-party tool it interoperates with. All product
names, logos, and trademarks are the property of their respective owners.

## Privacy

JustDownload collects no telemetry, requires no account, and makes no network calls other than the
downloads you initiate and an **opt-in** update check. Credentials are stored in the operating system's
keychain, never in plaintext, and are redacted from logs. See [`README.md`](../README.md) and
[`CLAUDE.md`](../CLAUDE.md) §5 for details.

## License

JustDownload is distributed under the [MIT License](../LICENSE). Third-party components, including the
LGPL ffmpeg binary it invokes as a separate process, are covered in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

---

*This document is provided for clarity about how JustDownload behaves. It is not legal advice. If you are
unsure whether a particular download is permitted, consult the relevant site's terms and applicable law.*
