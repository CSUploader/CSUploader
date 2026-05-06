# CSUploader

CSUploader is a Windows desktop application for uploading files to file hosting services. It provides a WPF interface for managing uploads with an upload wizard, queue, real-time progress tracking, and a connection manager for HTTP/HTTPS/SOCKS proxies.

## Features

- **Upload Wizard** — 4-step flow (directory → files → file hosters → start) with file filtering, hoster account selection, and a configurable start mode.
- **Upload Queue & Scheduling** — Concurrent CPU-bound (hashing) and upload jobs with per-host limits, pause/resume, priority ordering, and global speed limit.
- **File Hosters** — Pluggable architecture; Rapidgator currently shipping. Per-hoster account management with credential verification.
- **Connection Manager** — JD2-style proxy table with priority ordering, round-robin rotation, retry-on-fresh-proxy, multi-select test, live status icons (green check / red X), tested-OK filter on export, and import/export from text or file.
- **Dark / Light Themes** — Full WPF resource-dictionary swap; preference persists. Title bar uses Win11 immersive dark mode where supported.
- **System Tray** — Optional minimize-to-tray and close-to-tray with a first-run prompt to choose the close button's behaviour.
- **Auto-Update** — Velopack-driven background poll against GitHub Releases; install on demand via Help → Install Update.
- **Detailed HTTP Logging** — Every upload/test request lands in the Logs tab with full request + response, headers, hex dump, and the proxy used. Errors and status messages are categorised separately.
- **Upload History** — SQLite-backed log of completed uploads, browsable on the Uploaded tab with file-level URLs.

## Requirements

- Windows 10 (build 17763 / 1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for development; published builds are self-contained

## Build & Run

```bash
dotnet build
dotnet run --project src/CSUploader.csproj
dotnet test
```

The solution is `CSUploader.sln`. Source: `src/CSUploader.csproj`. Tests: `tests/CSUploader.Tests.csproj`. Target framework: `net10.0-windows10.0.17763.0`.

## Releasing

Auto-update is wired via Velopack against GitHub Releases on `CSUploader/CSUploader`.

1. Bump `<Version>` in `src/CSUploader.csproj` and commit.
2. Tag and push: `git tag v1.2.3 && git push origin v1.2.3`.
3. The `.github/workflows/release.yml` workflow tests, publishes self-contained `win-x64`, runs `vpk pack`, and creates a GitHub Release.
4. Running clients pick it up on their next 6-hour update poll, or via Help → Check for Updates.

The first release on a clean repo is a full bundle; later releases ship as delta patches automatically.

## Tech Stack

| Component | Technology |
|---|---|
| UI Framework | WPF on .NET 10 (MVVM) |
| MVVM | CommunityToolkit.Mvvm (source-generated `[ObservableProperty]` / `[RelayCommand]`) |
| Database | SQLite via EF Core 10 (`IDbContextFactory` per-operation contexts) |
| DI | Microsoft.Extensions.DependencyInjection / Hosting |
| Tray icon | `System.Windows.Forms.NotifyIcon` (WPF + WinForms hybrid) |
| Hashing | `System.Security.Cryptography.MD5` (Rapidgator's required hash) |
| HTTP | `HttpClient` with `WebProxy` (http/https/socks4/socks5 schemes) |
| Updates | Velopack (delta + full-bundle releases) |
| SVG icons | SharpVectors |
| Folder picker | Ookii.Dialogs.Wpf |

## Project Structure

```
src/
  App.xaml(.cs)        Entry point + DI composition root
  Converters/          IValueConverter implementations bound from XAML
  Dal/                 EF Core DbContext, entities (*Dbm), DTOs (*Dto), Repositories
  Lib/
    Crypto/            Hashing helpers
    Extensions/        Shared utility extensions
    Net/               ProxyManager, ProxyType/Result, HttpHandler, HttpTransaction
    UI/                Win32 interop (immersive dark title bar)
    Update/            IUpdateService + Velopack wrapper
  Properties/          Embedded resources (icons, logos)
  Resources/           Tokens.xaml + Theme.Light/Dark.xaml + ImageResources.xaml
  Services/            DialogService, TrayIconManager, ConfirmationKeys
  Upload/              FileHosterClient + scheduler + Package/PackageFile/PackageManager
    FileHosters/       Per-hoster API models
    Rapidgator/        Rapidgator API request/response models
  ViewModels/          One VM per primary surface, plus row VMs and item VMs
  Views/               *.xaml UserControls + *Window dialogs
tests/                 xUnit + Moq + in-memory SQLite (mirrors src/ layout)
```

## Architecture Highlights

- **Entry point** is `App.xaml.cs:OnStartup`. The DI container is built there and disposed on `OnExit`.
- **Layered MVVM** — Views (XAML) bind to ViewModels, ViewModels orchestrate DAL repositories + services. No code-behind business logic.
- **Repository pattern** — `Repository<TDbm, TDto>` base wraps EF Core; concrete repositories override `MapToDbm` / `MapToDto`. Schema migrations are raw SQL inside `FirstRun.cs` (`TableExists` + `CREATE TABLE` for new tables, `ALTER TABLE ADD COLUMN` for new columns).
- **Async + cancellation** — every long-running operation accepts a `CancellationToken`. Upload pause/resume uses a custom `PauseToken`.
- **Confirmation prompts** — `IDialogService.ShowOptOutConfirmation` with stable keys (`ConfirmationKeys`) so users can suppress repeats from the Settings page.
- **Settings dirty tracking** — `SettingsViewModel` snapshots saved values on Load/Save; navigating away from Settings with unsaved edits prompts the user.
- **HTTP logging** — `HttpHandler` builds an `HttpTransaction` per request (method, URL, headers, body, proxy, status, latency) and posts it to the Logs tab. Failures still produce a transaction so the proxy used for a failed call is visible.

## License

MIT — see [LICENSE](LICENSE).
