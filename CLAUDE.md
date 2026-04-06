# CLAUDE.md

## Build & Run

```bash
# Build
dotnet build

# Run
dotnet run --project src/CSUploader.csproj
```

The solution file is `CSUploader.sln` with a single project at `src/CSUploader.csproj`.
There are no tests yet. The project targets `net10.0-windows10.0.17763.0`.

## Code Style

- Follow the `.editorconfig` rules (Allman braces, 4-space indent, `_camelCase` private fields)
- File-scoped namespaces, nullable reference types enabled, implicit usings enabled
- Use `StringComparison.Ordinal` (or `OrdinalIgnoreCase`) for all string operations
- All source files must have the MIT license copyright header

## Architecture

### Dependency Injection
All services are registered in `Program.cs:ConfigureServices()`. Key registrations:
- `IDbContextFactory<CSUploaderDbContext>` - EF Core SQLite context factory
- `IAppLogger` / `Logger` - Application logging (singleton, also accessible via `Logger.Current` for non-DI code)
- `AppSettings` - Runtime settings (singleton, also accessible via `AppSettings.Current`)
- `SettingManager`, `FileHosterLoginManager`, `UploadPackageManager`, `UploadPackageFileManager` - DAL managers
- Store classes (`SettingStore`, etc.) - injected with `IDbContextFactory`

### Data Access Layer (src/Dal/)
- **Entities** (`*Dbm.cs`) - EF Core entities with `[Table]`, `[Key]`, `[Required]` attributes
- **DTOs** (`*Dto.cs`) - Data transfer objects used outside the DAL
- **Stores** (`*Store.cs`) - Generic `Store<T>` base with `IDbContextFactory` injection, per-operation context
- **Managers** (`*Manager.cs`) - Extend `StoreManager<Dbm, Dto, Store>` with manual `MapToDto`/`MapToDbm` methods
- No `DbManager` facade - managers are injected directly where needed

### Upload System (src/Upload/)
- `FileHosterClient` - Abstract base with static factory dictionary for file hoster implementations
- `RapidgatorClient` - HTTP-based Rapidgator upload client (only active hoster)
- `Package` / `PackageFile` / `PackageDetails` - Upload package hierarchy with compression, hashing, upload jobs
- `PackageManager` - Orchestrates packages with concurrent job limits from `AppSettings`

### UI (src/Views/)
- `MainForm` partial classes split by tab page (Upload, Uploads, Uploaded, Settings, Logs)
- `MainForm` receives `IServiceProvider` in constructor, resolves managers and services
- Uses `_logger` (IAppLogger) and `_settings` (AppSettings) injected fields in all partials

### Logging
- `IAppLogger` interface with `LogEventHandler` event pattern
- `Logger` class implements `IAppLogger`, registered as singleton
- `Logger.Current` static accessor for non-DI code (HttpHandler, RapidgatorClient, etc.)
- MainForm subscribes to `_logger.OnLogOutput` for real-time UI log display

## Key Conventions

- Entity classes use `Dbm` suffix, DTOs use `Dto` suffix
- Stores handle raw EF Core operations, Managers handle Dbm<->Dto mapping
- All async methods accept `CancellationToken`
- JSON serialization uses `System.Text.Json` with `[JsonPropertyName]` attributes
- Rapidgator API models are in `src/Upload/Rapidgator/` with `Response<T> : Response` pattern
