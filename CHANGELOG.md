# Changelog

All notable changes to CSUploader are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **The update prompt shows what's new.** When an update is found at startup, the prompt includes
  the release's notes in a scrollable section — the same notes the GitHub release shows — so
  "Update now or later?" is an informed question. Updates released before v1.6.0 carry no notes,
  so the section first appears when a v1.6.0 user is offered the release after it.
- **The History tab has a search bar.** Type to filter by file name, package name, hoster or URL —
  case-insensitive, and searching a package name keeps the whole package visible. Groups expand so
  matches are never hidden under a collapsed package, and an active search stays applied when the
  list reloads.
- **Every language now says every string.** The 51 strings that still showed English in Japanese,
  Korean, Simplified Chinese, Vietnamese and Filipino — the copy-links menu, the account sign-in
  flow, the browser sign-in window, the Logs columns and the upload wizard's summary page — are
  translated.
- **CSUploader checks for updates before it opens.** A small splash says "Checking for updates…"
  while it does, and anything it finds is offered before the main window appears — deliberately
  before queued uploads can auto-start, so choosing to update never interrupts a transfer already
  running. Two settings under **Settings → General** decide how it behaves. *Check for updates at
  startup* (default on) decides whether the check happens in front of the window or quietly behind
  it; turning it off is not turning updates off, it moves the check to just after the window opens,
  and the six-hourly poll is unaffected either way. *Install updates automatically at startup*
  (default off, and only available while the first is on) installs what the startup check finds
  without asking — off keeps the existing behaviour, because installing hands over to the updater,
  which replaces the app and restarts it.

### Changed

- **Builds that are not installed check for updates too.** Running from source or from unpacked
  build output used to skip the check entirely. It now reads the release feed directly and reports a
  newer version through **Help → Check for Updates** — while never offering to install one, because
  an uninstalled build has no packaged copy for the updater to replace and the offer could only
  fail.
- **Help → Check for Updates stops waiting after 20 seconds.** It joins a check that is already
  running rather than starting a second one, and a startup check cannot be cancelled, so the menu
  item could previously sit there for as long as the request took.

### Fixed

- **The About box showed a stale version.** It read the assembly version, which the project file
  pins to a literal and the release tag therefore never reaches — so a shipped 1.6.0 would have gone
  on calling itself 1.5.0.0, beside an update prompt that correctly said 1.6.0. Both now read the
  same value.

## [1.5.0] - 2026-08-24

Uploads use every connection a host allows, at the speed you actually asked for: five hosters send a
file's parts in parallel, and the speed limit — which was multiplying itself across concurrent
uploads — became a single shared budget. See
[docs/release-notes/v1.5.0.md](docs/release-notes/v1.5.0.md) for the full notes.

### Added

- **Parallel part uploads** for VikingFile, Hostize, DataNodes, UploadNow and storage.to. A benchmark
  against VikingFile settled that this was worth doing first: it throttles per connection, so eight
  parts ran 2.57x faster than one and had not plateaued there. **Settings → Upload → Max parallel
  parts per file** caps it, default 4; set it to 1 for exactly the previous behaviour. The cap is per
  file, so five concurrent files at 4 means up to 20 connections. Hosters declare their own support
  rather than the app assuming, since a host that throttles per account gains nothing.
- **The update window shows bytes, speed and time remaining** under its progress bar. When the update
  is applied as a delta there are no byte figures, only the bar, the percentage and the countdown —
  the updater reports one percentage for an operation that downloads several deltas concurrently and
  then patches, so the same percentage can mean many different byte totals and none can be recovered.
  Full downloads show everything.

### Fixed

- **The speed limit applied per hoster instead of per upload.** Four chunks in flight each received
  the full budget, so a 1 MB/s cap uploaded at 4 MB/s — measured at 394 kB/s against a configured
  100 kB/s. It is one shared token bucket per scope now, accruing continuously rather than in fixed
  windows. Per-package and per-file overrides may still exceed the global limit, as before.
- **MEGA and transfer.it ignored the speed limit entirely**, because they write ciphertext straight to
  a WebSocket and never reach the layer that throttles every other hoster. They are paced in fragments
  now: charging a whole 1 MiB chunk up front would wait ten seconds at 100 kB/s and then burst a
  megabyte, and at low limits the wait alone outlasted the idle watchdog and cancelled healthy
  uploads.
