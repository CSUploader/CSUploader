# Avalonia Migration Phase 3: Primitives — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land every UI primitive the view ports (Phases 4–6) will consume: the WPF reference-capture hook + contact-sheet pipeline, the fake-data seed script, image/geometry resources with WPF key parity, the SVG file-type icon pipeline, theme tokens + ThemeVariant dictionaries + a real `AvaloniaThemeApplier`, the 20 ported value converters, the Avalonia `LocExtension`, and the two DataGrid behaviors — each verified by headless tests and, where visual, by the phase's first contact sheet built from a dev-flag gallery window.

**Architecture:** Strangler step 3 from the design doc (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, §The Avalonia head — theming/SVG/images/localization/behaviors — and §Phases "Phase 3", whose ten PREP ITEMS this plan task-izes; the mapping is listed under §Prep-item coverage below). Nothing in this phase ports a real view; Phase 3 delivers the *vocabulary* the views will be written in, plus the two tools (reference shots, fake data) every later visual comparison depends on.

**Tech Stack:** .NET 10, Avalonia **11.3.18** + Avalonia.Controls.DataGrid **11.3.13** + Avalonia.Themes.Fluent, **Avalonia.Svg.Skia 11.3.0** (resolved at plan time — see Task 4), Avalonia.Headless.XUnit 11.3.18 (+ Avalonia.Markup.Xaml.Loader 11.3.18 for runtime-XAML tests), CommunityToolkit.Mvvm 8.4.2 (Core), Python 3 (contact-sheet script, same interpreter as `scripts/md-to-resx.py`), AvaDevBridge via the committed `.mcp.json` / `scripts/ava-drive.cs`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase2-shell-and-spike-ready`. Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree, has uncommitted Buzzheavier work).
- **Suite gate after every task** (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — **1178 green at phase start**; the count only goes up, never down.
  - `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — **5 green at phase start**; most Phase 3 tasks raise this count (record each new baseline and carry it forward).
  - The two test projects use **separate OutDirs** (shared OutDir mixes WPF and Avalonia assemblies and breaks discovery). Never run bare solution-level `dotnet test -p:OutDir=…`.
- Head builds: Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`; WPF `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf`. Scratch DBs live beside those exes by construction.
- **Version pins are hard**: Avalonia 11.3.18 (AvaDevBridge's pin), DataGrid 11.3.13. New this phase: Avalonia.Svg.Skia **11.3.0** (the only 11.3-line release; its Avalonia floor `>= 11.3.0` accepts 11.3.18). Do not "helpfully" bump any of them.
- Every new csproj change keeps: `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`.
- **The WPF head is touched by exactly one task** (Task 1's DEBUG-only capture hook, sanctioned by design prep item 1). Without `--shots` the WPF app behaves byte-for-byte identically; the Release build must not contain the hook (`#if DEBUG`). The full existing suite is the regression net.
- **avares convention** (design prep item 10, decided at the Phase 2 gate): XAML uses root-relative URIs (`/Assets/...`, `/FileTypes/...`, `/Resources/...` — rename-proof, resolve against the containing assembly); code-side `AssetLoader`/`SvgSource` builds `avares://{typeof(App).Assembly.GetName().Name}/...` so the Phase 9 `AssemblyName` change to `CSUploader` costs nothing.
- **i18n**: no new keys this phase (the gallery window is a dev tool — hardcoded English with a tracking comment is correct there). Never hand-edit `Strings*.resx`; the `I18nRegenGateTests` gate runs inside the main suite.
- **Agent-safety** (design §The Avalonia head): the seed script targets SCRATCH dirs only and writes only settled file states — **Paused/Failed/Completed/Cancelled, NOT Idle and never Uploading/\*Queued**. A persisted Idle row is NOT settled: the load path counts it as running-at-shutdown (`PackageManager.cs:287-291`) and remaps it to HashQueued/UploadQueued (`:347-349`), so under the default `OnlyIfRunningAtLastSession` policy it would auto-start a real upload on the guard-less WPF head. Avalonia launches for bridge work always pass `--agent`; never copy a real `CSUploader.db` into any scratch dir; the bridge and `ava-drive` exclude each other (single-driver lock).
- **ava-drive gotchas** (Phase 2 experience): the action tool's argument is **`verb`, not `action`** (check `ava_action`'s schema via the bridge README / a bare call if in doubt); search-style tools return a **bare JSON array**, not an envelope object; handshake discovery picks the **newest** live handshake — if a second bridge-enabled app is running (e.g. a forgotten spike window session), close it or you will drive the wrong process.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited file:line and copy the semantics exactly. Where this plan could not pin an Avalonia API shape against the installed packages, the step says so and the §Reality-check register lists it — resolve at implementation time, don't guess.

### Prep-item coverage (the 10 items from the Phase 2 gate, design §Phases "Phase 3")

| # | Prep item | Where |
|---|-----------|-------|
| 1 | WPF reference-capture hook FIRST | Task 1 |
| 2 | Contact-sheet generator + shots naming convention | Task 1 |
| 3 | Avalonia-head SVG glob + empty-submodule Error guard | Task 4 |
| 4 | FluentTheme density decision BEFORE shared styles | Task 5 Step 1 |
| 5 | RequestedThemeVariant ownership → AvaloniaThemeApplier | Task 5 Step 4 |
| 6 | Resolve Avalonia.Svg.Skia exact version at plan time | **Resolved: 11.3.0** (Task 4 header) |
| 7 | Headless test infra gets FluentTheme styles | Task 3 Step 4 (TestAppBuilder → real `App`) |
| 8 | LocExtension verification ×2 (live culture switch; CLR vs styled) | Task 7 Steps 3–4 |
| 9 | Fake-data seed script | Task 2 |
| 10 | avares convention | Global Constraints + Tasks 3/4 |

---

### Task 1: WPF reference-capture hook + shots convention + contact-sheet generator

Every screenshot comparison in Phases 3–6 depends on this; it was never built in Phases 0–2. Design shape (§MCP dev loop): DEBUG-only, in-app, **RenderTargetBitmap → PNG** (NOT PrintWindow — black frames + chrome + physical pixels), startup arg, pinned logical window size, light + dark. Both sides thus produce 96-DPI render-tree client-area shots.

**Files:**
- Create: `src/Services/ReferenceShotCapture.cs` (WPF head), `scripts/contact-sheet.py`
- Modify: `src/App.xaml.cs` (`OnStartup`, after `mainWindow.Show()` at :46)

**Interfaces:**
- Produces: the **shots convention** every later phase writes to — `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png` (view names lowercase, no spaces; this phase writes `mainwindow-uploads`, `mainwindow-uploaded`, `mainwindow-settings`, `mainwindow-logs` from WPF and `shell`/`gallery` from Avalonia in Task 9). Also `ReferenceShotCapture.CaptureWindow(Window, string)` — the reusable single-window primitive Phases 4–6 point at dialogs.
- Consumes: `IThemeApplier`/`MainViewModel` from the WPF head's provider (`src/App.xaml.cs:20` exposes `Services`); `WpfThemeApplier.ApplyTheme` (`src/Services/WpfThemeApplier.cs:42`).

- [ ] **Step 1: Write `ReferenceShotCapture`** (whole file `#if DEBUG` … `#endif` wrapped):

```csharp
// <copyright file="ReferenceShotCapture.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Services;

/// <summary>
/// DEBUG-only reference-shot capture (design §MCP dev loop): renders the main window's
/// client area per tab, light + dark, as 96-DPI render-tree PNGs under the shots
/// convention (&lt;view&gt;-&lt;light|dark&gt;-wpf.png), then shuts the app down.
/// RenderTargetBitmap, deliberately NOT PrintWindow — PrintWindow returns black frames
/// without PW_RENDERFULLCONTENT and captures chrome + physical pixels.
/// </summary>
public sealed class ReferenceShotCapture(IServiceProvider services)
{
    private static readonly string[] TabNames = ["uploads", "uploaded", "settings", "logs"];

    public async Task RunAndShutdownAsync(Window window, string dir)
    {
        Directory.CreateDirectory(dir);

        // Pin the logical size (screenshot normalization, design §MCP dev loop) — matches
        // the Avalonia shell's 1024x800 so paired shots line up.
        window.Width = 1024;
        window.Height = 800;

        // MainWindow_Loaded runs MainViewModel.InitializeAsync fire-and-forget; there is no
        // completion signal on the VM (verify at implementation — if one exists, await it
        // instead). Settle-delay is acceptable for a dev capture tool; bump it if a seeded
        // grid ever captures half-hydrated.
        await Task.Delay(2500);

        IThemeApplier theme = services.GetRequiredService<IThemeApplier>();
        MainViewModel vm = services.GetRequiredService<MainViewModel>();

        foreach (bool dark in (bool[])[false, true])
        {
            theme.ApplyTheme(dark);
            for (int i = 0; i < TabNames.Length; i++)
            {
                vm.SelectedTabIndex = i;
                await WaitForRenderAsync(window);
                CaptureWindow(window, Path.Combine(dir, $"mainwindow-{TabNames[i]}-{(dark ? "dark" : "light")}-wpf.png"));
            }
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Captures one window's client area to a PNG. Public and static on purpose: Phases 4-6
    /// reuse it for dialog reference shots (open the dialog, call this, close).
    /// </summary>
    public static void CaptureWindow(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        int w = (int)Math.Ceiling(root.ActualWidth);
        int h = (int)Math.Ceiling(root.ActualHeight);

        // Draw the window background first: rendering only the content visual misses the
        // Window's SurfaceBrush fill (set by the implicit Window style, Tokens.xaml:773-777).
        DrawingVisual dv = new();
        using (DrawingContext ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }

        RenderTargetBitmap rtb = new(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    private static async Task WaitForRenderAsync(Window window)
    {
        // Two settle passes: tab-content template realization at ContextIdle + a render tick.
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(150);
    }
}
#endif
```

- [ ] **Step 2: Trigger from `App.OnStartup`** — insert after `mainWindow.Show();` (`src/App.xaml.cs:46`):

```csharp
#if DEBUG
        // DEBUG-only reference-shot capture: --shots [dir]. Fire-and-forget by design —
        // the capture task ends with Application.Shutdown().
        int shotsIdx = Array.IndexOf(e.Args, "--shots");
        if (shotsIdx >= 0)
        {
            string shotsDir = shotsIdx + 1 < e.Args.Length && !e.Args[shotsIdx + 1].StartsWith('-')
                ? e.Args[shotsIdx + 1]
                : @"D:\temp2\cbuild-mig\shots";
            _ = new Services.ReferenceShotCapture(_serviceProvider!).RunAndShutdownAsync(mainWindow, shotsDir);
        }
#endif
```

- [ ] **Step 3: Write `scripts/contact-sheet.py`** (stdlib only, same interpreter as `md-to-resx.py`):

