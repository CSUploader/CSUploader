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

## Releasing

Auto-update is wired via Velopack against GitHub Releases on `CSUploader/CSUploader`.

To cut a new release:

1. Bump `<Version>` in `src/CSUploader.csproj` and commit.
2. Tag and push: `git tag v1.2.3 && git push origin v1.2.3`.
3. The `.github/workflows/release.yml` workflow runs `dotnet test`, builds a self-contained
   `win-x64` publish, runs `vpk pack`, and creates a GitHub Release with the artifacts.
4. Running clients pick it up on their next 6-hour update poll (or via Help → Check for Updates).

For ad-hoc local builds, run `./publish.ps1` (add `-Push` to also create the release via `gh`).
The first release on a clean repo must be a full bundle; later releases get delta patches automatically.

## Testing

- New repository methods, view-models, and services must include xUnit tests in `tests/`.
- Test layout mirrors source: `src/Dal/Foo.cs` → `tests/Dal/FooTests.cs`.
- Use the in-memory SQLite pattern in `tests/Dal/FileHosterLoginRepositoryTests.cs` for repository tests.
- Use Moq for `IDialogService`, `IAppLogger`, and other interfaces — concrete classes (`PackageManager`, `UploadScheduler`) are constructed with real in-memory dependencies.
- Run `dotnet test` and confirm all tests pass before reporting work as done.
- See `tests/CLAUDE.md` for naming, structure, and fixture conventions.

## Code Style

- Follow the `.editorconfig` rules (Allman braces, 4-space indent, `_camelCase` private fields)
- File-scoped namespaces, nullable reference types enabled, implicit usings enabled
- Use `StringComparison.Ordinal` (or `OrdinalIgnoreCase`) for all string operations
- All source files must have the MIT license copyright header
- XAML uses 2-space indent (per .editorconfig)

## Architecture

### UI Framework: WPF with MVVM
- **CommunityToolkit.Mvvm** for `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generators
- **Ookii.Dialogs.Wpf** for folder picker dialogs
- Entry point is `App.xaml` / `App.xaml.cs` (no Program.cs)

### Dependency Injection
All services are registered in `App.xaml.cs:ConfigureServices()`. Key registrations:
- `IDbContextFactory<CSUploaderDbContext>` - EF Core SQLite context factory
- `IAppLogger` / `Logger` - Application logging (singleton, also accessible via `Logger.Current` for non-DI code)
- `AppSettings` - Runtime settings (singleton, also accessible via `AppSettings.Current`)
- `IDialogService` / `DialogService` - MessageBox, folder/file dialogs
- `PackageManager` - Upload orchestration (singleton)
- `SettingManager`, `FileHosterLoginManager`, `UploadPackageManager`, `UploadPackageFileManager` - DAL managers
- Store classes (`SettingStore`, etc.) - injected with `IDbContextFactory`
- ViewModels (`MainViewModel`, `UploadViewModel`, etc.) - transient

### ViewModels (src/ViewModels/)
- `MainViewModel` - Orchestrator, holds sub-VMs, wires IAppLogger to LogsViewModel
- `UploadViewModel` - Upload form (directory, compression, file hosters, upload command)
- `UploadsViewModel` - Active packages with DispatcherTimer refresh, start/pause/stop
- `UploadedViewModel` - Historical packages from database
- `SettingsViewModel` - Settings CRUD with save command
- `LogsViewModel` - 4 ObservableCollections (one per LogType), auto-scroll
- `FileHosterSelectionViewModel` - Checkbox + account selector for file hoster
- `LogEntryViewModel` - Read-only log entry wrapper

### Views (src/Views/)
- `MainWindow.xaml` - TabControl with 5 tabs, each hosting a UserControl
- `UploadView.xaml` - GroupBox-based form for upload configuration
- `UploadsView.xaml` - TreeView with HierarchicalDataTemplate (Package -> PackageFile)
- `UploadedView.xaml` - TreeView for historical uploads
- `SettingsView.xaml` - Simple settings form
- `LogsView.xaml` - 4-tab DataGrid for categorized logs
- `ProgressWindow.xaml` - Modal indeterminate progress dialog
- `LogDetailsWindow.xaml` - Full log entry detail view

### Converters (src/Converters/)
- `ByteUnitConverter` - long bytes -> friendly string
- `TimeSpanFormatConverter` - TimeSpan -> tiered format
- `DateTimeFormatConverter` - DateTime -> "yyyy/MM/dd HH:mm:ss"
- `BoolToVisibilityConverter` - bool -> Visibility
- `ProgressWidthConverter` - IMultiValueConverter for progress bar width

### Services (src/Services/)
- `IDialogService` / `DialogService` - Replaces GUIHelper for dialogs

### Resources (src/Resources/)
- `ImageResources.xaml` - All 37 image assets as BitmapImage resources

### Data Access Layer (src/Dal/)
- **Entities** (`*Dbm.cs`) - EF Core entities with `[Table]`, `[Key]`, `[Required]` attributes
- **DTOs** (`*Dto.cs`) - Data transfer objects used outside the DAL
- **Stores** (`*Store.cs`) - Generic `Store<T>` base with `IDbContextFactory` injection, per-operation context
- **Managers** (`*Manager.cs`) - Extend `StoreManager<Dbm, Dto, Store>` with manual `MapToDto`/`MapToDbm` methods

### Upload System (src/Upload/)
- `FileHosterClient` - Abstract base with static factory dictionary for file hoster implementations
- `RapidgatorClient` - HTTP-based Rapidgator upload client (only active hoster)
- `Package` / `PackageFile` / `PackageDetails` - Upload package hierarchy with compression, hashing, upload jobs
- `PackageManager` - Orchestrates packages with concurrent job limits from `AppSettings`

### Logging
- `IAppLogger` interface with `LogEventHandler` event pattern
- `Logger` class implements `IAppLogger`, registered as singleton
- `Logger.Current` static accessor for non-DI code (HttpHandler, RapidgatorClient, etc.)
- MainViewModel wires `_logger.OnLogOutput` to `LogsViewModel.AddLogEntry()` via Dispatcher

## Key Conventions

- ViewModels use CommunityToolkit.Mvvm source generators (`partial` classes with `[ObservableProperty]`, `[RelayCommand]`)
- Entity classes use `Dbm` suffix, DTOs use `Dto` suffix
- Stores handle raw EF Core operations, Managers handle Dbm<->Dto mapping
- All async methods accept `CancellationToken`
- JSON serialization uses `System.Text.Json` with `[JsonPropertyName]` attributes
- Rapidgator API models are in `src/Upload/Rapidgator/` with `Response<T> : Response` pattern
- Cross-thread UI updates use `Application.Current.Dispatcher.Invoke()` (not WinForms InvokeRequired)
- Image resources accessed via `{StaticResource KeyName}` from `ImageResources.xaml`