- **A failed multipart upload left its parts on the host's storage indefinitely**, and every retry
  abandoned another set. An incomplete multipart is invisible to the account's file list and to the
  site's own UI, so nothing ever collected them. UploadNow is the only one of the five that exposes an
  abort, and now sends it — on its own bounded timeout, since the usual reason to be here is that the
  upload was cancelled.
- **UploadNow could report a failed upload as a finished one.** Its storage returns errors inside an
  HTTP 200, and the check matched a literal `<Error>` while the real responses are namespaced. A
  truncated or empty response did the same. A file could be presented as online that was never
  assembled.
- **DataNodes could finalise a file with a missing chunk**, because its acceptance check looked for
  `"status"` and `"OK"` anywhere in the response — so `{"status":"NOT OK"}` counted as success.
- **The wizard's Account dropdown stopped short of its column's right edge.** Reported on Linux,
  reproduced on Windows: an 8px right inset quietly defeated the stretch it was meant to have.
- **The wizard summary bar quoted size caps in different units from the row above it** — DropMB read
  500 MB in its row and 476.84 MiB in the bar, for the same cap.

## [1.4.5] - 2026-08-22

The upload wizard remembers how you work: filter the hoster list by upload mode, and point the file
pickers at the folder you actually upload from. Plus two fixes for things Linux users were seeing
and Windows users were not. See [docs/release-notes/v1.4.5.md](docs/release-notes/v1.4.5.md) for the
full notes.

### Added

- **Filter the hoster list by upload mode.** The File Hosters step's "Anonymous only" checkbox
  becomes an **Accounts** dropdown — **Both**, **Anonymous only**, or **Account only** — and
  Settings gains the mode the wizard opens on, for anyone who always uploads the same way. "Account
  only" means hosters that *offer* accounts, not merely the ones that refuse anonymous uploads: the
  two overlap, so catbox, gofile, ufile, upload.ee and UpZur appear under either. It ANDs with the
  name box and "No download captcha" as before, and Clear still returns to Both.
- **A "Default upload directory" setting** (Settings → Upload). "Add files…" and "Add folder…" open
  there. Leave it empty — the default — and they reopen wherever you last browsed, remembered across
  restarts; what gets remembered is the folder's *parent*, so the next season or the next release is
  one click away. Underneath, the file picker gained a start directory it never had: it used to open
  at the OS default no matter where you had just been.

### Fixed

- **The "Download captcha?" column header broke mid-word** into "Downloa / d captcha?" on Linux. The
  column's 80px left the header 60px of text budget against a 57px string — three pixels, which held
  in Segoe UI and did not in the wider default UI fonts. It is 100px now, clear by 40%. The wizard
  window also went 850 → 940 so the Account column keeps its width.
- **The wizard no longer advertises drag-and-drop on Linux**, where it cannot work: Avalonia's X11
  backend implements no XDND, so a dropped file never arrives. Promising it made the app look broken
  rather than the feature absent. Windows and macOS are unchanged, and the handler stays wired
  everywhere so the feature returns by itself if Avalonia implements XDND.

## [1.4.4] - 2026-08-22

Hoster size caps now read in the units their hosts state them in: DropMB's cell says **512 MB**, the
figure on its own site, instead of the arithmetically-identical 488.28 MiB. See
[docs/release-notes/v1.4.4.md](docs/release-notes/v1.4.4.md) for the full notes.

### Fixed

- **The "Max file size" column quoted every cap in binary units**, so hosts that advertise a
  decimal-round figure disagreed with their own copy — DropMB's 512,000,000-byte cap rendered as
  "488.28 MiB" against the "512 MB" its site and its `/api/configs` both state, and 1Fichier's 5 GB
  showed as "4.66 GiB". Declared caps now render in whichever base the figure is round in, so
  DropMB reads "512 MB" while DropMeFiles' genuine 53,687,091,200-byte cap stays "50 GiB". The
  change covers the three surfaces that quote a *declared* cap — the wizard column, its oversize
  warning, and the queue-skip log line — and they stay in lockstep, pinned by a test. Measured
  quantities (file sizes, transfer progress, storage figures) keep their explicit bases: they are
  nobody's advertised figure, and letting them pick a base would make their units flap.