```python
#!/usr/bin/env python3
"""Builds contact-sheet.html from the shots directory, pairing <view>-<theme>-wpf.png
with <view>-<theme>-ava.png side by side (design: screenshot review batched per phase
via a persisted HTML contact sheet). Missing counterparts render as a 'missing' cell —
expected while a view exists on only one side.

Usage: python scripts/contact-sheet.py [shots-dir]   (default D:/temp2/cbuild-mig/shots)
"""
import html
import re
import sys
from pathlib import Path


def main() -> None:
    shots = Path(sys.argv[1] if len(sys.argv) > 1 else r"D:/temp2/cbuild-mig/shots")
    pattern = re.compile(r"^(?P<view>.+)-(?P<theme>light|dark)-(?P<side>wpf|ava)\.png$")
    cells: dict[tuple[str, str, str], str] = {}
    for png in sorted(shots.glob("*.png")):
        m = pattern.match(png.name)
        if m:
            cells[(m["view"], m["theme"], m["side"])] = png.name

    pairs = sorted({(view, theme) for (view, theme, _) in cells})
    rows = []
    for view, theme in pairs:
        def img(side: str) -> str:
            name = cells.get((view, theme, side))
            if name is None:
                return '<td class="missing">missing</td>'
            return f'<td><img src="{html.escape(name)}" loading="lazy"></td>'
        rows.append(f"<tr><th>{html.escape(view)} — {theme}</th>{img('wpf')}{img('ava')}</tr>")

    out = shots / "contact-sheet.html"
    out.write_text(
        '<!doctype html><meta charset="utf-8"><title>CSUploader migration contact sheet</title>\n'
        "<style>body{font:13px sans-serif;background:#222;color:#eee}"
        "table{border-collapse:collapse}"
        "td,th{border:1px solid #555;padding:4px;vertical-align:top;text-align:left}"
        "img{max-width:900px;display:block}.missing{color:#f88}"
        "thead th{position:sticky;top:0;background:#333}</style>\n"
        "<table><thead><tr><th>view — theme</th><th>WPF (reference)</th><th>Avalonia</th></tr></thead>\n"
        + "".join(rows) + "</table>\n",
        encoding="utf-8",
    )
    print(f"wrote {out} ({len(pairs)} view/theme pairs)")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Prove the loop.** Build + run the WPF head from scratch, capture, sheet:

```powershell
dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf
D:\temp2\cbuild-mig\wpf\CSUploader.exe --shots            # exits by itself after ~15 s
python scripts/contact-sheet.py
```

Expected: 8 PNGs (`mainwindow-{uploads,uploaded,settings,logs}-{light,dark}-wpf.png`) in `D:\temp2\cbuild-mig\shots\`; open two with Read (one light, one dark) — full client area, correct theme, no black frames; `contact-sheet.html` written with 8 single-sided rows. Grids are empty (seed arrives in Task 2 — Task 2 re-captures populated ones).

- [ ] **Step 5: Release-build guard.** `dotnet build src/CSUploader.csproj -c Release -p:OutDir=D:\temp2\cbuild-mig\wpf-rel` succeeds; `D:\temp2\cbuild-mig\wpf-rel\CSUploader.exe --shots` must NOT capture (flag is dead in Release) — launch, confirm the window just stays open, close it.
- [ ] **Step 6:** Full suite gate (both projects). **Commit** — `"dev(wpf): DEBUG-only reference-shot capture hook (--shots) + contact-sheet generator"`

---

### Task 2: Fake-data seed script

Design §agent-safety operating rules: grid data comes from a fake-data seed script (bogus credentials only). Phases 5–6 grid work needs populated grids; Task 1's reference shots need them too. **Seam decision (made here, after reading the DAL):** a standalone .NET 10 file-based script writing DBM rows through the real `CSUploaderDbContext` (`#:project` reference, the `scripts/ava-drive.cs` precedent) — NOT a `--seed-fake-data` startup arg. Rationale: `EnsureCreated()` gives the script the exact current schema with zero SQL drift, it works for BOTH heads' scratch dirs (the WPF head has no `--agent` switch to hang seeding off), and it keeps seed logic out of the shipping app entirely.

**Files:**
- Create: `scripts/seed-fake-data.cs`

**Interfaces:**
- Consumes: `CSUploaderDbContext` (`src/CSUploader.Core/Dal/CSUploaderDbContext.cs:20` options ctor; `EnsureCreated` per `FirstRun.cs:19`), `FileHosterLoginDbm`, `UploadPackageDbm`, `UploadPackageFileDbm`, `FileState` (`src/CSUploader.Core/Upload/FileState.cs`).
- Produces: a seeded scratch DB + real small files under `<outdir>\FakeData\`; idempotent (marker = any login with username prefix `fake_`).

- [ ] **Step 1: Read the two repository mappers first** — `src/CSUploader.Core/Dal/UploadPackageRepository.cs` and `FileHosterLoginRepository.cs` — and note every DBM field the load path round-trips (the FileHosterLogin family has a history of silently-dropped fields; mirror what the mapper reads, not what the DBM merely declares). Also confirm the two hoster names against the registered pipelines (`grep -n "\"Rapidgator\"\|\"Catbox\"" src/CSUploader.Core/Upload` — names key both `HosterIconConverter`'s resource lookup and load-time hoster resolution).
- [ ] **Step 2: Write the script:**

```csharp
#!/usr/bin/env dotnet
// Seeds a SCRATCH CSUploader.db with bogus accounts + packages so agent-driven sessions
// (bridge screenshots, reference shots, Phases 3-6 grid work) have populated grids.
// NEVER run against a real profile DB — the target is always a per-bin scratch dir.
// Usage: dotnet run scripts/seed-fake-data.cs -- [outdir]   (default D:\temp2\cbuild-mig\ava)
//
// Safety invariants (design §The Avalonia head, agent-safety):
//   - credentials are bogus (fake_* / not-a-real-password); no anonymous rows;
//   - file states are ONLY Paused/Failed/Completed/Cancelled — NOT Idle, never
//     Uploading/*Queued. Idle is NOT settled: the load path counts a persisted Idle as
//     running-at-shutdown (PackageManager.cs:287-291) and remaps it to a queued state
//     (:347-349), so under the default OnlyIfRunningAtLastSession policy an Idle row
//     would AUTO-START a real upload on the guard-less WPF head (used for reference shots);
//   - ScheduledStartTime stays null (nothing wakes on a timer).
#:project ../src/CSUploader.Core/CSUploader.Core.csproj

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

string dir = args.FirstOrDefault() ?? @"D:\temp2\cbuild-mig\ava";
string dbPath = Path.Combine(dir, "CSUploader.db");
string dataDir = Path.Combine(dir, "FakeData");
Directory.CreateDirectory(dataDir);

// Real (small) files on disk. The loader itself does NO disk-existence check
// (PackageManager.cs:309-312 — missing files only surface as Failed when a run starts)
// and restores the persisted FileSize for gone files (:325-328), so fake paths WOULD
// load — but real bytes keep FileInfo-derived sizes truthful in the grid and avoid
// error-path noise if a row is ever started manually.
string MakeFile(string name, int mib)
{
    string path = Path.Combine(dataDir, name);
    if (!File.Exists(path))
    {
        File.WriteAllBytes(path, new byte[mib * 1024 * 1024]);
    }

    return path;
}

DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .Options;
using CSUploaderDbContext ctx = new(options);
ctx.Database.EnsureCreated();

if (ctx.FileHosterLogins.Any(l => l.Username.StartsWith("fake_")))
{
    Console.WriteLine($"{dbPath} is already seeded — nothing to do.");
    return 0;
}

// Hoster names MUST match registered pipelines exactly (icon lookup + load-time hoster
// resolution both key on the name) — verified in plan Task 2 Step 1.
FileHosterLoginDbm rapidgator = new()
{
    FileHosterName = "Rapidgator",
    Username = "fake_rg_user",
    Password = "not-a-real-password",
    StorageUsedBytes = 1L * 1024 * 1024 * 1024,
    StorageQuotaBytes = 10L * 1024 * 1024 * 1024,
    LastRefreshedDateTime = DateTime.Now.AddHours(-3),
    CreatedDateTime = DateTime.Now.AddDays(-12),
};
FileHosterLoginDbm catbox = new()
{
    FileHosterName = "Catbox",
    Username = "fake_catbox_user",
    Password = "not-a-real-password",
    CreatedDateTime = DateTime.Now.AddDays(-5),
};
ctx.FileHosterLogins.AddRange(rapidgator, catbox);
ctx.SaveChanges(); // ids assigned below

UploadPackageFileDbm File1(string name, int mib, FileState state, int login, string hoster, int order, string? error = null, string? url = null)
{
    string path = MakeFile(name, mib);
    return new UploadPackageFileDbm
    {
        FileName = name,
        FileDirectory = Path.GetDirectoryName(path)!,
        FileSize = new FileInfo(path).Length,
        FileHoster = hoster,
        FileHosterName = hoster,
        FileHosterAccount = login == rapidgator.Id ? rapidgator.Username : catbox.Username,
        FileHosterLoginId = login,
        State = (int)state,
        Error = error,
        FileUrl = url ?? string.Empty,
        SortOrder = order,
        QueueOrder = order,
        StartDateTime = state == FileState.Completed ? DateTime.Now.AddHours(-2) : default,
        FinishedDateTime = state == FileState.Completed ? DateTime.Now.AddHours(-1) : default,
        IsHashingComplete = state == FileState.Completed,
    };
}

UploadPackageDbm paused = new()
{
    Name = "Fake pack (paused)",
    CreatedDateTime = DateTime.Now.AddHours(-6),
    Files =
    [
        File1("fake_movie.mkv", 5, FileState.Paused, rapidgator.Id, "Rapidgator", 1),
        File1("fake_notes.txt", 1, FileState.Paused, rapidgator.Id, "Rapidgator", 2),
        File1("fake_archive.zip", 3, FileState.Failed, catbox.Id, "Catbox", 3,
            error: "HTTP 500\nserver said: quota exceeded"), // multi-line on purpose: SingleLineConverter's case
    ],
};
UploadPackageDbm done = new()
{
    Name = "Fake pack (completed)",
    CreatedDateTime = DateTime.Now.AddDays(-1),
    IsCompleted = true,
    Files =
    [
        File1("fake_song.mp3", 2, FileState.Completed, rapidgator.Id, "Rapidgator", 1,
            url: "https://rapidgator.net/file/fake000001"),
        File1("fake_photo.jpg", 1, FileState.Completed, catbox.Id, "Catbox", 2,
            url: "https://files.catbox.moe/fake01.jpg"),
    ],
};
ctx.UploadPackages.AddRange(paused, done);
ctx.SaveChanges();

Console.WriteLine($"Seeded {dbPath}: 2 logins, 2 packages, 5 files (states: Paused/Failed/Completed).");
return 0;
```

Adjust field fills to whatever Step 1's mapper read revealed (e.g. if the mapper derives `FileHosterAccount` differently) — the mapper is the contract, this listing is the starting point.

- [ ] **Step 3: Verify against BOTH heads.**

```powershell
dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\wpf
D:\temp2\cbuild-mig\wpf\CSUploader.exe   # manual look: Uploads tab shows the paused pack (3 rows expanded), Uploaded tab the 2 completed files, Settings→Accounts the 2 fake accounts with storage cells; NOTHING starts uploading. Close.
dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\wpf   # idempotency: prints "already seeded"
D:\temp2\cbuild-mig\wpf\CSUploader.exe --shots                    # re-capture: populated reference shots overwrite Task 1's empty ones
dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\ava   # Avalonia scratch too (grids are Phase 5/6, but the DB is ready)
```

Confirm in the new `mainwindow-uploads-light-wpf.png` (Read) that grid rows are visible.

- [ ] **Step 4:** Full suite gate. **Commit** — `"dev: fake-data seed script (bogus accounts/packages, settled states only, scratch DBs only)"`

---

### Task 3: Image assets + ImageResources with WPF key parity

Design: "pack:// → avares://; ImageResources.xaml (~33 logos, 8 geometries) converted mechanically." The **keys are load-bearing** — `HosterIconConverter` computes `FileHoster<Name>Image` strings at runtime, `FileStateIconConverter`/`ProxyTestOutcomeIconConverter`/`ResourceKeyToImageConverter` look keys up by name — so parity is enforced by test, not by eyeball. Mechanically: geometries port as a XAML `StreamGeometry` dictionary (Avalonia's idiomatic icon pattern); bitmaps CANNOT be declared as XAML resource entries in Avalonia (no `BitmapImage` element / no type-converting object element), so they merge from a code-built table — one line per WPF entry, same keys.

**Files:**
- Create: `src/CSUploader.Avalonia/Resources/ImageGeometries.axaml`, `src/CSUploader.Avalonia/Resources/BitmapImageResources.cs`
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` (asset glob), `src/CSUploader.Avalonia/App.axaml` (merge geometries), `src/CSUploader.Avalonia/App.axaml.cs` (merge bitmaps in `Initialize`), `tests/CSUploader.Avalonia.Tests/TestAppBuilder.cs` (→ real `App`)
- Test: `tests/CSUploader.Avalonia.Tests/Resources/ImageResourceTests.cs`

