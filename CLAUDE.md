# CLAUDE.md

## Build & Run

```bash
# Build
dotnet build

# Run
dotnet run --project src/CSUploader.csproj

# Test
dotnet test
```

The solution file is `CSUploader.sln`. Source lives at `src/CSUploader.csproj`, tests at `tests/CSUploader.Tests.csproj`. The project targets `net10.0-windows10.0.17763.0`.

`UseWPF=true` and `UseWindowsForms=true` are both set — the WinForms reference is only there for `System.Windows.Forms.NotifyIcon` (system-tray icon). The WinForms global usings are removed in the csproj so they don't collide with WPF (`Application`, `UserControl`, `Brush`, `DataGrid`, `ContextMenu` all overlap).

## Releasing

Auto-update is wired via Velopack against GitHub Releases on `CSUploader/CSUploader`.

1. Bump `<Version>` in `src/CSUploader.csproj` and commit.
2. Tag and push: `git tag v1.2.3 && git push origin v1.2.3`.
3. The `.github/workflows/release.yml` workflow runs `dotnet test`, builds a self-contained `win-x64` publish, runs `vpk pack`, and creates a GitHub Release with the artifacts.
4. Running clients pick it up on their next 6-hour update poll (or via Help → Check for Updates).

The first release on a clean repo is a full bundle; later releases get delta patches automatically.

## Testing

- New repository methods, view-models, and services must include xUnit tests in `tests/`.
- Test layout mirrors source: `src/Dal/Foo.cs` → `tests/Dal/FooTests.cs`.
- Use the in-memory SQLite pattern in `tests/Dal/FileHosterLoginRepositoryTests.cs` for repository tests.
- Use Moq for `IDialogService`, `IAppLogger`, and other interfaces — concrete classes (`PackageManager`, `UploadScheduler`) are constructed with real in-memory dependencies.
- Run `dotnet test` and confirm all tests pass before reporting work as done. The post-compile `apphost.exe` copy fails when the running app holds the EXE — that's a file lock, not a test failure; close the app and rerun.
- See `tests/CLAUDE.md` for naming, structure, and fixture conventions.

## Code Style

- Follow the `.editorconfig` rules (Allman braces, 4-space indent, `_camelCase` private fields)
- File-scoped namespaces, nullable reference types enabled, implicit usings enabled
- Use `StringComparison.Ordinal` (or `OrdinalIgnoreCase`) for all string operations
- All source files must have the MIT license copyright header
- XAML uses 2-space indent (per .editorconfig)

## Architecture

### UI Framework: WPF with MVVM