## [1.4.3] - 2026-08-22

A **"No download captcha"** filter on the wizard's hoster step: one tick narrows the list to the 32
hosters whose ordinary free-download flow was verified to put no puzzle in a downloader's way. See
[docs/release-notes/v1.4.3.md](docs/release-notes/v1.4.3.md) for the full notes.

### Added

- **"No download captcha" filter** on the wizard's File Hosters step, beside "Anonymous only". It
  keeps only the hosters whose "Download captcha?" verdict is a verified **No** — the 32 that
  shipped with the column in 1.4.2. **Unverified hosters (the column's em dash) are hidden, not
  kept**: the filter's promise is that a downloader meets no puzzle, and Unknown has never meant
  that. The three filters — the name box, "Anonymous only" and this one — AND together, and **Clear
  filter** now resets all three. Filtering stays a view concern, so a hoster ticked and then
  filtered out of sight still uploads, and the "showing N of M" hint is what says so. Localized in
  all six languages.

## [1.4.2] - 2026-08-21

A "Download captcha?" column on the wizard's hoster step: whether a free/anonymous downloader must
solve a captcha to fetch a shared link — 71 of the 76 hosters carry a verdict, each sourced from
the host's own pages, its own shipped code, or a walked live download. See
[docs/release-notes/v1.4.2.md](docs/release-notes/v1.4.2.md) for the full notes.

### Added

- **"Download captcha?" column** on the wizard's File Hosters step. **Yes** (39 hosts) means the
  ordinary free download flow gates the file behind a captcha the downloader must solve; **No**
  (32) means the flow was verified captcha-free — direct-link hosts where a captcha is impossible,
  hosts whose own FAQ says no, and hosts whose live free flow was walked to the bytes or the final
  direct file link; an em dash (5) means the flow couldn't be verified, and the cell's tooltip says
  so. Wait timers and automatic browser checks don't count, per the header's tooltip. One verdict
  per host — the cell ignores the account dropdown, because it reports what the link's *downloader*
  faces. Sorting groups each answer together, unknowns included. The full research register
  (verdict, confidence, check date, evidence per host) lives in `docs/hoster-download-captcha.md`,
  pinned to the pipelines by a build-failing coverage test.

### Changed

- **The hoster grid's columns are user-resizable** (the Use checkbox and scrollbar-gutter strips
  stay fixed), matching the Uploads, Uploaded, Logs and Accounts grids.
- **"Max file size", "Max parallel" and the new captcha column shrank to fit their values** (the
  Account column absorbs the freed width), and slim-column labels now wrap instead of clipping in
  any of the six languages.

## [1.4.1] - 2026-08-13

A "Kept for" column on the wizard's hoster step: how long each host keeps an uploaded file, from
each host's own published policy — 42 of the 77 hosters carry a figure. See
[docs/release-notes/v1.4.1.md](docs/release-notes/v1.4.1.md) for the full notes.

### Added

- **"Kept for" column** on the wizard's File Hosters step. A plain duration counts from upload; a
  starred one ("30 days *") counts from the **last download**, so traffic keeps the file alive;
  **Permanent** appears only where the host states it; an em dash means the host publishes nothing —
  an unknown, not a promise, and its tooltip says so. The figure follows the account dropdown
  (upload.ee: 50 days anonymous, 120 signed in) and sorts sensibly (unknowns grouped, Permanent on
  top). Every value cites the host's own plan table, FAQ, or a measured expiry — from DailyUploads
  deleting a guest file **one day** after its last download to Filedot keeping a registered one
  **1000 days** after its.

### Fixed

- The grid's vertical scrollbar rode over the last column's values (Avalonia draws it over the rows
  area); a gutter column now carries it. "Max parallel" had been losing its right edge to it before
  the new column made the clipping visible.
- The em dash's explaining tooltip was only reachable by hovering the glyph itself, a few pixels
  wide; the whole cell is now the hover target.

## [1.4.0] - 2026-08-12

Twenty-four new file hosters (77 in total, forty-one of them no-login), an upload wizard that builds
its file list from any number of folders, and expired sign-ins caught before a run rather than
during it. See [docs/release-notes/v1.4.0.md](docs/release-notes/v1.4.0.md) for the full notes.

### Added

