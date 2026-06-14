# Changelog

All notable changes to CSUploader are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.0.3]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.3
[0.0.2]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.2
[0.0.1]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.1