**Interfaces:**
- Produces: every key from `src/Resources/ImageResources.xaml` resolvable via `Application.Current.TryFindResource` in the Avalonia head — **69 bitmap keys** (`:8-88`: 6 Action + 3 Button + 1 GoDown + 2 Package + 9 Status + 1 Account + 32 FileHoster + 15 Logo) and **8 geometry keys** (`:96-119`: 7 `Settings*Geometry` + `ForceStartGeometry`). `internal static BitmapImageResources.Entries` (the `(string Key, string Path)[]` table) and `MergeInto(IResourceDictionary)`.
- Consumes: PNG/ICO sources under `src/Properties/Images/**` (single source of truth — the WPF head's folder, linked, not copied).

- [ ] **Step 1: Asset glob.** In `CSUploader.Avalonia.csproj`, next to the existing icon link:

```xml
<ItemGroup>
  <!-- The WPF head's image tree is the single source of truth; linked, not copied.
       avares URIs: /Assets/Images/<subpath> (root-relative in XAML; full avares:// with
       the assembly name code-side — the Phase 2 gate convention). -->
  <AvaloniaResource Include="..\Properties\Images\**\*" Link="Assets\Images\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

Keep the existing `Assets\icon.ico` link (`MainWindow` references it) — the duplicate embedding of one small .ico is deliberate, don't churn the shipped reference.

- [ ] **Step 2: Geometries.** `ImageGeometries.axaml` — copy the 8 path-data strings **verbatim** from `src/Resources/ImageResources.xaml:96-119` (same keys, same data, keep the per-icon comments):

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Settings General-tab section icons + ForceStart (Material Design Icons, Apache 2.0).
       Keys and path data copied 1:1 from the WPF ImageResources.xaml — keys are load-bearing. -->
  <StreamGeometry x:Key="SettingsLanguageGeometry">M12.87,15.07L10.33,12.56L10.36,12.53C12.1,10.59 13.34,8.36 14.07,6H17V4H10V2H8V4H1V6H12.17C11.5,7.92 10.44,9.75 9,11.35C8.07,10.32 7.3,9.19 6.69,8H4.69C5.42,9.63 6.42,11.17 7.67,12.56L2.58,17.58L4,19L9,14L12.11,17.11L12.87,15.07M18.5,10H16.5L12,22H14L15.12,19H19.87L21,22H23L18.5,10M15.88,17L17.5,12.67L19.12,17H15.88Z</StreamGeometry>
  <!-- …the remaining 7, copied the same way: SettingsDeveloperGeometry, SettingsGridAppearanceGeometry,
       SettingsWindowBehaviourGeometry, SettingsConfirmationGeometry, SettingsNotificationsGeometry,
       SettingsDatabaseGeometry, ForceStartGeometry… -->
</ResourceDictionary>
```

- [ ] **Step 3: Bitmap table.** `BitmapImageResources.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CSUploader.Resources;

/// <summary>
/// Code-built twin of the WPF ImageResources.xaml bitmap entries (Avalonia has no XAML
/// element for a keyed bitmap resource). Keys are load-bearing: HosterIconConverter
/// computes "FileHoster&lt;Name&gt;Image" at runtime and the status/action converters look
/// these up by name — copy keys 1:1, including the dotted ones (FileHosterStorage.toImage,
/// FileHosterTransfer.itImage, FileHosterFilehoster.ioImage).
/// </summary>
internal static class BitmapImageResources
{
    /// <summary>(key, path under Assets/Images/) — one line per ImageResources.xaml:8-88 entry.</summary>
    internal static readonly (string Key, string Path)[] Entries =
    [
        ("ActionCancelImage", "action_cancel.png"),
        ("ActionRetryImage", "action_retry.png"),
        // …ALL 69 entries, copied 1:1 from src/Resources/ImageResources.xaml:8-88, converting
        // pack://application:,,,/Properties/Images/<p> → "<p>". Do not skip any; do not rename.
        ("FileHosterStorage.toImage", "FileHosters/filehoster_storage.to.png"),
        ("LogoIcon", "Logo/icon.ico"),
        ("Logo20Image", "Logo/logo_20_20.png"),
    ];

    internal static void MergeInto(IResourceDictionary resources)
    {
        string assembly = typeof(BitmapImageResources).Assembly.GetName().Name!;
        foreach ((string key, string path) in Entries)
        {
            Uri uri = new($"avares://{assembly}/Assets/Images/{path}");
            resources.Add(key, new Bitmap(AssetLoader.Open(uri)));
        }
    }
}
```

Caveat on `LogoIcon` (the only .ico in the table): if `new Bitmap(...)` cannot decode ICO (§Reality-check register), drop the entry, `grep -rn "LogoIcon" src/Views src/Resources` for consumers, and note the substitution the consuming view port must make (Avalonia windows take `WindowIcon` separately — MainWindow already does).

- [ ] **Step 4: Merge into the app — in `App.Initialize`, not `OnFrameworkInitializationCompleted`.** `OnFrameworkInitializationCompleted` is guarded by `is IClassicDesktopStyleApplicationLifetime` and never runs under the headless test session; `Initialize` runs in both. Change `App.axaml.cs:21`:

```csharp
public override void Initialize()
{
    AvaloniaXamlLoader.Load(this);
    // Bitmaps merge in code (no XAML form for keyed bitmap resources); geometries and the
    // theme dictionaries merge in App.axaml. Initialize (not OnFrameworkInitializationCompleted)
    // so the headless test session gets the identical resource surface.
    Resources.MergedDictionaries.Add(BuildBitmapDictionary());
}

private static ResourceDictionary BuildBitmapDictionary()
{
    ResourceDictionary dict = new();
    // Fully qualified: inside App, the bare identifier `Resources` resolves to the
    // Application.Resources property, not the CSUploader.Resources namespace.
    CSUploader.Resources.BitmapImageResources.MergeInto(dict);
    return dict;
}
```

And in `App.axaml`, merge the geometries:

```xml
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="/Resources/ImageGeometries.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
```

Then switch the headless bootstrap to the real `App` (prep item 7 — this is the deliberate TestAppBuilder extension; the desktop-lifetime guard keeps startup composition from running under test). In `TestAppBuilder.cs`, replace `Configure<TestApp>` with `Configure<App>`, delete the now-unused `TestApp` class, and update the doc comment: the real App's XAML (FluentTheme, DataGrid styles, resource dictionaries) loads; `OnFrameworkInitializationCompleted`'s DI composition still never runs because the headless lifetime is not `IClassicDesktopStyleApplicationLifetime`.

**UseSkia stays OFF (prep item 7's "+ UseSkia where rendering is asserted" clause, made explicit):** no Phase 3 test asserts rendered pixels. Headless without Skia stubs bitmap loading — `new Bitmap(stream)` returns a stub instead of throwing — so this task's `Bitmap` asserts are safe as-is; and Task 4's `SvgImage.Size` comes from Svg.Skia's own SVG parsing, independent of Avalonia's render mode. Flip to `new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }` + `.UseSkia()` only when a test actually asserts rendering (or if the SvgImage escape hatch in the §Reality-check register fires).

- [ ] **Step 5: Parity tests** (`ImageResourceTests.cs`, `[AvaloniaFact]` — needs the app instance):

```csharp
[AvaloniaFact]
public void EveryBitmapEntry_ResolvesToALoadedBitmap()
{
    Assert.Equal(69, BitmapImageResources.Entries.Length); // count pinned to ImageResources.xaml:8-88
    foreach ((string key, _) in BitmapImageResources.Entries)
    {
        Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
        Assert.IsType<Bitmap>(value); // a wrong path would have thrown in MergeInto already
    }
}

[AvaloniaFact]
public void LoadBearingComputedKeys_Exist()
{
    // The exact keys HosterIconConverter computes for the awkward names (dots, hyphens):
    foreach (string key in (string[])["FileHosterStorage.toImage", "FileHosterTransfer.itImage",
        "FileHosterFilehoster.ioImage", "FileHosterExloadImage", "StatusSuccessImage", "StatusFailedImage"])
    {
        Assert.True(Application.Current!.TryFindResource(key, out _), $"missing resource: {key}");
    }
}

[AvaloniaFact]
public void EveryGeometryKey_Resolves()
{
    foreach (string key in (string[])["SettingsLanguageGeometry", "SettingsDeveloperGeometry",
        "SettingsGridAppearanceGeometry", "SettingsWindowBehaviourGeometry", "SettingsConfirmationGeometry",
        "SettingsNotificationsGeometry", "SettingsDatabaseGeometry", "ForceStartGeometry"])
    {
        Assert.True(Application.Current!.TryFindResource(key, out object? value), $"missing resource: {key}");
        Assert.IsAssignableFrom<Avalonia.Media.Geometry>(value);
    }
}
```

If `TryFindResource`'s extension shape differs on 11.3.18 (§Reality-check register), adapt mechanically.

- [ ] **Step 6:** Both suites green (this is also the proof the `TestAppBuilder` swap didn't break the 5 existing tests); record the new Avalonia count. **Commit** — `"feat(avalonia): image assets + ImageResources with WPF key parity (69 bitmaps, 8 geometries); headless tests boot the real App"`

---

### Task 4: SVG pipeline (Avalonia.Svg.Skia + FileTypeIconConverter)

**Version resolution (prep item 6, done at plan time):** `Avalonia.Svg.Skia` **11.3.0** — the only 11.3-line release; depends on `Avalonia >= 11.3.0` (open range, accepts our 11.3.18) + `Svg.Skia 3.0.2` + **`SkiaSharp >= 3.116.1`**. Note the wrinkle: Avalonia.Skia 11.3.18 declares `SkiaSharp >= 2.88.9`, so adding this package uplifts the app's SkiaSharp to 3.116.1. Avalonia 11.3 advertises SkiaSharp-3 compatibility, but **verify at runtime, not by faith** (Step 5 renders a real SVG through the live app). Pre-agreed fallback if rendering/runtime breaks: `Avalonia.Svg.Skia 11.2.0.3` (Svg.Skia 2.0.0.5 + SkiaSharp 2.88.9 — stays on Avalonia's own SkiaSharp line; Avalonia floor 11.2.6, satisfied).

**Files:**
- Create: `src/CSUploader.Avalonia/Converters/FileTypeIconConverter.cs`
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` (package + SVG glob + submodule guard)
- Test: `tests/CSUploader.Avalonia.Tests/Converters/FileTypeIconConverterTests.cs`

**Interfaces:**
- Produces: `CSUploader.Converters.FileTypeIconConverter : IValueConverter` (Avalonia) — file name string → cached `SvgImage` (`Avalonia.Svg.Skia`), same extension table as the WPF original; SVGs embedded at `avares://…/FileTypes/<name>.svg`.
- Consumes: `external/vscode-icons/icons/*.svg` (git submodule).

- [ ] **Step 1: csproj** — package + glob + guard (mirror `src/CSUploader.csproj:60-74`; the Avalonia head sits one directory deeper, hence `..\..\`):

```xml
<ItemGroup>
  <PackageReference Include="Avalonia.Svg.Skia" Version="11.3.0" />
</ItemGroup>

<!-- vscode-icons file-type SVGs from the git submodule, embedded as AvaloniaResource so
     Svg.Skia renders them by avares URI. Mirror of the WPF head's glob one level deeper. -->
<ItemGroup>
  <AvaloniaResource Include="..\..\external\vscode-icons\icons\*.svg" Link="FileTypes\%(Filename)%(Extension)" />
</ItemGroup>

<!-- A fresh worktree/clone without submodule init leaves the glob empty, which silently
     ships an app with no file-type icons. Fail the build instead (same guard as the WPF head). -->
<Target Name="EnsureVsCodeIconsSubmodule" BeforeTargets="BeforeBuild">
  <Error Condition="!Exists('..\..\external\vscode-icons\icons\file_type_json.svg')"
         Text="vscode-icons submodule not initialized — run: git submodule update --init --recursive" />
</Target>
```

**FIRST ACTION after editing — restore-verify**: `dotnet restore src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` must resolve cleanly (watch the SkiaSharp 3.116.1 uplift in the output; a version conflict here means fall back to 11.2.0.3 and record it).

- [ ] **Step 2: Converter.** Port of `src/Converters/FileTypeIconConverter.cs` — same extension table **copied verbatim** (all ~50 rows), Avalonia `IValueConverter`, and a static cache so grids don't re-parse an SVG per row:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Svg.Skia;

namespace CSUploader.Converters;

/// <summary>
/// Maps a file name (or extension) to the matching vscode-icons SVG, as a cached
/// <see cref="SvgImage"/> for Image.Source. Avalonia twin of the WPF converter
/// (src/Converters/FileTypeIconConverter.cs) — same extension table, same fallbacks;
/// differs only in payload type (WPF returned a pack URI for SharpVectors).
/// </summary>
public class FileTypeIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SvgImage> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.Ordinal)
    {
        // …copy ALL entries 1:1 from src/Converters/FileTypeIconConverter.cs:24-89 (video/audio/
        // archive/image/document/text groups, including the .nfo/.srr/.srs scene-file comment)…
        ["mkv"] = "video",
        ["txt"] = "text",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s) || !s.Contains('.', StringComparison.Ordinal))
        {
            return Load("default_file");
        }

        string ext = Path.GetExtension(s).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
        {
            return Load("default_file");
        }

        return ExtensionMap.TryGetValue(ext, out string? iconName)
            ? Load("file_type_" + iconName)
            : Load("default_file");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SvgImage Load(string name) => Cache.GetOrAdd(name, static n =>
    {
        // Code-side avares uses the assembly name (rename-proof at the Phase 9 cutover).
        string assembly = typeof(FileTypeIconConverter).Assembly.GetName().Name!;
        return new SvgImage { Source = SvgSource.Load($"avares://{assembly}/FileTypes/{n}.svg", null) };
    });
}
```

`SvgSource.Load`'s exact signature on 11.3.0 is a §Reality-check item — adapt mechanically (some versions take `(string path, Uri? baseUri)`, some have `LoadFromResource`).

- [ ] **Step 3: Tests** (`[AvaloniaFact]`):
  - `KnownExtension_ReturnsSvgImage` — `Convert("movie.mkv", …)` returns an `SvgImage` whose `Size` has positive width/height (proves the asset resolved AND parsed).
  - `UnknownExtension_FallsBackToDefaultIcon` — `Convert("weird.xyz", …)` returns the same cached instance as `Convert("also.abc", …)` (both `default_file`).
  - `PackageRowText_NoExtension_FallsBackToDefaultIcon` — `Convert("ReScene Files", …)` non-null.
  - `RepeatCalls_ServeTheCachedInstance` — two `Convert("a.mkv")` calls return `Object.ReferenceEquals` images.
- [ ] **Step 4:** Full suite gate (both).
- [ ] **Step 5: Live render proof** (the SkiaSharp-3 uplift check): build + launch `D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe --agent` — app starts, no SkiaSharp `MissingMethodException`/`TypeLoadException` on the render path (check `ava_logs` after connecting via ava-drive; a plain successful `ava_screenshot` of the shell IS the Skia render proof). The SVG control itself renders in Task 9's gallery. If the process dies on a Skia type load: swap to `Avalonia.Svg.Skia 11.2.0.3`, re-run, and record the fallback in the task notes + §Reality-check register outcome.
- [ ] **Step 6: Commit** — `"feat(avalonia): SVG pipeline — Avalonia.Svg.Skia 11.3.0, vscode-icons glob + submodule guard, cached FileTypeIconConverter"`

---

### Task 5: Theme tokens, ThemeVariant dictionaries, density, shared styles, real AvaloniaThemeApplier

Design §Theming: FluentTheme base; port Tokens.xaml palette/spacing as resources; Theme.Light/Dark → ThemeVariant dictionaries (runtime switch via `RequestedThemeVariant` replaces the WPF merged-dict swap + SystemColors overrides). **Explicit non-goal:** the WPF Tokens.xaml re-templates (ComboBox/Menu/ContextMenu/ScrollBar/TabControl/DatePicker/Calendar, `Tokens.xaml:155-895`) exist because WPF's default theme can't do dark mode — FluentTheme + variants makes them unnecessary. Re-template ONLY where the contact sheet later shows Fluent visibly diverging (design: screenshot-compared; reviewer arbitrates "close and consistent" over pixel-perfect).

**Files:**
- Create: `src/CSUploader.Avalonia/Resources/Tokens.axaml`, `src/CSUploader.Avalonia/Resources/ThemeBrushes.axaml`, `src/CSUploader.Avalonia/Resources/BaseStyles.axaml`
- Modify: `src/CSUploader.Avalonia/App.axaml`, `src/CSUploader.Avalonia/Services/AvaloniaThemeApplier.cs` (stub → real)
- Test: `tests/CSUploader.Avalonia.Tests/Theming/ThemeTests.cs`

**Interfaces:**
- Produces: every brush key from `src/Resources/Theme.Light.xaml` / `Theme.Dark.xaml` resolvable per ThemeVariant (**64 keys per variant** — every `x:Key`'d entry EXCEPT the five `SystemColors.*BrushKey` overrides per file, which are WPF-only mechanics and are deliberately dropped; counts verified at plan time: `grep -c 'x:Key="'` = 69 in each theme file, 5 of them SystemColors); the token keys from `Tokens.xaml:8-41` (spacing/typography/control sizing/corners + `GridFontFamily`/`GridFontSize`); style classes `form-label`, `section-header`, `jd2`, `primary`, `secondary`; a real `AvaloniaThemeApplier`.
- Consumes: `IThemeApplier` contract (`src/CSUploader.Core/Services/IThemeApplier.cs:21,30`), `IAppLogger`.

- [ ] **Step 1: Density decision (prep item 4 — decided NOW, before any shared style).** The WPF UI is compact: ControlHeightSm 24 / DataGrid row 26 / body font 12-13 vs Fluent's ~32px controls and 14px font. **Decision: `<FluentTheme DensityStyle="Compact" />` + a global 13px font + DataGrid RowHeight 26.** Targeted per-control size overrides are NOT added speculatively — the Task 9 contact sheet arbitrates whether Compact alone is "close and consistent"; add overrides only against evidence, recorded in the task notes.
- [ ] **Step 2: `Tokens.axaml`** — port `Tokens.xaml:8-41` values 1:1 (Avalonia declares all these types in resource dictionaries; `GridLength` is the one §Reality-check candidate):

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Grid font tokens — runtime-overridable via IThemeApplier.ApplyGridFont (Settings →
       General → Grid Font/Size). Grids consume them via DynamicResource. -->
  <FontFamily x:Key="GridFontFamily">Tahoma</FontFamily>
  <x:Double x:Key="GridFontSize">12</x:Double>

  <!-- Spacing -->
  <Thickness x:Key="SpacingNone">0</Thickness>
  <Thickness x:Key="SpacingXs">2</Thickness>
  <Thickness x:Key="SpacingSm">4</Thickness>
  <Thickness x:Key="SpacingMd">8</Thickness>
  <Thickness x:Key="SpacingLg">12</Thickness>
  <Thickness x:Key="SpacingXl">16</Thickness>
  <Thickness x:Key="GroupBoxPadding">8,6,8,8</Thickness>
  <Thickness x:Key="RowSpacingSm">0,0,0,4</Thickness>
  <Thickness x:Key="RowSpacingMd">0,0,0,8</Thickness>
  <Thickness x:Key="SectionSpacing">0,0,0,6</Thickness>

  <!-- Typography -->
  <x:Double x:Key="FontSizeXs">11</x:Double>
  <x:Double x:Key="FontSizeSm">12</x:Double>
  <x:Double x:Key="FontSizeMd">13</x:Double>
  <x:Double x:Key="FontSizeLg">14</x:Double>

  <!-- Control sizing -->
  <x:Double x:Key="ControlHeightSm">24</x:Double>
  <x:Double x:Key="ControlHeightMd">28</x:Double>
  <x:Double x:Key="DataGridRowHeight">26</x:Double>
  <GridLength x:Key="LabelWidth">110</GridLength>

  <!-- Corners -->
  <CornerRadius x:Key="RadiusSm">2</CornerRadius>
  <CornerRadius x:Key="RadiusMd">4</CornerRadius>
</ResourceDictionary>
```

If `<GridLength>` won't parse as a resource element (§Reality-check register), replace with `<x:Double x:Key="LabelWidth">110</x:Double>` and note that view ports write `Width="110"`-shaped literals or bind — record which.

- [ ] **Step 3: `ThemeBrushes.axaml`** — ONE dictionary, both variants:

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="{x:Static ThemeVariant.Light}">
      <!-- ALL 64 keys from src/Resources/Theme.Light.xaml, copied 1:1 — SolidColorBrush and
           LinearGradientBrush syntax is identical in Avalonia. DROP ONLY the five
           SystemColors.*BrushKey overrides (GrayText, Menu, MenuBar, MenuText, MenuHighlight)
           — WPF-only mechanics; Fluent's variant resources cover their role. -->
      <SolidColorBrush x:Key="SurfaceBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="TextPrimaryBrush" Color="#1F2937" />
      <LinearGradientBrush x:Key="JD2ButtonBgBrush" StartPoint="0%,0%" EndPoint="0%,100%">
        <GradientStop Color="#FCFCFC" Offset="0" />
        <GradientStop Color="#E8E8E8" Offset="1" />
      </LinearGradientBrush>
      <!-- …the remaining 61… -->
    </ResourceDictionary>
    <ResourceDictionary x:Key="{x:Static ThemeVariant.Dark}">
      <!-- ALL 64 keys from src/Resources/Theme.Dark.xaml, same treatment. -->
      <SolidColorBrush x:Key="SurfaceBrush" Color="#1E1F26" />
      <!-- … -->
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

Gradient note: WPF `StartPoint="0,0" EndPoint="0,1"` is relative-to-bounds; the Avalonia equivalent is percent syntax (`0%,0%` → `0%,100%`) — convert every gradient this way.

- [ ] **Step 4: Real `AvaloniaThemeApplier`** (replaces the Phase 2 stub) — this class is now the **sole owner of `RequestedThemeVariant` changes** (prep item 5); `App.axaml`'s hardcoded `RequestedThemeVariant="Light"` stays as the pre-hydration default (startup light-flash = WPF parity, intended). It is also the designated sole writer of the Phase 7 new-window dark-chrome preference when that lands.

```csharp
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CSUploader.Lib;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IThemeApplier"/>. ApplyTheme sets
/// <see cref="Application.RequestedThemeVariant"/> (the ThemeVariant dictionaries in
/// ThemeBrushes.axaml follow); ApplyGridFont writes the two grid-font resources the
/// DataGrids consume via DynamicResource. SOLE writer of the theme variant after startup
/// (App.axaml's hardcoded Light is only the pre-hydration default — design prep item 5).
/// Win11 recolors the title bar with the variant automatically; the Win10 DWM fallback
/// is Phase 7's item.
/// </summary>
public sealed class AvaloniaThemeApplier(IAppLogger logger) : IThemeApplier
{
    public void ApplyTheme(bool isDark)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public void ApplyGridFont(string family, double size)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Resources["GridFontFamily"] = new FontFamily(family);
            app.Resources["GridFontSize"] = size;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to apply grid font: {ex.Message}");
        }
    }
}
```

- [ ] **Step 5: `BaseStyles.axaml`** (a `<Styles>` file) — the keyed-WPF-style → Avalonia-class mapping (`FormLabelStyle` → `Classes="form-label"`, etc.; the Phases 4-6 port rule). Fluent pseudo-class overrides go through the template part (`/template/ ContentPresenter#PART_ContentPresenter` — Fluent's Button template root; §Reality-check register):

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!-- App-wide compact typography + palette (WPF parity: implicit Window style, Tokens.xaml:773-777). -->
  <Style Selector="Window">
    <Setter Property="Background" Value="{DynamicResource SurfaceBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
    <Setter Property="FontSize" Value="{DynamicResource FontSizeMd}" />
  </Style>

  <!-- FormLabelStyle → Classes="form-label" -->
  <Style Selector="TextBlock.form-label">
    <Setter Property="VerticalAlignment" Value="Center" />
    <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}" />
    <Setter Property="FontSize" Value="{DynamicResource FontSizeMd}" />
    <Setter Property="Margin" Value="0,0,8,0" />
  </Style>

  <!-- SectionHeaderStyle → Classes="section-header" -->
  <Style Selector="TextBlock.section-header">
    <Setter Property="FontSize" Value="{DynamicResource FontSizeLg}" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
    <Setter Property="Margin" Value="0,0,0,6" />
  </Style>

  <!-- PrimaryButtonStyle → Classes="primary" -->
  <Style Selector="Button.primary">
    <Setter Property="Background" Value="{DynamicResource AccentBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource AccentForegroundBrush}" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="16,6" />
    <Setter Property="FontSize" Value="{DynamicResource FontSizeMd}" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="CornerRadius" Value="{DynamicResource RadiusMd}" />
  </Style>
  <Style Selector="Button.primary:pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource AccentHoverBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource AccentForegroundBrush}" />
  </Style>

  <!-- SecondaryButtonStyle → Classes="secondary" -->
  <Style Selector="Button.secondary">
    <Setter Property="Padding" Value="12,4" />
    <Setter Property="FontSize" Value="{DynamicResource FontSizeSm}" />
    <Setter Property="Background" Value="{DynamicResource SurfaceMutedBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}" />
  </Style>

  <!-- JD2Button → Classes="jd2" (gradient raised button; brushes come from the variant dicts) -->
  <Style Selector="Button.jd2">
    <Setter Property="Height" Value="28" />
    <Setter Property="MinWidth" Value="90" />
    <Setter Property="Padding" Value="10,0" />
    <Setter Property="FontSize" Value="12" />
    <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
    <Setter Property="Background" Value="{DynamicResource JD2ButtonBgBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource JD2ButtonBorderBrush}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="1" />
  </Style>
  <Style Selector="Button.jd2:pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource JD2ButtonHoverBgBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource JD2ButtonHoverBorderBrush}" />
  </Style>
  <Style Selector="Button.jd2:pressed /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource JD2ButtonPressedBgBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource JD2ButtonPressedBorderBrush}" />
  </Style>

  <!-- DataGrid baseline: compact rows + the runtime-overridable grid font. -->
  <Style Selector="DataGrid">
    <Setter Property="FontFamily" Value="{DynamicResource GridFontFamily}" />
    <Setter Property="FontSize" Value="{DynamicResource GridFontSize}" />
    <Setter Property="RowHeight" Value="26" />
  </Style>

</Styles>
```

Deliberately NOT ported (record in the port-rule table, Task 6 Step 1): `CompactTextBoxStyle`/`CompactComboBoxStyle`/`CompactPasswordBoxStyle` (Compact density covers them; PasswordBox is a TextBox+PasswordChar in Avalonia, a Phase 5 concern), every implicit dark-mode re-template (`Tokens.xaml:155-895`), `DataGridEditingTextBoxStyle` (Phase 6, only if the editing cell proves unreadable).

- [ ] **Step 6: App.axaml** — density + includes (final shape):

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="CSUploader.App"
             RequestedThemeVariant="Light">
  <!-- RequestedThemeVariant=Light is only the pre-hydration default; after startup the ONLY
       writer is AvaloniaThemeApplier (design prep item 5). Startup light-flash = WPF parity. -->
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="/Resources/Tokens.axaml" />
        <ResourceInclude Source="/Resources/ThemeBrushes.axaml" />
        <ResourceInclude Source="/Resources/ImageGeometries.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
  <Application.Styles>
    <FluentTheme DensityStyle="Compact" />
    <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />
    <StyleInclude Source="/Resources/BaseStyles.axaml" />
  </Application.Styles>
</Application>
```

- [ ] **Step 7: Tests** (`ThemeTests.cs`, `[AvaloniaFact]`; save/restore `Application.Current.RequestedThemeVariant` around each test — it's process state):
  - `EveryBrushKey_ResolvesInBothVariants` — for each of the 64 keys (pin the list by copying the `x:Key` names once into the test), `TryFindResource(key, ThemeVariant.Light, out _)` and `(…, ThemeVariant.Dark, …)` both true; assert `IBrush`. This is the drift gate between the two variant dictionaries.
  - `SurfaceBrush_DiffersBetweenVariants` — resolve per variant, assert the two `SolidColorBrush.Color` values are `#FFFFFF` vs `#1E1F26` (pins variant plumbing, not just key presence).
  - `ApplyTheme_FlipsRequestedThemeVariant` — `new AvaloniaThemeApplier(Mock.Of<IAppLogger>()).ApplyTheme(true)` → `Application.Current.RequestedThemeVariant == ThemeVariant.Dark`; false → Light.
  - `ApplyGridFont_OverwritesTokens` — call `ApplyGridFont("Verdana", 14)`; `TryFindResource("GridFontSize")` returns `14.0` and `GridFontFamily` name contains `Verdana`. (Live DynamicResource propagation to a rendered control is verified in the Task 9 gallery, where a real window exists — see §Reality-check register.)
  - Token spot-check — `TryFindResource("SpacingMd")` is `Thickness(8)`, `"ControlHeightSm"` is `24.0`.
- [ ] **Step 8:** Full suite gate (both); record counts. **Commit** — `"feat(avalonia): theme tokens + ThemeVariant brush dictionaries, Fluent Compact density, base style classes, real AvaloniaThemeApplier"`

---

### Task 6: Port the value converters (with full disposition table)

All 21 WPF converter files (22 classes) enumerated; 20 classes port, 2 don't. Ported converters keep their WPF class names (including "…ToVisibility…" — Avalonia visibility IS a bool; keeping names makes the Phases 4-6 XAML ports mechanical) and the head-local namespace `CSUploader.Converters` (RootNamespace is `CSUploader`, so ported XAML keeps `xmlns:conv="clr-namespace:CSUploader.Converters"` unchanged).

**Disposition table** (this is also the Phases 4-6 port rule for `Visibility=` attributes):

| # | WPF class (src/Converters/) | Disposition in `src/CSUploader.Avalonia/Converters/` |
|---|------------------------------|------------------------------------------------------|
| 1 | AccountCheckStatusToColorConverter | **Port** — theme lookup becomes variant-aware (`TryFindResource(key, app.ActualThemeVariant, …)`); same fallback brushes |
| 2 | BoolToVisibilityConverter | **NOT ported.** Port rule: `Visibility="{Binding X, Converter=BoolToVisibilityConverter}"` → `IsVisible="{Binding X}"`; the `ConverterParameter=Invert` form → `IsVisible="{Binding X, Converter={StaticResource InvertBoolConverter}}"` (or `!X` where compiled bindings arrive later) |
| 3 | ByteUnitConverter | **Port verbatim** (logic identical; interface swap only) |
| 4 | DateTimeFormatConverter | **Port verbatim** |
| 5 | EnumBoolConverter | **Port** — `Binding.DoNothing` → `Avalonia.Data.BindingOperations.DoNothing` |
| 6 | FileStateDisplayConverter | **Port verbatim** (Localizer is Core) |
| 7 | FileStateIconConverter | **Port** — `TryFindResource(...) as Bitmap` |
| 8 | FileTypeIconConverter | **Already ported in Task 4** (SVG payload) |
| 9 | HosterIconConverter | **Port** — same computed-key normalization; `Bitmap` payload |
| 10 | InvertBoolConverter | **Port verbatim** (kept two-way + non-bool passthrough; also serves rule #2) |
| 11 | ItemStateToVisibilityConverter | **Port, returns `bool`** for `IsVisible` (same three modes, same state sets) |
| 12 | ProgressWidthConverter | **Port to Avalonia `IMultiValueConverter`** (`IList<object?>` values; guard `UnsetValue`) |
| 13 | ProxyTestOutcomeIconConverter | **Port** — resource lookup |
| 14 | ResourceKeyToImageConverter | **Port** — `DependencyProperty.UnsetValue` → `AvaloniaProperty.UnsetValue` |
| 15 | SingleLineConverter | **Port verbatim** |
| 16 | SpeedLimitConverter | **Port verbatim** |
| 17 | StartMenuLabelConverter | **Port verbatim** |
| 18 | StepVisibilityConverter (StepConverters.cs) | **Port, returns `bool`** for `IsVisible` |
| 19 | StepFontConverter (StepConverters.cs) | **Port** — `FontWeights.Bold/Normal` → `Avalonia.Media.FontWeight.Bold/Normal` |
| 20 | StorageAvailableDisplayMultiConverter | **Port to Avalonia `IMultiValueConverter`** (null AND `UnsetValue` slots guard) |
| 21 | TimeSpanFormatConverter | **Port verbatim** |
| 22 | UrlDecodeConverter | **Port verbatim** |

**Files:**
- Create: `src/CSUploader.Avalonia/Converters/` — one file per WPF file (19 new files; FileTypeIconConverter exists; no BoolToVisibilityConverter file), keeping `StepConverters.cs` as the two-class file it is in WPF.
- Modify: `src/CSUploader.Avalonia/App.axaml` (app-level `ResourceKeyToImageConverter` instance, mirroring `src/App.xaml:14`)
- Test: `tests/CSUploader.Avalonia.Tests/Converters/ConverterTests.cs`

**Interfaces:**
- Consumes: `Avalonia.Data.Converters.IValueConverter` / `IMultiValueConverter`, Task 3 resources, Task 5 variant brushes, Core types (`FileState`, `AccountCheckStatus`, `Package`/`PackageFile`, `ByteUnit`, `Localizer`, `FileHosterClient.HasUnlimitedStorage`, `ProxyTestOutcome`).
- Produces: the classes above, referenced by every view port in Phases 4-6.

- [ ] **Step 1: "Port verbatim" mechanics** (applies to rows 3, 4, 6, 10, 15, 16, 17, 21, 22): copy the WPF file, then change ONLY: `using System.Windows.Data` → `using Avalonia.Data.Converters`; drop `using System.Windows[.Media/.Media.Imaging]`; signatures to Avalonia's nullable shape (`object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)`). Body, comments, and class name stay identical. One full example (ByteUnitConverter) to set the pattern:

```csharp
// <copyright file="ByteUnitConverter.cs" company="CSUploader"> …standard header… </copyright>

using System.Globalization;
using Avalonia.Data.Converters;
using CSUploader.Lib;

namespace CSUploader.Converters;

public class ByteUnitConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
        }

        // (comment block copied verbatim from the WPF source)
        return parameter as string ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

- [ ] **Step 2: Resource-resolving converters** share one private helper shape; full code for the trickiest (AccountCheckStatusToColorConverter — variant-aware) and the lookup pattern the others reuse:

```csharp
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CSUploader.Dal;

namespace CSUploader.Converters;

/// <summary>(doc comment copied from the WPF source)</summary>
public class AccountCheckStatusToColorConverter : IValueConverter
{
    // Fallbacks for when no Application is running (designer / bare unit tests).
    private static readonly IBrush FallbackSuccess = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly IBrush FallbackWarning = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly IBrush FallbackUnchecked = new SolidColorBrush(Color.FromRgb(0xA8, 0xAA, 0xC0));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AccountCheckStatus status)
        {
            return Resolve("TextDisabledBrush", FallbackUnchecked);
        }

        return status switch
        {
            AccountCheckStatus.Valid => Resolve("SuccessBrush", FallbackSuccess),
            AccountCheckStatus.Failed => Resolve("ErrorBrush", FallbackError),
            AccountCheckStatus.Checking => Resolve("WarningBrush", FallbackWarning),
            _ => Resolve("TextDisabledBrush", FallbackUnchecked),
        };
    }

    private static IBrush Resolve(string resourceKey, IBrush fallback)
    {
        Application? app = Application.Current;
        // Variant-aware lookup: the brush keys live in ThemeBrushes.axaml's ThemeDictionaries,
        // so the ACTIVE variant must be passed — an unscoped lookup misses variant-scoped keys.
        return app is not null && app.TryFindResource(resourceKey, app.ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

`FileStateIconConverter` / `HosterIconConverter` / `ProxyTestOutcomeIconConverter` / `ResourceKeyToImageConverter` use the same `Application.Current?.TryFindResource(key, out …)` pattern (no variant argument needed — bitmaps aren't variant-scoped), returning `Bitmap`/`object`; copy each WPF body otherwise 1:1 (`ResourceKeyToImageConverter` returns `AvaloniaProperty.UnsetValue` where WPF returned `DependencyProperty.UnsetValue`).

- [ ] **Step 3: Multi-value + the bool/enum specials** — full code for the non-obvious deltas:

```csharp
// ProgressWidthConverter — Avalonia IMultiValueConverter: IList<object?>, and unset bindings
// arrive as AvaloniaProperty.UnsetValue (not null) — the double pattern-match guards both.
public class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2
            && values[0] is double progress
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            double clampedProgress = Math.Clamp(progress, 0.0, 100.0);
            return containerWidth * clampedProgress / 100.0;
        }

        return 0.0;
    }
}
```

`StorageAvailableDisplayMultiConverter`: same `IList<object?>` shape; body copied from the WPF source with the slot reads adapted (`values is { Length: > 0 }` → `values.Count > 0`); the `long?`/`string?` pattern matches already treat `UnsetValue` slots as "not a long" — keep them. `EnumBoolConverter.ConvertBack`: `value is true … ? Enum.Parse(targetType, paramStr) : BindingOperations.DoNothing`. `ItemStateToVisibilityConverter` / `StepVisibilityConverter`: same switch bodies, `return visible;` / `return current == step;` (bool). `StepFontConverter`: `FontWeight.Bold` / `FontWeight.Normal`.

- [ ] **Step 4: App-level converter instance** (mirror `src/App.xaml:14`) — add inside `App.axaml`'s `<ResourceDictionary>` (after the MergedDictionaries element):

```xml
      <conv:ResourceKeyToImageConverter x:Key="ResourceKeyToImageConverter" />
```

with `xmlns:conv="clr-namespace:CSUploader.Converters"` on the Application element.

- [ ] **Step 5: Tests** — mirror every test class in `tests/Converters/ConverterTests.cs` (ByteUnit, StorageAvailableDisplayMulti, TimeSpanFormat, DateTimeFormat, UrlDecode, AccountCheckStatusToColor, ItemStateToVisibility, SingleLine) with these deltas, plus new coverage for the resource-resolving converters WPF never unit-tested:
  - `Visibility.Visible/Collapsed` asserts → `true`/`false`.
  - `BoolToVisibilityConverterTests` has no twin (dropped class) — replace with `InvertBoolConverterTests` (true→false, false→true, both directions, non-bool passthrough).
  - `AccountCheckStatusToColorConverterTests` upgrades from fallback-color asserts to **real resolution**: `[AvaloniaFact]`, assert `Convert(AccountCheckStatus.Valid) is IBrush b && ReferenceEquals(b, resolved)` where `resolved` comes from `Application.Current.TryFindResource("SuccessBrush", Application.Current.ActualThemeVariant, out …)` — pins converter ↔ theme-dictionary wiring, which the WPF tests couldn't.
  - New: `ResourceKeyToImageConverter` (null/whitespace → `AvaloniaProperty.UnsetValue`; `"StatusSuccessImage"` → `Bitmap`), `HosterIconConverter` (`"Ex-Load"` → resolves the `FileHosterExloadImage` bitmap — the hyphen-stripping case; unknown hoster → null), `FileStateIconConverter` (Uploading → `StatusUploadingImage` instance; non-enum → null), `ProxyTestOutcomeIconConverter` (Ok/Failed/Untested), `StepFontConverter`, `EnumBoolConverter` (incl. `ConvertBack` DoNothing), `SpeedLimitConverter` (1024 → "1 MB/s", 512 → "512 KB/s", 0 → empty), `ProgressWidthConverter` (50, 200 → 100.0; `UnsetValue` slot → 0.0), `FileStateDisplayConverter` + `StartMenuLabelConverter` (result equals `Localizer.Instance[expectedKey]` — culture-independent).
  - `ItemStateToVisibilityConverterTests` reuses the WPF harness's `FileInState` helper verbatim (`ConverterTests.cs:499-506` — Core types, compiles unchanged).
- [ ] **Step 6:** Full suite gate (both); record counts. **Commit** — `"feat(avalonia): port 20 value converters with WPF key/name parity + headless tests (BoolToVisibility retired → IsVisible rule)"`

---

### Task 7: LocExtension port + MainWindow headers

Design §Localization: LocExtension re-authored Avalonia-idiomatically (markup extension returning a Binding to `Localizer.Instance[key]`; live switching preserved). Same resx, same keys. The two prep-item-8 verifications become tests.

**Files:**
- Create: `src/CSUploader.Avalonia/Localization/LocExtension.cs`
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml` (real localized tab headers)
- Test: `tests/CSUploader.Avalonia.Tests/Localization/LocExtensionTests.cs`; modify `tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj` (+ `Avalonia.Markup.Xaml.Loader` 11.3.18 for runtime-XAML tests)

**Interfaces:**
- Produces: `CSUploader.Lib.Localization.LocExtension` (Avalonia head) — **deliberately the same namespace as the WPF head's extension** so ported XAML keeps `xmlns:loc="clr-namespace:CSUploader.Lib.Localization"` unchanged (an assembly-less clr-namespace resolves against the local assembly in both frameworks; Core's `Localizer` shares the namespace from its own assembly, which C# allows).
- Consumes: `Localizer.Instance` (`src/CSUploader.Core/Lib/Localization/Localizer.cs:27`; culture switch raises `PropertyChanged("Item[]")` + `("")`, `:85-86`).

- [ ] **Step 1: The extension** (Avalonia markup extensions are duck-typed — no base class, just `ProvideValue`):

```csharp
// <copyright file="LocExtension.cs" company="CSUploader"> …standard header… </copyright>

using Avalonia.Data;

namespace CSUploader.Lib.Localization;

/// <summary>
/// Avalonia twin of the WPF head's LocExtension (src/Lib/Localization/LocExtension.cs):
/// <c>{loc:Loc Common_OK}</c> produces a one-way binding to <see cref="Localizer.Instance"/>'s
/// indexer, so a live culture switch (PropertyChanged("Item[]")) re-evaluates every bound
/// value in place. Same namespace as the WPF extension ON PURPOSE — ported XAML keeps its
/// xmlns:loc declaration unchanged.
/// </summary>
public sealed class LocExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
}
```

- [ ] **Step 2: MainWindow headers** — replace the four hardcoded headers (`Views/MainWindow.axaml:10-20`) with `{loc:Loc Main_Tab_Uploads}` / `Main_Tab_Uploaded` / `Main_Tab_Settings` / `Main_Tab_Logs` (the WPF originals, `src/Views/MainWindow.xaml:49-58`; keys confirmed in `docs/i18n-inventory.md:82-85`), adding `xmlns:loc="clr-namespace:CSUploader.Lib.Localization"`. Update the tracking comment to `<!-- TODO(phase5/6): real views -->`.
- [ ] **Step 3: Verification test 1 — live culture switch on a styled property** (`[AvaloniaFact]`; `Localizer.Instance.Culture` is process-global — always restore in `finally`):

```csharp
[AvaloniaFact]
public void CultureSwitch_ReEvaluatesLocBinding_OnStyledProperty()
{
    CultureInfo original = Localizer.Instance.Culture;
    try
    {
        Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
        var textBlock = (TextBlock)AvaloniaRuntimeXamlLoader.Load(
            """
            <TextBlock xmlns="https://github.com/avaloniaui"
                       xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia"
                       Text="{loc:Loc Main_Tab_Uploads}" />
            """);
        var window = new Window { Content = textBlock };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Uploads", textBlock.Text);              // en neutral value (i18n-inventory.md:82)

        Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("アップロード", textBlock.Text);           // ja satellite (i18n-inventory.ja.md:81)
        window.Close();
    }
    finally
    {
        Localizer.Instance.Culture = original;
    }
}
```

This pins the load-bearing invalidation chain: `Localizer` raises `PropertyChanged("Item[]")` and Avalonia's reflection indexer binding must honor it (WPF does; if Avalonia only re-evaluates on the empty-string notification, the test still passes because `Localizer` raises both — but if NEITHER works, this catches it before 20 views depend on it).

- [ ] **Step 4: Verification test 2 — DirectProperty target (the grid-header case).** Same runtime-XAML approach against the real consumer shape: a `DataGrid` with `<DataGridTextColumn Header="{loc:Loc Main_Tab_Uploads}">`. Verified at plan time by reflection on the built 11.3.13 assembly: `DataGridColumn.HeaderProperty` is a **`DirectProperty<DataGridColumn, object>`** (an AvaloniaObject property, NOT a plain CLR property), so the explicit-Source loc binding SHOULD attach, and DirectProperty bindings default to OneWay — matching LocExtension's mode. The open question this test answers is narrower: does the bound header **live-update** when `Localizer` raises `PropertyChanged("Item[]")`? Assert the header renders the localized string AND updates on a culture switch. Only if the update assert fails does the fallback enter the Phases 5/6 port rule (*grid column headers rebind/reassign in code-behind on culture change*) — record the verdict either way; do not silently drop the test.
- [ ] **Step 5:** Build the head, launch `--agent`, `dotnet run scripts/ava-drive.cs -- ava_tree` — the four tab headers still read Uploads/Uploaded/Settings/Logs (now via Localizer). Check `ava_logs` for binding errors (`area:"Binding"`): none.
- [ ] **Step 6:** Full suite gate (both); record counts. **Commit** — `"feat(avalonia): LocExtension (live culture switching) + localized MainWindow tab headers"`

---

### Task 8: The two DataGrid behaviors

Design §The Avalonia head: rebuilt on `PointerPressed`/`ContextRequested` — Avalonia has no `PreviewMouseRightButtonDown`/`ContextMenuOpening`. The right-click handler runs at **tunnel** phase so the selection is already updated when `ContextRequested` fires (the SelectedRows-snapshot timing guarantee); full interaction verification happens when the first consuming grid ships (Phase 5), per the design — this task delivers the port + the best headless coverage we can get.

**Files:**
- Create: `src/CSUploader.Avalonia/Behaviors/DataGridSelectionBehaviors.cs`, `src/CSUploader.Avalonia/Behaviors/AutoScrollBehavior.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Behaviors/DataGridBehaviorTests.cs`

**Interfaces:**
- Produces: attached properties `DataGridSelectionBehaviors.ClearSelectionOnEmptyClick` / `.SelectRowOnRightClick` and `AutoScrollBehavior.IsEnabled`, namespace `CSUploader.Behaviors` (XAML ports keep `xmlns:beh="clr-namespace:CSUploader.Behaviors"`).
- Consumes: Avalonia `DataGrid`/`DataGridRow`/`DataGridColumnHeader` (Avalonia.Controls.DataGrid 11.3.13), `ScrollBar`.

- [ ] **Step 1: `DataGridSelectionBehaviors`** (non-static class — Avalonia's generic `RegisterAttached` needs a non-static owner type; §Reality-check register):

```csharp
// <copyright …standard header… >

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CSUploader.Behaviors;

/// <summary>
/// Attached behaviors that make <see cref="DataGrid"/> selection match conventional
/// Windows / Explorer UX — Avalonia rebuild of the WPF behaviors
/// (src/Behaviors/DataGridSelectionBehaviors.cs); same two independent switches.
/// The right-click handler is registered at TUNNEL phase so selection is already
/// updated when ContextRequested fires and context-menu commands snapshot SelectedRows
/// (the WPF PreviewMouseRightButtonDown → ContextMenuOpening ordering guarantee).
/// </summary>
public sealed class DataGridSelectionBehaviors
{
    public static readonly AttachedProperty<bool> ClearSelectionOnEmptyClickProperty =
        AvaloniaProperty.RegisterAttached<DataGridSelectionBehaviors, DataGrid, bool>("ClearSelectionOnEmptyClick");

    public static readonly AttachedProperty<bool> SelectRowOnRightClickProperty =
        AvaloniaProperty.RegisterAttached<DataGridSelectionBehaviors, DataGrid, bool>("SelectRowOnRightClick");

    static DataGridSelectionBehaviors()
    {
        ClearSelectionOnEmptyClickProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed_ClearIfEmpty, RoutingStrategies.Bubble);
            }
            else
            {
                grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed_ClearIfEmpty);
            }
        });

        SelectRowOnRightClickProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                // Tunnel: must beat both the grid's own selection handling and ContextRequested.
                grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed_SelectRowOnRight, RoutingStrategies.Tunnel);
            }
            else
            {
                grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed_SelectRowOnRight);
            }
        });
    }

    public static void SetClearSelectionOnEmptyClick(DataGrid grid, bool value) => grid.SetValue(ClearSelectionOnEmptyClickProperty, value);

    public static bool GetClearSelectionOnEmptyClick(DataGrid grid) => grid.GetValue(ClearSelectionOnEmptyClickProperty);

    public static void SetSelectRowOnRightClick(DataGrid grid, bool value) => grid.SetValue(SelectRowOnRightClickProperty, value);

    public static bool GetSelectRowOnRightClick(DataGrid grid) => grid.GetValue(SelectRowOnRightClickProperty);

    private static void OnPointerPressed_ClearIfEmpty(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A hit on a row keeps normal selection handling; header clicks (sorting) and
        // scrollbar clicks must not drop the selection either — mirror of the WPF walk.
        if (FindOwnChromeAncestor(e.Source as Visual, grid) is not null)
        {
            return;
        }

        grid.SelectedItems.Clear();
    }

    private static void OnPointerPressed_SelectRowOnRight(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
        {
            return;
        }

        DataGridRow? row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row is null || grid.SelectedItems.Contains(row.DataContext))
        {
            return; // right-click inside the selection preserves the multi-selection (Explorer UX)
        }

        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(row.DataContext);
    }

    /// <summary>Walks source→grid; returns the first row/header/scrollbar hit, else null.
    /// Internal so the headless tests can pin the walk without synthesizing pointer events.</summary>
    internal static Visual? FindOwnChromeAncestor(Visual? source, DataGrid grid)
    {
        for (Visual? v = source; v is not null && v != grid; v = v.GetVisualParent())
        {
            if (v is DataGridRow or DataGridColumnHeader or ScrollBar)
            {
                return v;
            }
        }

        return null;
    }
}
```

- [ ] **Step 2: `AutoScrollBehavior`** — WPF original subscribed `dataGrid.Items` (an auto-tracking `ItemCollection`); Avalonia must track `ItemsSource` changes itself. Also fix the WPF version's known sloppiness (never unsubscribed on disable) instead of porting it:

```csharp
// <copyright …standard header… >

