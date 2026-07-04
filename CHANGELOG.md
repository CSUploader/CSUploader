# Changelog

All notable changes to CSUploader are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.0.6] - 2026-07-05

Eleven new file hosters (including three end-to-end-encrypted, no-login services), byte-weighted package progress and ETA, a per-hoster max-file-size column in the wizard, and a SQLite security fix. See [docs/release-notes/v0.0.6.md](docs/release-notes/v0.0.6.md) for the full notes.

### Added

- **Eleven new file hosters**: **transfer.it**, **mega.nz**, and **wormhole.app** (all end-to-end-encrypted); **Storage.to**, **catbox.moe**, **gofile.io**, and **ufile.io** (anonymous, several also logged-in); and **MediaFire**, **Pixeldrain**, **File Garden**, and **Filehoster.io** (account uploads).
- **ufile.io tiered accounts** (Free / Pro / Business) with per-tier max file size, simultaneous-upload limits, and storage reporting — backed by a new per-pipeline concurrent-upload cap and two new `AccountType` values.
- **"Max file size" column** on the wizard's hoster step.
- **Instant-link de-duplication** for MediaFire (identical content links without re-uploading) and a pre-upload existence check for File Garden.

### Changed

- **Package progress and ETA are byte-weighted** across the whole package instead of averaging per-file percentages, so a part-way-through large file reports true completion and a whole-package time-remaining.
- Accounts grid shows **"-"** when a hoster reports no Used figure and **"Unlimited"** for capless hosts (e.g. catbox); the wizard Summary names the account's free space in an amber auto-fit notice.
- The Uploads grid shows a **horizontal scrollbar** on column overflow rather than clipping.

### Security

- Cleared **CVE-2025-6965** (high-severity SQLite memory corruption, `NU1903`) by updating the bundled SQLite native library past the fix (SQLite 3.50.4).

## [0.0.5] - 2026-06-29

Five new file hosters, Rapidgator storage reporting, a non-ASCII filename fix, and a dead-hoster cleanup. See [docs/release-notes/v0.0.5.md](docs/release-notes/v0.0.5.md) for the full notes.

### Added

- **Five new file hosters**: **Isracloud** (XFileSharing web-form, session sign-in), **Keep2Share** and **TezFiles** (shared "moneyplatform" backend), **NitroFlare** (reCAPTCHA sign-in, account hash), and **Upstore** (anonymous *and* logged-in).
- **Rapidgator storage reporting** (used / available) in the Accounts grid and the upload wizard.

### Fixed

- **Non-ASCII upload filenames** (Japanese, etc.) no longer arrive on the server as `?????` — multipart filenames are sent as raw UTF-8, the way a browser does.
- **Upload order is respected** when the first file needs hashing before upload — it no longer leapfrogs a queued #1 to start #2.
- **Removing a package's only file** now removes the empty package row too.
- **Uploads / History context menus** apply every action to all selected rows, not just the focused one.

### Removed

- **Disabled** (retained in-tree): **TakeFile** (Cloudflare managed-challenge TLS fingerprinting) and **UploadGIG** (no working upload capture).
- **Removed** dead / premium-only / unusable hosts: Novafile, Openload, Rapidu, RareFile, ShareOnline, UbiqFile, UniBytes, Uploaded, and WuShare.

## [0.0.4] - 2026-06-26

A new IcerBox hoster, HitFile account uploads, a wizard that fits your selection to each account's live free space, per-file upload order, and a bounded upload-retry layer. See [docs/release-notes/v0.0.4.md](docs/release-notes/v0.0.4.md) for the full notes.

### Added

- **IcerBox** file hoster (custom Bearer-JWT REST API, email / password) with used / available storage reporting.
- **HitFile registered-account uploads** (previously anonymous-only), with cookie-based storage refresh.
- **Per-hoster capacity fit on the wizard Summary page**: each selected account's free space is re-checked live (no sign-in window), files that don't fit are unchecked largest-first, over-capacity blocks **Next**, and a per-hoster "N unchecked to fit" clue plus a grand-total footer explain the result.
- **Numeric per-file upload Order** with Move up / down / to and renumber, editable in the Uploads grid.
- **"Force start"** to launch a queued upload over the concurrency limit, re-hashing and re-uploading an already-completed file on confirmation.
- Sign-in failures shown as a compact `Error: …` line with a **Details** dialog carrying the full server response.
- Account / Added-at / Started-at columns across the grids; Method / URL / Proxy columns and show-hide-reorder for the Logs HTTP grid.

### Changed

- A shared, bounded **upload-retry layer** retries only faults that provably never created a file (mid-send body aborts, connect-phase failures) and never retries a chunked upload mid-stream; Alfafile / Rapidgator auto-retry "state 3" finalize failures and re-authenticate on a mid-upload 401.
- Replaced the per-package **priority** field with the per-file upload Order.
- Column renames: Uploads "Duration" → "Elapsed", "Save to" → "Path".

### Removed

- **Hotlink** disabled — free accounts cannot upload and the API key is unobtainable. Retained in-tree pending a usable path.