- **CommunityToolkit.Mvvm** for `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generators.
- **Ookii.Dialogs.Wpf** for folder picker dialogs.
- Entry point is `App.xaml` / `App.xaml.cs` (no Program.cs).
- Themes ship as merged `ResourceDictionary` files (`Resources/Tokens.xaml` + `Theme.Light.xaml` / `Theme.Dark.xaml`); the active theme is swapped at runtime in `MainViewModel.ApplyTheme`.

### Dependency Injection

All services are registered in `App.xaml.cs:ConfigureServices()`. Key registrations:

- `IDbContextFactory<CSUploaderDbContext>` — EF Core SQLite context factory.
- `IAppLogger` / `Logger` — Application logging (singleton, also accessible via `Logger.Current` for non-DI code).
- `AppSettings` — Runtime settings (singleton, also accessible via `AppSettings.Current`).
- `IDialogService` / `DialogService` — MessageBox, confirmation, folder/file dialogs.
- `IUpdateService` / `UpdateService` — Velopack wrapper for the auto-update poll/install flow.
- `TrayIconManager` — Owns the `System.Windows.Forms.NotifyIcon` and tray menu; visibility is driven by settings.
- `PackageManager`, `UploadScheduler` — Upload orchestration (singletons).
- `Lib.Net.ProxyManager` — Proxy rotation, test runner, and the live `ProxyResultObserved` event for upload feedback.
- DAL repositories: `SettingRepository`, `FileHosterLoginRepository`, `UploadPackageRepository`, `UploadPackageFileRepository`, `ProxySettingRepository`.
- ViewModels are registered as singletons so the UI can rebind without losing state.

### ViewModels (src/ViewModels/)

- `MainViewModel` — Orchestrator, holds sub-VMs, manages dark-mode state, intercepts tab navigation for unsaved-Settings warnings, drives the update-check timer.
- `UploadWizardViewModel` — 4-step upload wizard (directory → files → file hosters → start).
- `UploadsViewModel` — Active packages with `DispatcherTimer` refresh, start/pause/stop, soft-remove flow.
- `UploadedViewModel` — Historical packages from the database (file-level rows joined to package name).
- `SettingsViewModel` — Settings CRUD with snapshot-based dirty tracking and `TryConfirmDiscardChanges()` opt-out prompt.
- `ConnectionManagerViewModel` — JD2-style proxy grid, Test/TestAll, Import/Export (text/file, all/tested-OK), live status from `ProxyManager.ProxyResultObserved`.
- `LogsViewModel` — Four `ObservableCollection`s (Status / Http / Errors / UI), auto-scroll.
- `FileHosterSelectionViewModel` — Row VM for the wizard's hoster list (use checkbox + account selector).
- `ProxySettingItem` — Row VM wrapping `ProxySettingDto` for the Connection Manager grid.
- `LogEntryViewModel`, `UploadedFileRow`, `SuppressedConfirmationItem` — small row/item VMs.

### Views (src/Views/)

- `MainWindow.xaml` — `TabControl` with 4 tabs: Uploads, Uploaded, Settings, Logs. Hosts the menu bar (File / View / Help).
- `UploadWizardWindow.xaml` — Modal upload wizard.
- `UploadsView.xaml` / `UploadedView.xaml` — DataGrid-based tabs (file-level rows, ItemContainerStyle for hierarchy).
- `SettingsView.xaml` — Sidebar nav (General / Upload / Connection / Accounts) with content panels switched by `SelectedCategoryIndex`.
- `LogsView.xaml` — 4-tab DataGrid for categorized logs.
- Dialogs: `AboutWindow`, `ConfirmationDialog`, `CloseActionDialog`, `EditAccountWindow`, `HttpDetailsWindow`, `LogDetailsWindow`, `ProgressWindow`, `ProxyTextDialog`, `SpeedLimitDialog`, `UpdateProgressWindow`.

### Converters (src/Converters/)

`ByteUnitConverter`, `TimeSpanFormatConverter`, `DateTimeFormatConverter`, `BoolToVisibilityConverter`, `InvertBoolConverter`, `EnumBoolConverter`, `StatusToColorConverter`, `ProgressWidthConverter` (multi-value), `FileTypeIconConverter`, `HosterIconConverter`, `FileStateIconConverter`, `FileStateDisplayConverter`, `ProxyTestOutcomeIconConverter`, `SpeedLimitConverter`, `ItemStateToVisibilityConverter`, `StepConverters`.

### Services (src/Services/)

- `IDialogService` / `DialogService` — Confirmations, folder/file picks, `ShowOptOutConfirmation` with persisted suppression.
- `ConfirmationKeys` — Stable string keys for opt-out-able prompts (`RemoveUploadPackageOrFile`, `RemoveUploadedEntry`, `RemoveFileHosterAccount`, `RemoveProxy`, `DiscardSettingsChanges`). Listed in `ConfirmationKeys.All` so `SettingsViewModel.RefreshConfirmationPrompts` can render the user-facing toggle list.
- `TrayIconManager` — Owns `NotifyIcon`, watches `AppSettings` flags to show/hide; emits a one-shot balloon tip on first hide per session.

### Resources (src/Resources/)

- `Tokens.xaml` — Implicit styles (Button, ScrollBar, DataGrid*, ComboBox, etc.) and shared brushes/sizes.
- `Theme.Light.xaml` / `Theme.Dark.xaml` — Themed brushes (Accent, Surface, Text, ScrollBar, etc.). Swapped at runtime.
- `ImageResources.xaml` — All image assets as `BitmapImage` resources, accessed via `{StaticResource KeyName}`.

### Data Access Layer (src/Dal/)

- **Entities** (`*Dbm.cs`) — EF Core entities with `[Table]`, `[Key]`, `[Required]` attributes.
- **DTOs** (`*Dto.cs`) — Plain data classes used outside the DAL.
- **Repositories** (`*Repository.cs`) — Inherit from generic `Repository<TDbm, TDto>` (in `Repository.cs`); each implementation overrides `MapToDbm` / `MapToDto` and adds entity-specific queries (e.g. `SoftRemoveFromUploadsAsync`, `IncrementProblemsAsync` *(retired)*, `FindByKeyAsync`).
- **Schema migrations** — `FirstRun.cs` runs `EnsureCreated()` then patches existing databases via `TableExists` + raw SQL (`CREATE TABLE`, `ALTER TABLE ADD COLUMN`). EF reads/writes only mapped properties so legacy columns are harmless.

### Networking (src/Lib/Net/)

- `ProxyManager` — Loads enabled proxies from the DB (priority-ordered), hands them out via round-robin `NextProxy()`, runs connectivity tests (`TestProxyAsync` routes through `HttpHandler` so the request lands in the Logs tab), and raises `ProxyResultObserved` events for live UI updates.
- `HttpHandler` — Wraps `HttpClient` and produces an `HttpTransaction` per request (logs to Http channel). Constructor takes the proxy description and a `bypassMockServer` flag for the connectivity-test path.
- `HttpTransaction` — Captures method, URL, request/response headers, bodies, status, latency, and the proxy used.
- `ProxyType` enum (`None / Http / Https / Socks4 / Socks5`) maps to `WebProxy` URI schemes (`socks4://`, `socks5://` are built into .NET 6+).

