# CSUploader

CSUploader is a desktop application for **Windows** and **Linux** for uploading files to file hosting services. It provides an upload wizard, a queue with real-time progress tracking, and history of past uploads.

![CSUploader main window](docs/images/main-window.png)

## Features

- **Upload Wizard** — Guided flow for picking a directory, selecting files, choosing file hosters and accounts, and starting the upload.
- **Upload Queue** — Concurrent jobs with pause/resume, per-file upload order, and a global speed limit.
- **File Hosters** — Pluggable architecture with thirty-one hosters shipping: Alfafile, BRupload, Buzzheavier, catbox.moe, DropGalaxy, Ex-Load, FileBoom, File Garden, Filehoster.io, GigaPeta, gofile.io, Hexload, HitFile, Hxfile, IcerBox, Isracloud, KatFile, Keep2Share, MEGA, MediaFire, NitroFlare, Pixeldrain, Rapidgator, Send.now, Storage.to, TezFiles, transfer.it, ufile.io, Uploady, Upstore, and wormhole.app. Per-hoster account management with credential verification, plus anonymous (no-login) uploads for Buzzheavier, catbox.moe, DropGalaxy, GigaPeta, gofile.io, Hexload, HitFile, Send.now, Storage.to, transfer.it, ufile.io, Uploady, Upstore, and wormhole.app. Three hosters upload with end-to-end encryption: transfer.it and wormhole.app (anonymous) and MEGA (into your account).
- **Connection Manager** — Optional proxy support with priority ordering, automatic rotation on retry, and connectivity testing.
- **Dark / Light Themes** — Choose your preferred theme; the choice persists between sessions.
- **System Tray** — Optional minimize-to-tray and close-to-tray with a first-run prompt to choose the close button's behaviour.
- **Auto-Update** — New releases are picked up in the background; install on demand via Help → Install Update.
- **Upload History** — Browsable log of completed uploads on the Uploaded tab with file-level URLs.

## Requirements

- **Windows**: Windows 10 (build 17763 / 1809+) or Windows 11
- **Linux**: x64 with glibc (tested on Ubuntu) — ships as a portable AppImage
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for development; published builds are self-contained

## Install

- **Windows**: run the installer from the latest release; the app keeps itself up to date.
- **Linux**: download `CSUploader.AppImage` from the latest release, then:

  ```bash
  chmod +x CSUploader.AppImage
  ./CSUploader.AppImage
  ```

  No installer and no dependencies; the app self-updates from the same release feed.

## Build & Run

```bash
dotnet build
dotnet run --project src/CSUploader/CSUploader.csproj
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
