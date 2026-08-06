# CSUploader

CSUploader is a desktop application for **Windows** and **Linux** for uploading files to file hosting services. It provides an upload wizard, a queue with real-time progress tracking, and history of past uploads.

![CSUploader main window](docs/images/main-window.png)

## Features

- **Upload Wizard** — Guided flow for picking a directory, selecting files, choosing file hosters and accounts, and starting the upload.
- **Upload Queue** — Concurrent jobs with pause/resume, per-file upload order, and a global speed limit.
- **File Hosters** — Pluggable architecture with fifty-three hosters shipping: 1Fichier, Alfafile, BRupload, Buzzheavier, catbox.moe, Clicknupload, DailyUploads, DataVaults, DDownload, DropMeFiles, Easybytez, EliteFile, Ex-Load, FILEAXA, FileBoom, Filedot, File Garden, Filehoster.io, Filestank, GigaPeta, gofile.io, Hexload, HitFile, Hxfile, IcerBox, Isracloud, KatFile, Keep2Share, Litterbox, MediaFire, MEGA, NitroFlare, Pixeldrain, qu.ax, Rapidgator, Send.now, Sendspace, Storage.to, temp.sh, TeraBytez, TezFiles, tmpfiles.org, transfer.it, Turbobit, ufile.io, upload.ee, Uploadrar, Uploady, Upstore, UsersDrive, VikingFile, Webshare, and wormhole.app. Per-hoster account management with credential verification, plus anonymous (no-login) uploads for twenty-five of them: 1Fichier, Buzzheavier, catbox.moe, DailyUploads, DropMeFiles, FILEAXA, GigaPeta, gofile.io, Hexload, HitFile, Litterbox, qu.ax, Send.now, Sendspace, Storage.to, temp.sh, tmpfiles.org, transfer.it, ufile.io, upload.ee, Upstore, UsersDrive, VikingFile, Webshare, and wormhole.app. Three hosters upload with end-to-end encryption: transfer.it and wormhole.app (anonymous) and MEGA (into your account).
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