using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace CSUploader.Behaviors;

/// <summary>
/// Scrolls a <see cref="DataGrid"/> to its newest item whenever the bound collection grows
/// (the Logs tab's follow mode). Avalonia rebuild of src/Behaviors/AutoScrollBehavior.cs;
/// unlike the WPF original it tracks ItemsSource swaps and detaches cleanly on disable.
/// </summary>
public sealed class AutoScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<AutoScrollBehavior, DataGrid, bool>("IsEnabled");

    // Per-grid subscription state (the INCC handler currently attached, if any).
    private static readonly AttachedProperty<NotifyCollectionChangedEventHandler?> AttachedHandlerProperty =
        AvaloniaProperty.RegisterAttached<AutoScrollBehavior, DataGrid, NotifyCollectionChangedEventHandler?>("AttachedHandler");

    static AutoScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                Attach(grid, grid.ItemsSource);
                grid.PropertyChanged += Grid_PropertyChanged;
            }
            else
            {
                grid.PropertyChanged -= Grid_PropertyChanged;
                Detach(grid, grid.ItemsSource);
            }
        });
    }

    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    private static void Grid_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is DataGrid grid && e.Property == DataGrid.ItemsSourceProperty)
        {
            Detach(grid, e.OldValue as IEnumerable);
            Attach(grid, e.NewValue as IEnumerable);
        }
    }

    private static void Attach(DataGrid grid, IEnumerable? source)
    {
        if (source is not INotifyCollectionChanged incc)
        {
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && grid.ItemsSource is IList { Count: > 0 } list)
            {
                grid.ScrollIntoView(list[^1], null);
            }
        };
        incc.CollectionChanged += handler;
        grid.SetValue(AttachedHandlerProperty, handler);
    }

    private static void Detach(DataGrid grid, IEnumerable? source)
    {
        if (source is INotifyCollectionChanged incc && grid.GetValue(AttachedHandlerProperty) is { } handler)
        {
            incc.CollectionChanged -= handler;
            grid.SetValue(AttachedHandlerProperty, null);
        }
    }
}
```

(The WPF original scrolled on EVERY collection change; scrolling on `Add` only is the same observable behavior for a log grid — if the Phase 5 checklist disagrees, widen it there.)

- [ ] **Step 3: Headless tests.** Primary path — real input simulation against a shown headless window (`Avalonia.Headless` exposes `window.MouseDown/MouseUp(Point, MouseButton)`-style helpers; TestApp is the real App since Task 3, so DataGrid's Fluent styles are loaded and rows realize):
  - `RightClick_OnUnselectedRow_SelectsExactlyThatRow` — grid with 3 string items, `SelectedItems = [items[0]]`; locate row 2's realized `DataGridRow` via `grid.GetVisualDescendants().OfType<DataGridRow>()`, translate its center to window coordinates, `window.MouseDown(point, MouseButton.Right)`; assert `SelectedItems` is exactly `[items[2]]`.
  - `RightClick_InsideSelection_PreservesMultiSelection` — select rows 0+1, right-click row 1's point; assert both still selected.
  - `LeftClick_OnEmptyArea_ClearsSelection` — grid taller than its 2 rows; click a point below the last row; assert `SelectedItems` empty.
  - `LeftClick_OnHeader_PreservesSelection` — click inside the header band; selection unchanged.
  - `AutoScroll_AddToBoundCollection_ScrollsWithoutThrowing` — `ObservableCollection<string>`, enabled behavior, add an item, `Dispatcher.UIThread.RunJobs()`; assert no throw and (if the headless scroll position is readable) the last row is realized.
  - **Fallback, decided per test not wholesale** (headless DataGrid row realization is a known finicky area — §Reality-check register): any case that cannot realize rows headlessly drops to unit-testing the walk/selection helpers directly (`FindOwnChromeAncestor` with a hand-built visual chain; the selection mutation via a synthesized `DataGridRow { DataContext = … }`), and that interaction case moves explicitly to the Phase 5 bridge checklist (design already schedules it there). Record which cases fell back in the task notes.
- [ ] **Step 4:** Full suite gate (both); record counts. **Commit** — `"feat(avalonia): DataGrid selection + auto-scroll behaviors (tunnel-phase right-click targeting) + headless interaction tests"`

---

### Task 9: Gallery window + the phase's first contact sheet (visual gate)

Phase 3 has no real views, but the theme/token work is exactly the kind of decision the design routes through screenshot comparison. **Judgment call (endorsed at planning): a small internal gallery window behind a dev flag** showing themed control samples in both variants makes the token port verifiable NOW and stays useful through Phases 4–6 (a standing style test page). Honest framing: at this phase the WPF shots and Avalonia shots are *different views* (real tabs vs gallery) — the gate is "tokens faithful + density close + everything renders", arbitrated by a reviewer against the WPF reference, NOT pixel-diffed pairs. Per-view pairing starts in Phase 4.

**Files:**
- Create: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml`, `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs`
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (DEBUG-only `--gallery` trigger, same pattern as `--webview-spike` at :74/:119-126)