## [0.0.3] - 2026-06-14

Ten new file hosters, anonymous uploads, a chunked upload protocol, and account storage reporting. See [docs/release-notes/v0.0.3.md](docs/release-notes/v0.0.3.md) for the full notes.

### Added

- **Ten new file hosters** alongside Rapidgator: Alfafile, BRupload, Ex-Load, FileBoom, KatFile, TakeFile, Hexload, Hxfile, Hotlink, and GigaPeta — each plugging into the `IFileHosterPipeline` architecture with its own verification and upload flow.
- **Anonymous (no-login) uploads** for GigaPeta and Hexload, offered as an "Anonymous" entry in the wizard's account picker and persisted across restarts.
- **Chunked XFileSharing upload protocol** for modern CDN-backed hosters (per-chunk upload + finalize), with a classic single-multipart fallback.
- **WebView2 captcha sign-in** for hosters whose login is captcha-gated, plus API-key bootstrap for the XFileSharing family.
- **Account storage (used / available)** in the Account Manager, with an explicit "Unlimited" state and binary IEC units.
- Hoster icons in the Account Manager grid and wizard; per-request header capture in the HTTP transcript.
- Package priority as a real five-level scheduling primitive; per-hoster max-file-size / max-files validation in the wizard.

### Changed

- Multipart uploads reshaped to match a real browser (field order, quoted `name=`, `Origin` / `Sec-Fetch-*` headers, User-Agent).
- Uploads and account checks refuse to run when "Use Proxies" is on but no proxy is available.
- Connection Manager rework (Edit Proxy dialog), reliable dark title bar across Windows 10 / 11, alphabetical hoster lists, and Settings polish.

### Removed

- **FilesMonster** and **Filecloud** (no usable free upload path). **FlashBit** and **ExtMatrix** are retained in-tree but disabled pending upstream fixes.

## [0.0.2] - 2026-05-13

Upload reliability against real Rapidgator behaviour, schedule-aware starting, and a redesigned wizard. See [docs/release-notes/v0.0.2.md](docs/release-notes/v0.0.2.md) for the full notes.

### Added

- New **Upload files** wizard mode for picking individual files, with mode / source / file list consolidated into a single step.
- **Scheduled at** column and a schedule-aware **Start all uploads** that skips files scheduled for the future.
- Live **hash speed / progress** during the hashing phase.

### Changed

- Rapidgator: poll `upload_info` until Done / Fail (3-minute budget, exponential backoff); per-credentials login gate so parallel uploads share one login round-trip.
- Package status no longer flips to **Failed** while siblings are still uploading — a mixed result reports **Done with errors**; persisted `StartedDate` / `FinishedDate` / `Duration` survive a restart.

## [0.0.1] - 2026-05-10

First public release.

### Added

- **Upload pipeline.** Per-attempt `AttemptRunner` with proxy selection, HTTP handler construction, and a unified `UploadEvent` stream consumed by `PackageFile` and `ProxyManager`. Hoster-specific upload logic implements `IFileHosterPipeline` (currently Rapidgator).
- **Upload wizard.** Four-step flow: directory → files → file hosters → start. Filter, "Select all" / "Deselect all", and a per-file checkbox grid on the Files step.
- **Uploads / History tabs.** Live DataGrid of active packages with start / pause / stop / soft-remove, plus a separate History tab for completed uploads. Hidden-columns persistence per tab.
- **Completion-toast notifications.** Bottom-right popups (JD2-style) when a file finishes uploading, plus a per-package summary when the whole package terminates. Stacking, 5-second auto-dismiss, hover-pause, click-to-activate. Toggleable in Settings → General → Notifications.
- **Settings page** with General, Upload, Connection, and Accounts sections. Persisted via SQLite. Live theme switching (light / dark), language picker (6 languages), grid font controls, concurrency limits, autostart policy, "if file exists" behaviour, and minimize-to-tray / close-action options.
- **Connection Manager.** Proxy grid with import / export (text or file, all or tested-OK), per-proxy Test, "Test all", and live status feedback driven by `ProxyManager.ProxyResultObserved` events from the upload pipeline.
- **Logs tab.** Four channels (Status, HTTP, Errors, UI) with detail dialogs for HTTP transactions and log entries. Persisted to the local database.
- **System-tray integration.** `NotifyIcon`-based tray icon with show / exit menu, single-click restore, and a one-shot first-hide balloon tip. Visibility is driven by user settings.
- **Localization** in English, 简体中文, 日本語, 한국어, Tiếng Việt, and Filipino.
- **Auto-update** wired through Velopack against GitHub Releases. Six-hour poll plus a manual Help → Check for Updates entry.

### Notes

- Targets `net10.0-windows10.0.17763.0` (Windows 10 1809+).
- Self-contained `win-x64` build is published from the release workflow; first install is a full bundle, subsequent updates are delta patches.

[0.0.6]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.6
[0.0.5]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.5
[0.0.4]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.4
[0.0.3]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.3
[0.0.2]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.2
[0.0.1]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.1
