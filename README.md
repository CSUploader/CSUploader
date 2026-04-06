# CSUploader

CSUploader is a Windows desktop application for uploading files to various file hosting services. It provides a user-friendly interface for managing uploads with advanced features like file compression, hashing, queue management, and progress tracking.

## Features

- **Multi-Platform Upload Support** - Upload files to file hosting services (Rapidgator, with extensible architecture for adding more)
- **Upload Queue Management** - Queue system with configurable concurrent upload limits, pause/resume, and priority ordering
- **File Compression** - Built-in 7-Zip compression with volume splitting, password protection, and progress tracking
- **File Hashing** - Async streaming hash computation (MD5, SHA256, etc.) with progress reporting
- **Upload History** - Local SQLite database tracking all uploads with browsable history
- **Detailed Logging** - Real-time categorized logging (Status, HTTP, Errors, UI) with source location info

## Requirements

- Windows 10 (version 1809+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build

```bash
dotnet build
```

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 10.0 (Windows Forms) |
| Database | SQLite via EF Core 10 |
| Serialization | System.Text.Json |
| Compression | 7-Zip (SevenZipSharp) |
| DI | Microsoft.Extensions.DependencyInjection |
| ListView | BrightIdeasSoftware ObjectListView |

## Project Structure

```
src/
  Controls/          Custom WinForms controls and view models
  Dal/               Data access layer (EF Core entities, stores, managers)
  Lib/               Core utilities (logging, compression, crypto, networking)
  Upload/            Upload engine (file hosters, packages, queue, settings)
    Rapidgator/      Rapidgator API models
  Views/             Windows Forms UI (MainForm with tab partials)
  Program.cs         Entry point and DI configuration
```

## Architecture

- **Layered architecture** with DI throughout: Views -> Upload/Business Logic -> DAL
- **Store/Manager pattern** with `IDbContextFactory` for EF Core context management
- **Event-driven async** with `CancellationToken` and custom `PauseToken` for pause/resume
- **Factory pattern** for file hoster client extensibility

## License

This project is licensed under the terms included in the LICENSE file.