**Interfaces:**
- Consumes: everything Phases 3 delivered — Task 5 tokens/styles/applier, Task 3 bitmaps/geometries, Task 4 SVG converter, Task 6 converters, Task 7 LocExtension.
- Produces: `shots/gallery-{light,dark}-ava.png`, `shots/shell-{light,dark}-ava.png`, the phase contact sheet, and the recorded density/token verdict.

- [ ] **Step 1: GalleryWindow.** 900×700, `Title="Gallery (dev)"`; a `ScrollViewer` of sections (hardcoded English — dev tool, tracking comment `<!-- dev-only; no i18n by design -->`). Required samples, each exercising a Phase 3 deliverable:
  - **Palette strip**: for ~10 key brushes (`SurfaceBrush`, `SurfaceAltBrush`, `SurfaceMutedBrush`, `BorderBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `AccentBrush`, `SelectionBrush`, `SuccessBrush`, `ErrorBrush`, `WarningBrush`) a 48×24 `Border Background="{DynamicResource …}"` + key name — DynamicResource means the strip re-colors on theme toggle.
  - **Typography**: one `TextBlock` per class — plain, `Classes="form-label"`, `Classes="section-header"`.
  - **Buttons row**: default, `Classes="primary"`, `Classes="secondary"`, `Classes="jd2"`, a disabled one.
  - **Inputs row**: `TextBox` (with text), `ComboBox` (3 items, one selected), `CheckBox` (checked), `RadioButton`, `DatePicker`.
  - **DataGrid**: 5 in-code sample rows (name/size/state strings), `RowHeight` from the base style, font via the `GridFontFamily`/`GridFontSize` DynamicResources — plus both behaviors attached (`beh:DataGridSelectionBehaviors.SelectRowOnRightClick="True"` etc.) so the XAML attach path is exercised.
  - **Icons row**: 4 status bitmaps via `{StaticResource StatusSuccessImage}`-style `Image.Source` + 4 hoster logos + 4 file-type SVGs via `conv:FileTypeIconConverter` bound to sample names (`a.mkv`, `b.zip`, `c.pdf`, `d.xyz`) + 3 geometries as `<PathIcon Data="{StaticResource SettingsLanguageGeometry}" Foreground="{DynamicResource AccentBrush}"/>`.
  - **Loc sample**: `<TextBlock Text="{loc:Loc Main_Tab_Uploads}"/>`.
  - **Action buttons** (named so ava_action can drive them): `ThemeToggleButton` — code-behind flips a bool and calls `IThemeApplier.ApplyTheme` (the exact production path SettingsViewModel uses); `GridFontButton` — calls `ApplyGridFont("Verdana", 14)` (proves runtime DynamicResource propagation, the §Reality-check item Task 5 deferred here).
  Code-behind: ctor takes `IThemeApplier` (resolve at the open site); keep a `private bool _dark;`.
- [ ] **Step 2: Trigger.** In `App.axaml.cs`, alongside the spike flag: `bool gallery = desktop.Args?.Contains("--gallery", StringComparer.Ordinal) == true;` and inside the `mainWindow.Opened` handler's post-init section, `#if DEBUG … if (gallery) { new DevTools.GalleryWindow(_serviceProvider.GetRequiredService<IThemeApplier>()).Show(); } … #endif`.
- [ ] **Step 3: Bridge session** (single driver — no MCP attach while ava-drive runs; make sure no second bridge app is alive):

```powershell
dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava
dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\ava     # no-op if already seeded
# launch detached (Bash tool: run_in_background):
D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe --agent --gallery
dotnet run scripts/ava-drive.cs -- ava_windows                       # gallery + main window listed
dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out D:\temp2\cbuild-mig\shots\gallery-light-ava.png
# toggle theme via the named button (arg name is `verb`, not `action` — Phase 2 gotcha; get the
# button ref from ava_tree/ava_find first, and remember find-style results are a bare array):
dotnet run scripts/ava-drive.cs -- ava_find '{"text":"ThemeToggleButton"}'
dotnet run scripts/ava-drive.cs -- ava_action '{"ref":"<button-ref>","verb":"click"}'
dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out D:\temp2\cbuild-mig\shots\gallery-dark-ava.png
# grid-font propagation check: click GridFontButton, re-screenshot, confirm the DataGrid re-rendered larger/Verdana
# shell shots (focus the main window first — screenshot targets the focused/main window per bridge semantics):
dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out D:\temp2\cbuild-mig\shots\shell-dark-ava.png
dotnet run scripts/ava-drive.cs -- ava_action '{"ref":"<theme-button-ref>","verb":"click"}'   # back to light
dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out D:\temp2\cbuild-mig\shots\shell-light-ava.png
```

(Exact per-window screenshot addressing: check `ava_screenshot`'s args for a window ref — resolve against the tool schema at run time.) Also confirm via `ava_logs`: zero `area:"Binding"` errors from the gallery.

- [ ] **Step 4: Contact sheet + verdict.** `python scripts/contact-sheet.py` → open `D:\temp2\cbuild-mig\shots\contact-sheet.html` content via Read on the PNGs it references. Review checklist (record PASS/notes per line in the task notes; reviewer arbitrates — design: "close and consistent" beats pixel-perfect):
  1. Dark gallery surface reads as the token dark (#1E1F26 family), not Fluent's default dark.
  2. Light/dark text hierarchy legible (TextPrimary vs TextSecondary vs Disabled).
  3. Compact density: control heights visually close to the WPF reference rows (24-28px, not 32).
  4. JD2/primary/secondary buttons show their gradient/accent identities in both themes.
  5. All four icon families render (bitmap status, hoster logos, SVG file types, geometry PathIcons).
  6. DataGrid: Tahoma 12 by default; after `GridFontButton`, Verdana 14 (proves live DynamicResource writes — closes the Task 5 deferred verification).
  7. Title bar follows the variant on Win11 (observe; Win10 fallback is Phase 7's item — note only).
  Any FAIL that traces to a Fluent visual divergence becomes a targeted re-template note for the phase gate (NOT an immediate fix unless trivial), keeping the "re-template only where visibly divergent, screenshot-compared" rule.
- [ ] **Step 5:** Full suite gate (both). **Commit** — `"feat(avalonia): dev gallery window (--gallery) + first migration contact sheet; density/token verdict recorded"`

---

### Task 10: Phase gate — review, tag, reconcile

- [ ] **Step 1: Whole-diff review**: `git diff phase2-shell-and-spike-ready..HEAD` reviewed by a fresh adversarial reviewer (per-task reviews already happened; this repo's history shows whole-diff panels catch cross-task issues — the Order-column DataGrid bug precedent).
- [ ] **Step 2: Gates**:
  - `grep -rn "System.Windows" src/CSUploader.Avalonia/` → zero; `grep -rn "System.Windows" src/CSUploader.Core/` → still zero.
  - Both suites green; record final counts (1178+ / 5+Task-3..8 additions) in this step's notes.
  - i18n gate green (runs inside the main suite) — this phase added no keys; the diff must show zero `Strings*.resx` changes.
  - WPF-head safety: `git diff phase2-shell-and-spike-ready..HEAD -- src/App.xaml.cs src/Services/ReferenceShotCapture.cs` is the ONLY WPF-head delta, all `#if DEBUG`; Release build succeeds (`-c Release -p:OutDir=D:\temp2\cbuild-mig\wpf-rel`); launch it once WITHOUT flags — behavior identical (tabs, one dialog).
  - CI-safety re-check (the Avalonia csproj changed): rename `Directory.Build.local.props` away → `dotnet restore` + Release build of the Avalonia head succeed (no bridge, no Svg/Skia conflict in Release) → rename back.
- [ ] **Step 3:** `git tag phase3-primitives-ready`.
- [ ] **Step 4: Reconcile the design doc** with what Phase 3 taught — at minimum: Svg.Skia 11.3.0 + SkiaSharp outcome (or the 11.2.0.3 fallback if taken), the density verdict, the `DataGridColumn.Header` bindability verdict (Task 7 Step 4) and its Phase 5/6 implication, which behavior tests fell back to helper-level + the Phase 5 checklist additions, any re-template notes from the Task 9 sheet. Commit — `"docs: reconcile design with Phase 3 outcomes (SVG stack, density verdict, loc/behavior findings)"`.
- [ ] **Step 5: Surface to the maintainer** (via the team lead): the contact-sheet path + the two seed/capture tools' one-line usage; the BoolToVisibilityConverter retirement (port rule instead); the density decision awaiting his eyeball whenever he next runs the head; the standing Phase 1 merge-back reminder if still unmerged (design §Merge protocol — and note that a Buzzheavier merge after this phase must manually add its PNG to BOTH ImageResources tables per the §Merge protocol checklist, which now includes `BitmapImageResources.Entries`).

---

## Reality-check register

Things this plan cites that the implementer must verify against the installed bits before/while coding — the plan could not pin them from source:

1. **`TryFindResource` extension shapes** — `Application.Current.TryFindResource(key, out object?)` and the ThemeVariant overload `TryFindResource(key, ThemeVariant, out object?)` on 11.3.18 (Tasks 3, 5, 6). Adapt call syntax mechanically if the signature differs.
2. **Runtime resource writes propagate to DynamicResource** — `app.Resources["GridFontSize"] = 14.0` must invalidate consumers (Task 5's `ApplyGridFont`); verified LIVE by Task 9's `GridFontButton`. If it does not propagate, the applier must instead swap a merged dictionary — record and adapt.
3. **`<GridLength x:Key>` literal** in an Avalonia ResourceDictionary (Task 5 Step 2) — fallback to `x:Double` documented in-step.
4. **ICO decode** via `new Bitmap(AssetLoader.Open(...))` for the `LogoIcon` entry (Task 3 Step 3) — drop-and-substitute path documented in-step.
5. **`SvgSource.Load` exact signature + `SvgImage.Size` semantics** on Avalonia.Svg.Skia 11.3.0 (Task 4); and the **SkiaSharp 3.116.1 uplift vs Avalonia.Skia 11.3.18 (built against 2.88.9)** — live render proof in Task 4 Step 5; pre-agreed fallback pin 11.2.0.3.
6. **Fluent Button template root is `ContentPresenter#PART_ContentPresenter`** for the `:pointerover`/`:pressed` overrides (Task 5 Step 5) — check the Fluent theme source/devtools if hover doesn't restyle.
7. **`DataGridColumn.Header` live-update on indexer invalidation** — the property KIND is resolved (reflection-verified against 11.3.13: `DirectProperty<DataGridColumn, object>`, bindable, OneWay default); Task 7 Step 4 answers only whether the binding re-evaluates on `PropertyChanged("Item[]")`; both outcomes recorded, port rule updated.
8. **Avalonia.Headless input helpers** (`window.MouseDown(Point, MouseButton)` shape) and **headless DataGrid row realization** under the real App's styles (Task 8 Step 3) — per-test fallback to helper-level units + Phase 5 checklist, documented in-step.
9. **`AvaloniaProperty.RegisterAttached` owner-type constraint** — behaviors are non-static classes for this reason (Task 8); if the non-generic overload permits static owners, keep the non-static shape anyway for uniformity.
10. **Markup-extension duck typing with ctor argument** — `{loc:Loc Main_Tab_Uploads}` compiling via XamlX with the `LocExtension(string)` ctor (Task 7); if positional-arg resolution balks, the `Key` property-setter form `{loc:Loc Key=…}` is the fallback and every ported XAML site must then be adjusted — surface it loudly, don't absorb it silently.
11. **`AvaloniaRuntimeXamlLoader`** requires the `Avalonia.Markup.Xaml.Loader` package (Task 7 csproj edit) — confirm 11.3.18 exists on nuget for it.
12. **WPF capture fidelity** — the `DrawingVisual` + `VisualBrush` + window-background technique and the settle-delay sequencing (Task 1); also whether `MainViewModel` exposes any init-completed signal worth awaiting instead (grep before accepting the delay).
13. **Seed-row field completeness** — mirror what `UploadPackageRepository`/`FileHosterLoginRepository` mappers actually read (Task 2 Step 1), and verify the two hoster names against the registered pipelines.
14. **`ava_action` argument schema** (`verb`, window-targeted `ava_screenshot` args) — resolve against the bridge tool schemas at run time (Task 9 Step 3).
15. **Theme-brush key count** — RESOLVED at plan time: `grep -c 'x:Key="'` = 69 per theme file, 5 of them SystemColors overrides → **64** ported keys/variant. Re-count only if a master merge lands new brushes before Task 5 executes.
16. **`Localizer` culture switch under test parallelism** — `[AvaloniaFact]` serializes on the UI thread, but `Localizer.Instance` is process-global; every culture-touching test restores in `finally` (Tasks 6, 7).
17. **Headless SvgImage sizing** — `SvgImage.Size` should come from Svg.Skia's parsed document, render-mode-independent; if it returns 0 under the default headless session, set `UseHeadlessDrawing = false` + `.UseSkia()` in `TestAppBuilder` (Task 3 Step 4's documented flip) and re-run Task 4's tests.
