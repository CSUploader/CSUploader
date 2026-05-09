# Changelog

All notable changes to CSUploader are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.0.1]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.1
