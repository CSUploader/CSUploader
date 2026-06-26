# CSUploader

CSUploader is a Windows desktop application for uploading files to file hosting services. It provides an upload wizard, a queue with real-time progress tracking, and history of past uploads.

![CSUploader main window](docs/images/main-window.png)

## Features

- **Upload Wizard** — Guided flow for picking a directory, selecting files, choosing file hosters and accounts, and starting the upload.
- **Upload Queue** — Concurrent jobs with pause/resume, priority ordering, and a global speed limit.
- **File Hosters** — Pluggable architecture with fourteen hosters shipping: Rapidgator, Alfafile, BRupload, Ex-Load, FileBoom, Keep2Share, KatFile, TakeFile, Hexload, Hxfile, IcerBox, Isracloud, GigaPeta, and HitFile. Per-hoster account management with credential verification, plus anonymous (no-login) uploads for GigaPeta, Hexload, and HitFile.
- **Connection Manager** — Optional proxy support with priority ordering, automatic rotation on retry, and connectivity testing.
- **Dark / Light Themes** — Choose your preferred theme; the choice persists between sessions.
- **System Tray** — Optional minimize-to-tray and close-to-tray with a first-run prompt to choose the close button's behaviour.
- **Auto-Update** — New releases are picked up in the background; install on demand via Help → Install Update.
- **Upload History** — Browsable log of completed uploads on the Uploaded tab with file-level URLs.

## Requirements

- Windows 10 (build 17763 / 1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for development; published builds are self-contained

## Build & Run

```bash
dotnet build
dotnet run --project src/CSUploader.csproj
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