- **Twenty-four file hosters.** No account needed: **GigaFile** (300 GB, kept 100 days),
  **UploadNow** (100 GiB), **FileMirage** (50 GiB), **Hostize** (20 GB, but free links live 24
  hours), **udrop**, **BowFile** and **MegaUp** (20 GiB each, on the YetiShare platform),
  **UploadHive** (no cap), **World Files** (5 GB, 10 GB signed in), **DataNodes** (3 GiB), **Filego**
  (2 GiB), **Filebin** (no cap, 7-day expiry), **DropMB** (512 MB), **UpZur** (200 MiB), **BtaFile**
  (100 MiB, 10 GiB signed in) and **Xubster** (10 MiB, 500 MiB signed in). With an account:
  **UploadGIG** (re-enabled on the API the host publishes), **DepositFiles** (10 GiB), **Emload**
  (no cap), **SubyShare** (5 GiB), **kshared** (no cap), **FileStore** (250 MiB), **FileCat**
  (2000 MiB) and **PreFiles** (512 MiB).
- **Accounts for UsersDrive**, which double its cap without opening a browser; its anonymous route is
  unchanged.
- **Files from any number of folders** in the upload wizard, as an explorer split — sources as a tree
  on the left, their files on the right — including files and folders dropped onto the window.
- **Filters on the wizard's hoster step**, by name and by whether the host takes anonymous uploads,
  and a **Use-column header checkbox** that ticks every hoster the filter is currently showing.

### Changed

- **"Refresh all" checks up to ten accounts at once** rather than one after another, serialised per
  host so a rate-limited sign-in isn't hit twice at once, and reports accounts that would need a
  browser instead of opening a queue of sign-in windows.
- **An expired sign-in switches its account off** when the list loads, and switching it back on
  re-verifies it — opening the sign-in window where one is needed — instead of trusting the flag.
- **The Add Account dialog verifies credentials before saving them**, so a mistyped password can be
  retried in place; editing an account re-checks it too.
- **Hosts with no sign-in are no longer listed** in the Add Account dialog.
- **Next is disabled on the hoster step until a hoster is ticked.**

### Fixed

- The saved theme is applied before the first window is built, so a dark-theme start no longer
  flashes light.
- **Xubster**'s icon was a seven-pixel sliver; the icon tests now fail an almost-entirely-transparent
  icon.
- Editing an account no longer wipes its stored API key, and a still-valid saved session is re-checked
  without reopening the sign-in window.

### Notes

- Assessed and rejected this cycle, with reasons recorded: pillows.su, UploadHaven, WuShare,
  fastbit.cc, Jumploads, File.AL, Filextras, Filestore.to and CloudGhost. **TakeFile** stays disabled.
- **DropGalaxy** and **ShareMods** remain present in the code but disabled, unchanged from 1.3.0.

## [1.3.0] - 2026-08-06

Seven new file hosters (53 in total, twenty-five of them no-login) and an upload wizard that stops
sending files a host will refuse. See [docs/release-notes/v1.3.0.md](docs/release-notes/v1.3.0.md)
for the full notes.

### Added

- **Seven file hosters.** No account needed: **qu.ax** (permanent storage, 256 MiB, but an extension
  allowlist), **upload.ee** (100 MB, or 200 MB signed in), **temp.sh** (4 GB, 3-day expiry),
  **Litterbox** (1 GB, 72-hour expiry) and **tmpfiles.org** (100 MiB, 48-hour expiry). With an
  account: **EliteFile** (no per-file cap, 488 GB storage) and **Easybytez** (200 MB per file).
- **Pre-upload deselection of file types a hoster refuses**, in the wizard, before Next is pressed —
  covering qu.ax's allowlist and the Uploadrar and filedot.to blocklists, two of which are only
  enforced after the whole file has transferred.

### Changed

- **The wizard says why a hoster will receive nothing** instead of dropping every file into an
  unexplained "won't be uploaded to any hoster" list on the summary page, and blocks Next when
  nothing can upload at all.
- **Accounts switched off in Settings → Accounts are no longer offered** in the wizard's account
  picker.

### Fixed

- **EliteFile** saved "Settings" as the account name on themes whose menu matches the usual marker.
- **Litterbox** links are full length again; one optional field had been omitted, which took a
  6-character link.
