# CSUploader

CSUploader is a desktop application for **Windows** and **Linux** for uploading files to file hosting services. It provides an upload wizard, a queue with real-time progress tracking, and history of past uploads.

![CSUploader main window](docs/images/main-window.png)

## Features

- **Upload Wizard** — Guided flow for gathering files from any number of folders in an explorer view, choosing file hosters and accounts, and starting the upload.
- **Upload Queue** — Concurrent jobs with pause/resume, per-file upload order, and a global speed limit.
- **File Hosters** — Pluggable architecture with seventy-seven hosters shipping: 1Fichier, Alfafile, BowFile, BRupload, BtaFile, Buzzheavier, catbox.moe, Clicknupload, DailyUploads, DataNodes, DataVaults, DDownload, DepositFiles, DropMB, DropMeFiles, Easybytez, EliteFile, Emload, Ex-Load, FILEAXA, Filebin, FileBoom, FileCat, Filedot, File Garden, Filego, Filehoster.io, FileMirage, Filestank, FileStore, GigaFile, GigaPeta, gofile.io, Hexload, HitFile, Hostize, Hxfile, IcerBox, Isracloud, KatFile, Keep2Share, kshared, Litterbox, MediaFire, MEGA, MegaUp, NitroFlare, Pixeldrain, PreFiles, qu.ax, Rapidgator, Send.now, Sendspace, Storage.to, SubyShare, temp.sh, TeraBytez, TezFiles, tmpfiles.org, transfer.it, Turbobit, udrop, ufile.io, upload.ee, UploadGIG, UploadHive, UploadNow, Uploadrar, Uploady, Upstore, UpZur, UsersDrive, VikingFile, Webshare, World Files, wormhole.app, and Xubster. Per-hoster account management with credential verification, plus anonymous (no-login) uploads for forty-one of them: 1Fichier, BowFile, BtaFile, Buzzheavier, catbox.moe, DailyUploads, DataNodes, DropMB, DropMeFiles, FILEAXA, Filebin, Filego, FileMirage, GigaFile, GigaPeta, gofile.io, Hexload, HitFile, Hostize, Litterbox, MegaUp, qu.ax, Send.now, Sendspace, Storage.to, temp.sh, tmpfiles.org, transfer.it, udrop, ufile.io, upload.ee, UploadHive, UploadNow, Upstore, UpZur, UsersDrive, VikingFile, Webshare, World Files, wormhole.app, and Xubster. Three hosters upload with end-to-end encryption: transfer.it and wormhole.app (anonymous) and MEGA (into your account).
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