### Upload System (src/Upload/)

- `FileHosterClient` — Abstract base with a static factory dictionary. `RefreshConnection()` is overridden by hosters with long-lived `HttpClient`s so retried files pick a fresh proxy.
- `RapidgatorClient` — HTTP-based client (only active hoster). Builds its `HttpHandler` with the next rotation proxy.
- `Package` / `PackageFile` — In-memory upload hierarchy. Persisted via `UploadPackageRepository` / `UploadPackageFileRepository` with soft-remove flags (`IsHidden` for the Uploaded tab, `IsRemovedFromUploads` for the Uploads tab — independent).
- `PackageManager` — Orchestrates packages with concurrent job limits from `AppSettings`. On terminal file-state changes, calls `ProxyManager.Current?.ReportResult` so the Connection grid reflects the live outcome.
- `UploadScheduler` — Background loop dispatching ready files to file-hoster clients respecting CPU/upload concurrency limits.

### Logging

- `IAppLogger` interface with a `LogEventHandler` event pattern.
- `Logger` class implements `IAppLogger`, registered as singleton.
- `Logger.Current` static accessor for non-DI code (`HttpHandler`, `RapidgatorClient`, etc.).
- `MainViewModel` wires `_logger.OnLogOutput` to `LogsViewModel.AddLogEntry()` via the dispatcher.
- HTTP transactions land in `LogType.Http`; categorical errors go to `LogType.Error`; status messages to `LogType.Status`; UI events to `LogType.UI`.

## Key Conventions

- ViewModels use CommunityToolkit.Mvvm source generators (`partial` classes with `[ObservableProperty]`, `[RelayCommand]`).
- Entity classes use the `Dbm` suffix; DTOs use `Dto`.
- Repositories handle EF Core operations and Dbm↔Dto mapping; ViewModels and services consume DTOs only.
- All async methods accept `CancellationToken`.
- JSON serialization uses `System.Text.Json` with `[JsonPropertyName]` attributes.
- Rapidgator API models are in `src/Upload/Rapidgator/` with the `Response<T> : Response` pattern.
- Cross-thread UI updates use `Application.Current.Dispatcher` (`BeginInvoke` / `Invoke`).
- Image resources accessed via `{StaticResource KeyName}` from `ImageResources.xaml`.
- Confirmation prompts always go through `IDialogService.ShowOptOutConfirmation(ConfirmationKeys.X, message, title)` and the key must be listed in `ConfirmationKeys.All` so the Settings page can re-enable it.
- Persisted user preferences are added to `AppSettings`, hydrated in `SettingsViewModel.LoadAsync`, and saved via `SettingsViewModel.SaveAsync` (or the owning page's Save command).