- **TeraBytez** and **DataVaults** showed no icon in the hoster list.

### Notes

- Targets `net10.0-windows10.0.17763.0` (Windows 10 1809+).
- Self-contained `win-x64` build is published from the release workflow; first install is a full bundle, subsequent updates are delta patches.

## [1.2.0] - 2026-08-02

Eighteen new file hosters (46 in total, twenty of them no-login), a per-hoster parallel-upload limit
in the wizard, and a run of reliability work aimed at uploads that fail quietly. See
[docs/release-notes/v1.2.0.md](docs/release-notes/v1.2.0.md) for the full notes.

### Added

- **Eighteen file hosters.** No account needed: **1Fichier**, **VikingFile**, **Sendspace**,
  **Webshare**, **DropMeFiles**, **UsersDrive**, **DailyUploads**, **FILEAXA** and **Send.now**. With
  an account: **DDownload**, **Clicknupload**, **Uploady**, **Uploadrar**, **Filedot**, **TeraBytez**,
  **Turbobit**, **Filestank** (the first YetiShare host) and **DataVaults**.
- **"Max parallel" column** on the wizard's hoster step, showing each host's own concurrency limit —
  and the scheduler now honours it (DropMeFiles 5, Send.now 4, DataVaults 4, GigaPeta 1, ufile.io by
  tier).
- **Pre-upload rejection of blocked file types** on hosts that publish a blocklist but only enforce it
  after the transfer (Uploadrar blocks video, filedot.to blocks images).
- **Daily-allowance handling for Filestank**, which stops a batch cleanly when a free account's ~10
  uploads a day are spent.

### Changed

- **Dead upload nodes are remembered for ten minutes** on hosts that hand out a rotating node, so one
  broken node no longer costs every file in a batch.
- **Sign-in is serialised per account** — ten parallel uploads to a new host open one browser window,
  not ten.

### Fixed

- A server that accepts an upload and stores nothing (`file_status: OK` with `file_code: undef`) is
  reported as a failure instead of producing a link to nothing.
- A momentary Cloudflare 5xx during the upload-server lookup is retried, and an error page is no
  longer mistaken for an expired session — which used to discard a valid sign-in.
- `Content-Disposition` now precedes `Content-Type` on multipart file parts, matching browsers; one
  host silently accepted whole uploads and then reported "no file found" without it.
- Accounts added from the upload wizard appear in Settings → Accounts.
- Account names are read correctly on themes that don't publish one the usual way (some saved blank,
  others picked up the word "Profile").
- A sign-in window that stayed open after a successful login on query-routed login pages now closes.
- A package's **Elapsed** is a wall-clock span rather than the sum of its files' times.
- Binding errors from the Logs/Uploaded tooltips and the Settings Connection panel are gone.
- Log entries render each HTTP header as the single line that goes on the wire.
- Wizard grid headers use the app's header chrome; italic hoster names are no longer clipped.

### Notes

- **DropGalaxy** and **ShareMods** ship disabled — DropGalaxy caps anonymous uploads at ~10 bytes with
  signups closed, and ShareMods began serving this client a Cloudflare challenge before release.

## [1.1.0] - 2026-07-26

Linux support via a portable AppImage, large-file uploads to Storage.to, a responsiveness overhaul for
large queues, package renaming, and paste-ready link export. See
[docs/release-notes/v1.1.0.md](docs/release-notes/v1.1.0.md) for the full notes.

### Added

- **Portable Linux AppImage**, including the in-app captcha/sign-in browser via bundled Chromium.
- **Copy Links** — export a completed package's links as plain text, BBCode or Markdown, grouped by
  file or by hoster.
- **Rename packages** inline (F2 or the context menu), **Remove All Completed**, and **Elapsed** /
  **Finish at** in the Upload Overview.

### Changed

- **Storage.to** uploads large files through Cloudflare R2 multipart, removing the previous size
  ceiling.
- Responsiveness overhaul for queues of 500+ files.

## [1.0.0] - 2026-07-19

**Rebuilt on Avalonia UI.** The whole interface was reimplemented off WPF onto Avalonia on .NET 10,
keeping the same layout, features and workflow; existing installs update in place with settings,
accounts and history intact. See [docs/release-notes/v1.0.0.md](docs/release-notes/v1.0.0.md).

## [0.0.6] - 2026-07-05

Eleven new file hosters (including three end-to-end-encrypted, no-login services), byte-weighted package progress and ETA, a per-hoster max-file-size column in the wizard, and a SQLite security fix. See [docs/release-notes/v0.0.6.md](docs/release-notes/v0.0.6.md) for the full notes.

### Added

- **Eleven new file hosters**: **transfer.it**, **mega.nz**, and **wormhole.app** (all end-to-end-encrypted); **Storage.to**, **catbox.moe**, **gofile.io**, and **ufile.io** (anonymous, several also logged-in); and **MediaFire**, **Pixeldrain**, **File Garden**, and **Filehoster.io** (account uploads).
- **ufile.io tiered accounts** (Free / Pro / Business) with per-tier max file size, simultaneous-upload limits, and storage reporting — backed by a new per-pipeline concurrent-upload cap and two new `AccountType` values.
- **"Max file size" column** on the wizard's hoster step.
- **Instant-link de-duplication** for MediaFire (identical content links without re-uploading) and a pre-upload existence check for File Garden.

### Changed

- **Package progress and ETA are byte-weighted** across the whole package instead of averaging per-file percentages, so a part-way-through large file reports true completion and a whole-package time-remaining.
- Accounts grid shows **"-"** when a hoster reports no Used figure and **"Unlimited"** for capless hosts (e.g. catbox); the wizard Summary names the account's free space in an amber auto-fit notice.
- The Uploads grid shows a **horizontal scrollbar** on column overflow rather than clipping.

### Security

- Cleared **CVE-2025-6965** (high-severity SQLite memory corruption, `NU1903`) by updating the bundled SQLite native library past the fix (SQLite 3.50.4).

## [0.0.5] - 2026-06-29

Five new file hosters, Rapidgator storage reporting, a non-ASCII filename fix, and a dead-hoster cleanup. See [docs/release-notes/v0.0.5.md](docs/release-notes/v0.0.5.md) for the full notes.

### Added

- **Five new file hosters**: **Isracloud** (XFileSharing web-form, session sign-in), **Keep2Share** and **TezFiles** (shared "moneyplatform" backend), **NitroFlare** (reCAPTCHA sign-in, account hash), and **Upstore** (anonymous *and* logged-in).
- **Rapidgator storage reporting** (used / available) in the Accounts grid and the upload wizard.

### Fixed

- **Non-ASCII upload filenames** (Japanese, etc.) no longer arrive on the server as `?????` — multipart filenames are sent as raw UTF-8, the way a browser does.
- **Upload order is respected** when the first file needs hashing before upload — it no longer leapfrogs a queued #1 to start #2.
- **Removing a package's only file** now removes the empty package row too.
- **Uploads / History context menus** apply every action to all selected rows, not just the focused one.

### Removed

- **Disabled** (retained in-tree): **TakeFile** (Cloudflare managed-challenge TLS fingerprinting) and **UploadGIG** (no working upload capture).
- **Removed** dead / premium-only / unusable hosts: Novafile, Openload, Rapidu, RareFile, ShareOnline, UbiqFile, UniBytes, Uploaded, and WuShare.

## [0.0.4] - 2026-06-26

A new IcerBox hoster, HitFile account uploads, a wizard that fits your selection to each account's live free space, per-file upload order, and a bounded upload-retry layer. See [docs/release-notes/v0.0.4.md](docs/release-notes/v0.0.4.md) for the full notes.

### Added

- **IcerBox** file hoster (custom Bearer-JWT REST API, email / password) with used / available storage reporting.
- **HitFile registered-account uploads** (previously anonymous-only), with cookie-based storage refresh.
- **Per-hoster capacity fit on the wizard Summary page**: each selected account's free space is re-checked live (no sign-in window), files that don't fit are unchecked largest-first, over-capacity blocks **Next**, and a per-hoster "N unchecked to fit" clue plus a grand-total footer explain the result.
- **Numeric per-file upload Order** with Move up / down / to and renumber, editable in the Uploads grid.
- **"Force start"** to launch a queued upload over the concurrency limit, re-hashing and re-uploading an already-completed file on confirmation.
- Sign-in failures shown as a compact `Error: …` line with a **Details** dialog carrying the full server response.
- Account / Added-at / Started-at columns across the grids; Method / URL / Proxy columns and show-hide-reorder for the Logs HTTP grid.

### Changed

- A shared, bounded **upload-retry layer** retries only faults that provably never created a file (mid-send body aborts, connect-phase failures) and never retries a chunked upload mid-stream; Alfafile / Rapidgator auto-retry "state 3" finalize failures and re-authenticate on a mid-upload 401.
- Replaced the per-package **priority** field with the per-file upload Order.
- Column renames: Uploads "Duration" → "Elapsed", "Save to" → "Path".

### Removed

- **Hotlink** disabled — free accounts cannot upload and the API key is unobtainable. Retained in-tree pending a usable path.

## [0.0.3] - 2026-06-14

Ten new file hosters, anonymous uploads, a chunked upload protocol, and account storage reporting. See [docs/release-notes/v0.0.3.md](docs/release-notes/v0.0.3.md) for the full notes.

### Added

- **Ten new file hosters** alongside Rapidgator: Alfafile, BRupload, Ex-Load, FileBoom, KatFile, TakeFile, Hexload, Hxfile, Hotlink, and GigaPeta — each plugging into the `IFileHosterPipeline` architecture with its own verification and upload flow.
- **Anonymous (no-login) uploads** for GigaPeta and Hexload, offered as an "Anonymous" entry in the wizard's account picker and persisted across restarts.
- **Chunked XFileSharing upload protocol** for modern CDN-backed hosters (per-chunk upload + finalize), with a classic single-multipart fallback.
- **WebView2 captcha sign-in** for hosters whose login is captcha-gated, plus API-key bootstrap for the XFileSharing family.
- **Account storage (used / available)** in the Account Manager, with an explicit "Unlimited" state and binary IEC units.
- Hoster icons in the Account Manager grid and wizard; per-request header capture in the HTTP transcript.
- Package priority as a real five-level scheduling primitive; per-hoster max-file-size / max-files validation in the wizard.

### Changed

- Multipart uploads reshaped to match a real browser (field order, quoted `name=`, `Origin` / `Sec-Fetch-*` headers, User-Agent).
- Uploads and account checks refuse to run when "Use Proxies" is on but no proxy is available.
- Connection Manager rework (Edit Proxy dialog), reliable dark title bar across Windows 10 / 11, alphabetical hoster lists, and Settings polish.

### Removed

- **FilesMonster** and **Filecloud** (no usable free upload path). **FlashBit** and **ExtMatrix** are retained in-tree but disabled pending upstream fixes.

## [0.0.2] - 2026-05-13

Upload reliability against real Rapidgator behaviour, schedule-aware starting, and a redesigned wizard. See [docs/release-notes/v0.0.2.md](docs/release-notes/v0.0.2.md) for the full notes.

### Added

- New **Upload files** wizard mode for picking individual files, with mode / source / file list consolidated into a single step.
- **Scheduled at** column and a schedule-aware **Start all uploads** that skips files scheduled for the future.
- Live **hash speed / progress** during the hashing phase.

### Changed

- Rapidgator: poll `upload_info` until Done / Fail (3-minute budget, exponential backoff); per-credentials login gate so parallel uploads share one login round-trip.
- Package status no longer flips to **Failed** while siblings are still uploading — a mixed result reports **Done with errors**; persisted `StartedDate` / `FinishedDate` / `Duration` survive a restart.

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

[1.4.5]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.5
[1.4.4]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.4
[1.4.3]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.3
[1.4.2]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.2
[1.4.1]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.1
[1.4.0]: https://github.com/CSUploader/CSUploader/releases/tag/v1.4.0
[1.3.0]: https://github.com/CSUploader/CSUploader/releases/tag/v1.3.0
[1.2.0]: https://github.com/CSUploader/CSUploader/releases/tag/v1.2.0
[1.1.0]: https://github.com/CSUploader/CSUploader/releases/tag/v1.1.0
[1.0.0]: https://github.com/CSUploader/CSUploader/releases/tag/v1.0.0
[0.0.6]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.6
[0.0.5]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.5
[0.0.4]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.4
[0.0.3]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.3
[0.0.2]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.2
[0.0.1]: https://github.com/CSUploader/CSUploader/releases/tag/v0.0.1
