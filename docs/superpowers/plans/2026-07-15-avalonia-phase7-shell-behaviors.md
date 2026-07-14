# Avalonia Migration Phase 7: Shell Behaviors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Avalonia head the remaining shell behaviors so it reaches feature parity with the WPF head (bar the Phase 8 WebView login): bottom-right completion toasts (real `ToastNotificationService` on an Avalonia `ToastWindow`, DIP<->physical geometry), the File/View/Help menu bar (with its theme toggle, Upload-Overview check, Check-for-updates / Install-update / About), close/minimize-to-tray behavior, balloon->toast routing for the "still running in the tray" tip, and the Win10 dark-title-bar DWM fallback (the theme-applier becoming the sole writer of the new-window dark-chrome preference). Column persistence — the sixth design bullet — already shipped structurally in Phase 6 Task 10; this phase only verifies it. Every behavior is covered by headless interaction tests plus WPF-vs-Avalonia reference shots in the phase contact sheet.

**Architecture:** Strangler step 7 (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, section Phases "Phase 7", including the six PREP ITEMS from the Phase 6 gate and the Phase 1-gate note that the theme-applier is the sole writer of the cached dark-chrome preference). The shared `ToastNotificationService`, `UploadNotificationListener`, `IToastNotificationService`/`IToastHost`/`IToastWindowFactory`/`DipRect`, `ITrayIconService`, `IThemeApplier` and `MainViewModel` already live in Core (Phase 1); this phase supplies the Avalonia implementations of the toast window/host/factory, wires the real toast service into the head's DI, adds the menu + close-to-tray code-behind on `MainWindow`, and ports `ImmersiveDarkMode` into the head. The Core ViewModels are read-only this phase — `MainViewModel` (`IsDarkMode`/`ToggleThemeCommand`/`ThemeMenuLabel`/`IsUpdateAvailable`/`AvailableVersion`/`InstallUpdateCommand`/`CheckForUpdatesAsync`/`ActivateAndShowUploadedTab`) and `UploadsViewModel.ShowUploadOverview` already expose everything the menu binds. If a port seems to need a VM change, STOP and surface it. The ONE deliberate Core (non-VM) touch this phase is a single additive method — `IToastNotificationService.ShowInfo(string,string)` + its `ToastNotificationService` implementation — required by the design's balloon-routing deliverable (design section "Tray balloon tip"). It is flagged for team-lead sign-off in section Open questions.

**Tech Stack:** unchanged — .NET 10, Avalonia 11.3.18 + Avalonia.Controls.DataGrid 11.3.13 + Avalonia.Themes.Fluent + Avalonia.Svg.Skia 11.3.0, Avalonia.Headless.XUnit 11.3.18, CommunityToolkit.Mvvm 8.4.2 (Core), Moq. This phase adds NO packages. Bridge via `scripts/ava-drive.cs`; contact sheet via `scripts/contact-sheet.py`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase6-hard-views-ready` (tip `af58310`). Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree — has uncommitted Buzzheavier work).
- Suite gate after every task (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — 1200 green at phase start; the count only goes up, never down. (Task 4 adds one Core `ShowInfo` test here -> 1201.)
  - `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — 385 green at phase start (Phase 6 verdict). Confirm the exact number at Task 1's gate and correct it here if it drifted; every Phase 7 task raises it — record each new baseline and carry it forward.
  - Separate OutDirs are mandatory (a shared OutDir mixes WPF and Avalonia assemblies and breaks discovery). Never run bare solution-level `dotnet test -p:OutDir=...`.
- Head builds: Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`; WPF `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf`. Bash mangles `-p:OutDir=D:\temp2\...` (strips backslashes) — build/test through PowerShell, or use forward slashes; otherwise a Phase 7 bridge drive launches a STALE exe (the recurring Phase 5/6 gotcha). Scratch DBs live beside those exes; seed with `dotnet run scripts/seed-fake-data.cs -- <outdir>` (idempotent).
- Every csproj keeps `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`. Version pins are hard; do not bump anything.
- Core ViewModels are untouched this phase (see section Architecture). The ONLY Core change permitted is the additive `IToastNotificationService.ShowInfo` + `ToastNotificationService.ShowInfo` (Task 4). Anything else touching `src/CSUploader.Core/**` is a plan violation. The gate (Task 8) asserts `git diff phase6-hard-views-ready..HEAD -- src/CSUploader.Core/` shows ONLY those two files, ONLY the `ShowInfo` additions.
- The WPF head is touched by exactly ONE file this phase: `src/Services/ReferenceShotCapture.cs` (Task 1 — the toast reference-shot mode, inside the existing `#if DEBUG` envelope). Anything else touching `src/` outside `src/CSUploader.Avalonia/**` and the Core exception above is a plan violation.
- i18n: NO new keys this phase. Every string the ports need already exists and is live in the WPF head: menu — `Main_Menu_File`, `Main_Menu_File_Exit`, `Main_Menu_View`, `Main_Menu_View_UploadOverview`, `Main_Menu_View_DarkMode`, `Main_Menu_View_LightMode`, `Main_Menu_Help`, `Main_Menu_Help_CheckForUpdates`, `Main_Menu_Help_InstallUpdate`, `Main_Menu_Help_About`, `Main_CheckForUpdates_Available_Format`, `Main_CheckForUpdates_AlreadyLatest`, `Main_CheckForUpdates_DialogTitle`; tray — `Tray_Balloon_Title`, `Tray_Balloon_Body`; toast — `Toast_FileCompleted_Title/Body`, `Toast_PackageCompleted_Title/Body`. The `Main_Menu_File_Exit_Gesture` key ("Alt+F4") is deliberately unused in the Avalonia menu (rule 41). The phase-gate diff must show zero `Strings*.resx` changes. Never hand-edit resx.
- Agent-safety (unchanged): Avalonia launches for bridge work always pass `--agent`; scratch DBs only; never copy a real `CSUploader.db`; all driver/gallery data is synthesized. Never click a picker in a bridge session; never drive the wizard through its final "Add". Toasts and the menu contain no upload triggers, but the menu's Install update binds a real `InstallUpdateCommand` — never invoke it in a bridge session (it would download+restart against GitHub); it is only screenshotted.
- The KeyBinding-vs-editor port rule (Phase 6 reconcile item 10 / `DataGridDeleteKeyGuard.EditorGuardedCommand`) applies to any NEW destructive gesture bound over a scope where a text editor can hold focus. Phase 7 adds NO such gesture — the menu Exit and close-to-tray are a menu `Click` and a `Window.Closing` handler, not visual-ancestor `KeyBinding`s. Confirmed N/A this phase (recorded so a later edit that adds one remembers the rule).
- `[AvaloniaFact]` discipline (Phase 3 rule): tests that flip theme/culture, open windows, or mutate a process-global static (incl. `AvaloniaImmersiveDarkMode.IsDark` and `AppSettings` fields on shared instances) restore that state in `finally`; close every window opened (snapshot the window list before closing).
- Shots convention (extends Phase 6): `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png`. Phase 7 new view names (identical on both sides so the contact sheet pairs them): `toast` (the completion-toast card). `mainwindow-uploads` is re-captured on the Avalonia side in Task 5 (it now carries the menu bar); its WPF cell already shows the menu (the WPF MainWindow always had it) and is not re-captured.
- Defender-ML false-positive (Phase 5/6 finding): where a driver/gallery file would embed dense hoster-URL literals, use neutral placeholders (`https://example.test/...`). Phase 7's new surfaces (toast text, tray text) carry no URLs — low risk, but keep the rule.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited `file:line` and copy the semantics exactly. For Avalonia API shapes this plan could not pin against the installed bits, see section Reality-check register — verify each before/while coding (ILSpy per `dotnet-skills:ilspy-decompile` on the installed 11.3.18 assemblies).

### Prep-item coverage (the six items from the Phase 6 gate, design section Phases "Phase 7")

| # | Prep item | Where |
|---|-----------|-------|
| 1 | Theme toggle lives on MainViewModel's menu bar (`IsDarkMode`/`ToggleThemeCommand`/`ThemeMenuLabel` — confirmed MainViewModel members; SettingsViewModel's `IThemeApplier` is only the grid FONT). Porting File/View/Help closes the largest standing visual divergence | Task 5 |
| 2 | The KeyBinding-vs-editor rule applies to every NEW destructive gesture Phase 7 adds — wrap in `EditorGuardedCommand` where a text editor can hold focus under the binding's scope | N/A this phase — Phase 7 adds no visual-ancestor destructive KeyBinding (menu `Click` + `Window.Closing`). Recorded in section Global Constraints |
| 3 | Column persistence: only UploadsView-specific remnants remain (the Phase 5 twin ships structurally in every grid) | ALREADY DONE — Phase 6 Task 10 wired `DataGridColumnVisibilityPersistence.CaptureCurrentState`/`ApplyAsync` + `DataGridColumnMenu.Build`/`AttachToHeaders` + `ColumnDisplayIndexChanged`->`PersistAsync` on the Avalonia UploadsView (`src/CSUploader.Avalonia/Views/UploadsView.axaml.cs:196-211`). Task 8 verifies; no build task |
| 4 | CUTOVER-NOTES LEDGER: register `UploadWizardViewModel` in DI; make `MainViewModel` IDisposable (stop the 6h `_updateTimer`, unsubscribe the ctor `Localizer.Instance.PropertyChanged` lambda) | Phase 9 (recorded, not implemented here — both need a Core VM change, forbidden this phase). Carried into Task 8's reconcile note |
| 5 | The one-shot header-metrics pass (`DataGridSortIconMinWidth` override + header Padding trim; prioritize blank checkbox headers) | Phase 9 (before the parity sweep). Not this phase; carried in Task 8's reconcile note |
| 6 | The Phase 9 parity-sweep per-grid checklist gains two items ("Delete inside a cell editor edits text"; "Ctrl+C AND Ctrl+Insert both produce the package-expanding copy") | Phase 9. Not this phase; carried in Task 8's reconcile note |

## Port rules

Rules 1-17 (Phase 4), 18-32 (Phase 5), 33-40 (Phase 6) and the KeyBinding-vs-editor rule (Phase 6 reconcile item 10) are carried forward by reference — see the prior phase plans. This phase adds five new rows.

| # | WPF | Avalonia |
|---|-----|----------|
| 41 | In-window menu bar: `<Menu>`/`<MenuItem>`; `MenuItem IsCheckable="True"` + two-way `IsChecked`; `<Separator/>`; `InputGestureText="{loc:Loc ...}"` (display-only "Alt+F4") | Use Avalonia's in-window `<Menu>`/`<MenuItem>` — NOT `NativeMenu` (on Windows `NativeMenu` drives the tray/native menu, not a window menu bar). `IsCheckable="True"` -> `ToggleType="CheckBox"` (the two-way `IsChecked` binding stays). `<Separator/>` -> `<Separator/>` (supported in Avalonia `Menu`). `InputGestureText` -> DROP — Avalonia's `MenuItem.InputGesture` is a real `KeyGesture` accelerator, not a display string; the gesture text is an accepted divergence (the Phase 6 gate already ruled gesture-text keys droppable). `Main_Menu_File_Exit_Gesture` stays in resx, unused |
| 42 | Chrome-less popup window: `WindowStyle="None"` + `AllowsTransparency="True"` + `Background="Transparent"` + `ResizeMode="NoResize"` + `ShowActivated="False"` + `WindowStartupLocation="Manual"`; `Border.Effect` = `DropShadowEffect`; positioned via `Window.Top`/`Left` (DIP) | `SystemDecorations="None"` + `TransparencyLevelHint="Transparent"` + `Background="Transparent"` + `CanResize="False"` + `ShowActivated="False"` + `WindowStartupLocation="Manual"`; shadow -> `Border.BoxShadow` (`"0 2 12 0 #66000000"`); positioned via `Window.Position` (`PixelPoint`, PHYSICAL px) — convert DIP->physical through `Screen.Scaling` (see `ToastPlacement`); `Window.Height` stays DIP (Avalonia `Height` is logical). Mouse events: `MouseEnter`->`PointerEntered`, `MouseLeave`->`PointerExited`, `MouseLeftButtonDown`->`PointerPressed` with a left-button guard (rule 10) |
| 43 | `Window.StateChanged` event (minimize->hide) | Avalonia has no `StateChanged`. Override `OnPropertyChanged` and act when `change.Property == Window.WindowStateProperty` (or subscribe `this.GetObservable(WindowStateProperty)`). Concrete recipe: `OnPropertyChanged` |
| 44 | `Window.Closing` shows a synchronous `ShowDialog()` and reads the result inline (the close-action prompt) | Avalonia `Closing` (`WindowClosingEventArgs`) is synchronous and cannot await `ShowDialog<T>`. Pattern: set `e.Cancel = true`, kick an async method that `await`s `ShowDialog<T>`, and on an Exit choice set `_forceClose = true` and call `Close()` again (the guard makes the re-entrant close bypass the prompt). The direct (non-prompt) `CloseAction` branches do their `Hide()`/`return` inline |
| 45 | Global new-window dark chrome: `EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, ...)` + `new WindowInteropHelper(window).EnsureHandle()`; `DwmSetWindowAttribute`/`SetWindowPos`/`WM_NCACTIVATE` P/Invokes; bounce at `DispatcherPriority.ContextIdle` | `Control.LoadedEvent.AddClassHandler<Window>((w,_) => Apply(w, IsDark))` (Avalonia routed-event class handler — the direct analog); HWND via `window.TryGetPlatformHandle()?.Handle`; the DWM/`user32` P/Invokes port verbatim (Win32, framework-agnostic); bounce via `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`. The theme-applier is the sole writer of the cached `IsDark` preference (design Phase 1-gate note) |

---

## Task 1: GO/NO-GO probe — toast Window mechanics + DIP<->physical placement + WPF toast reference shot

**Files:**
- Create: `src/CSUploader.Avalonia/Lib/UI/ToastPlacement.cs`
- Create: `tests/CSUploader.Avalonia.Tests/Lib/ToastPlacementTests.cs`
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml` (add `ToastProbeButton`) + `GalleryWindow.axaml.cs` (throwaway probe handler — deleted in Task 2)
- Modify: `src/Services/ReferenceShotCapture.cs` (WPF head; add a `--toast` sub-mode inside `#if DEBUG`)

**Interfaces:**
- Produces: `CSUploader.Lib.UI.ToastPlacement` — `static DipRect WorkAreaToDip(PixelRect physicalWorkArea, double scaling)`, `static PixelPoint DipToPhysical(double dipLeft, double dipTop, double scaling)`. Consumed by `AvaloniaToastHost` (Task 2) and the DI `workAreaProvider` (Task 3).
- Consumes: `CSUploader.Services.DipRect` (Core, `src/CSUploader.Core/Services/DipRect.cs`).

The design flags toast positioning/click-through/topmost/ShowActivated at Win10+Win11 DPI as GO/NO-GO. This task pins the risky Window API surface (Reality-check items 1-3), builds the pure DIP math with tests, drives a throwaway chrome-less topmost non-activated probe window through the bridge to prove no focus-steal + topmost + bottom-right placement, and captures the WPF toast reference shot (shot-driver-first). Record the verdict + recipe (or the ShowActivated fallback).

- [x] **Step 1: Pin the Window API surface (Reality-check 1-3) via ILSpy.** Confirm on the installed Avalonia 11.3.18 bits: `Window.ShowActivated` (bool), `Window.SystemDecorations` (`SystemDecorations.None`), `Window.TransparencyLevelHint`, `Window.Position` (`PixelPoint`), `Window.Screens` (`Screens`), `Screens.Primary`/`ScreenFromWindow(Window)` (`Screen?`), `Screen.WorkingArea` (`PixelRect`), `Screen.Scaling` (double), `Border.BoxShadow` syntax. Record each in the Reality-check register with the concrete member. If `ShowActivated` is ABSENT, record the fallback recipe (after `host.Show()`, re-activate the MainWindow: `desktop.MainWindow?.Activate()`).

- [x] **Step 2: Write the failing `ToastPlacement` tests.**

```csharp
// tests/CSUploader.Avalonia.Tests/Lib/ToastPlacementTests.cs
using Avalonia; // PixelPoint, PixelRect
using CSUploader.Lib.UI;
using CSUploader.Services; // DipRect

namespace CSUploader.Tests.Avalonia.Lib;

public class ToastPlacementTests
{
    [Fact]
    public void DipToPhysical_ScalesAndRounds()
    {
        // 100.4*1.5 = 150.6 -> 151 ; 200.6*1.5 = 300.9 -> 301
        Assert.Equal(new PixelPoint(151, 301), ToastPlacement.DipToPhysical(100.4, 200.6, 1.5));
    }

    [Fact]
    public void WorkAreaToDip_DividesByScaling()
    {
        DipRect d = ToastPlacement.WorkAreaToDip(new PixelRect(0, 0, 2880, 1620), 1.5);
        Assert.Equal(0, d.X);
        Assert.Equal(0, d.Y);
        Assert.Equal(1920, d.Width);
        Assert.Equal(1080, d.Height);
        Assert.Equal(1920, d.Right);   // X + Width
        Assert.Equal(1080, d.Bottom);  // Y + Height
    }

    [Fact]
    public void ZeroOrNegativeScaling_TreatedAsUnity()
    {
        Assert.Equal(new PixelPoint(10, 20), ToastPlacement.DipToPhysical(10, 20, 0));
        DipRect d = ToastPlacement.WorkAreaToDip(new PixelRect(5, 6, 100, 200), -1);
        Assert.Equal(100, d.Width);
    }
}
```

- [x] **Step 3: Run — verify it fails.** PowerShell: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter ToastPlacementTests`. Expected: FAIL (`ToastPlacement` does not exist).

- [x] **Step 4: Implement `ToastPlacement`.**

```csharp
// src/CSUploader.Avalonia/Lib/UI/ToastPlacement.cs
// <copyright file="ToastPlacement.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia; // PixelPoint, PixelRect
using CSUploader.Services; // DipRect

namespace CSUploader.Lib.UI;

/// <summary>
/// Pure conversions between the toast service's DIP geometry (design: ALL toast geometry is in DIPs)
/// and Avalonia's physical-pixel Window.Position / Screen.WorkingArea. The WPF head needed neither
/// (WPF Top/Left and SystemParameters.WorkArea are already DIPs).
/// </summary>
public static class ToastPlacement
{
    /// <summary>Converts a screen's physical work area to DIPs (design DipRect / ToastNotificationService).</summary>
    public static DipRect WorkAreaToDip(PixelRect physicalWorkArea, double scaling)
    {
        if (scaling <= 0)
        {
            scaling = 1.0;
        }

        return new DipRect(
            physicalWorkArea.X / scaling,
            physicalWorkArea.Y / scaling,
            physicalWorkArea.Width / scaling,
            physicalWorkArea.Height / scaling);
    }

    /// <summary>Converts a DIP top-left to a physical PixelPoint for Window.Position.</summary>
    public static PixelPoint DipToPhysical(double dipLeft, double dipTop, double scaling)
    {
        if (scaling <= 0)
        {
            scaling = 1.0;
        }

        return new PixelPoint(
            (int)Math.Round(dipLeft * scaling),
            (int)Math.Round(dipTop * scaling));
    }
}
```

- [x] **Step 5: Run — verify it passes.** Same filter. Expected: PASS (3 tests).

- [x] **Step 6: Add the WPF toast reference-shot mode** to `src/Services/ReferenceShotCapture.cs`. Add a `--toast` branch at the top of `RunAndShutdownAsync` (mirroring the existing `--wizard`/`--settings` argv dispatch, so `App.xaml.cs` stays untouched — the one-WPF-file rule), and a `RunToastShotsAndShutdownAsync` that shows the WPF `ToastWindow` with a synthesized `ToastViewModel` per theme and captures `toast-<light|dark>-wpf.png`.

```csharp
// In RunAndShutdownAsync, alongside the existing --wizard / --settings checks:
if (Array.IndexOf(argv, "--toast") >= 0)
{
    await RunToastShotsAndShutdownAsync(dir);
    return;
}
```

```csharp
/// <summary>
/// DEBUG-only toast reference-shot mode (--shots --toast): shows the WPF ToastWindow with a synthesized
/// ToastViewModel, light + dark, and captures its card under the shots convention
/// (toast-light-wpf.png / toast-dark-wpf.png) — the WPF reference cell the Avalonia toast port (Task 2)
/// arbitrates against. No network, no upload.
/// </summary>
public async Task RunToastShotsAndShutdownAsync(string dir)
{
    Directory.CreateDirectory(dir);
    await Task.Delay(1500); // let the app settle (no MainViewModel hydration needed for a toast)

    IThemeApplier theme = services.GetRequiredService<IThemeApplier>();

    foreach (bool dark in (bool[])[false, true])
    {
        theme.ApplyTheme(dark);
        string suffix = dark ? "dark" : "light";

        var vm = new ViewModels.ToastViewModel(
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }),
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }))
        {
            Title = Localizer.Instance["Toast_FileCompleted_Title"],
            Message = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Toast_FileCompleted_Body"], "holiday_clip.mkv"),
            IconKey = "StatusSuccessImage",
        };
        ToastWindow toast = new(vm) { Left = 200, Top = 200 };
        toast.Show();
        await WaitForRenderAsync(toast);
        CaptureWindow(toast, Path.Combine(dir, $"toast-{suffix}-wpf.png"));
        toast.Close();
    }

    Application.Current.Shutdown();
}
```

- [x] **Step 7: Capture the WPF toast reference shots.** PowerShell: build the WPF head to `D:\temp2\cbuild-mig\wpf`, then run it with `--shots --toast D:\temp2\cbuild-mig\shots`. Verify `toast-light-wpf.png` and `toast-dark-wpf.png` exist and show the accent-striped card. NOTE for the Task 8 arbitration: `ReferenceShotCapture.CaptureWindow` renders `root.ActualWidth/Height` — i.e. the toast Border, NOT the window — so the WPF cell CLIPS the `DropShadowEffect` and looks tighter/shadowless; the ava side is a full-window bridge screenshot with the `BoxShadow` visible. That difference is expected framing, not a regression.

- [x] **Step 8: Add the throwaway bridge probe.** In `GalleryWindow.axaml` add `<Button x:Name="ToastProbeButton" Content="Toast probe" />`; in `GalleryWindow.axaml.cs` wire `ToastProbeButton.Click += OnToastProbe` with a handler that shows a bare chrome-less topmost non-activated window bottom-right using `ToastPlacement` + `Screens.Primary`:

```csharp
// THROWAWAY (Task 1 GO/NO-GO probe — deleted in Task 2 when the real ToastWindow lands).
private void OnToastProbe(object? sender, RoutedEventArgs e)
{
    var screen = Screens.Primary; // Screen (element type) is Avalonia.Platform; var avoids importing it
    double scaling = screen?.Scaling ?? 1.0;
    DipRect work = screen is null
        ? new DipRect(0, 0, 1920, 1080)
        : ToastPlacement.WorkAreaToDip(screen.WorkingArea, scaling);

    var probe = new Window
    {
        Width = 360,
        Height = 80,
        SystemDecorations = SystemDecorations.None,
        Background = Brushes.Transparent,
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
        ShowInTaskbar = false,
        Topmost = true,
        CanResize = false,
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Content = new Border { Background = Brushes.CornflowerBlue, Child = new TextBlock { Text = "toast probe" } },
    };
    probe.Position = ToastPlacement.DipToPhysical(work.Right - 360 - 12, work.Bottom - 80 - 12, scaling);
    probe.Show();
}
```

- [x] **Step 9: Drive the probe through the bridge; record the GO/NO-GO verdict.** Launch the Avalonia head `--agent --gallery`; via `ava-drive`, click `ToastProbeButton`; `ava_screenshot` (maxWidth 2500) shows the blue card at the bottom-right of the shell; `ava_props` on the MainWindow/gallery confirms it stayed active (no focus steal from the non-activated toast). Record in Reality-check item 1: GO (ShowActivated works; topmost; bottom-right placement correct at the dev machine's DPI) or the fallback. Note: the dev machine is Win11; 125%/150% placement fidelity and the Win10 chrome path are maintainer-verified (section Open questions).

- [x] **Step 10: Suite gate + commit.** Both suites (Avalonia +3, WPF unchanged), both heads build 0-warning Debug.

```bash
git add src/CSUploader.Avalonia/Lib/UI/ToastPlacement.cs tests/CSUploader.Avalonia.Tests/Lib/ToastPlacementTests.cs src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs src/Services/ReferenceShotCapture.cs
git commit -m "feat(avalonia): Phase 7 Task 1 - toast placement math + GO/NO-GO probe + WPF toast reference shot"
```

---

## Task 2: Avalonia ToastWindow + AvaloniaToastHost + AvaloniaToastWindowFactory (gallery-verified)

**Files:**
- Create: `src/CSUploader.Avalonia/Views/ToastWindow.axaml` + `ToastWindow.axaml.cs`
- Create: `src/CSUploader.Avalonia/Services/AvaloniaToastHost.cs`
- Create: `src/CSUploader.Avalonia/Services/AvaloniaToastWindowFactory.cs`
- Create: `tests/CSUploader.Avalonia.Tests/Views/ToastWindowTests.cs`
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml` (+`.axaml.cs`) — replace the Task 1 throwaway `ToastProbeButton` with a real `ToastButton` that builds a toast via `AvaloniaToastWindowFactory`

**Interfaces:**
- Consumes: `IToastHost`, `IToastWindowFactory`, `ToastViewModel` (Core); `ToastPlacement` (Task 1).
- Produces: `CSUploader.Views.ToastWindow` (ctors `ToastWindow()` [loader] and `ToastWindow(ToastViewModel)`); `CSUploader.Services.AvaloniaToastHost : IToastHost`; `CSUploader.Services.AvaloniaToastWindowFactory : IToastWindowFactory` (public, DI-registered in Task 3). `AvaloniaToastHost.Height` returns the window's DIP height; `Top`/`Left` setters store DIPs and drive `Window.Position` (physical, via `ToastPlacement` + primary-screen `Scaling`).

- [x] **Step 1: Create `ToastWindow.axaml`** (port of `src/Views/ToastWindow.xaml`, rule 42). `SurfaceBrush`/`AccentBrush`/`TextPrimaryBrush` and `ResourceKeyToImageConverter` are app-scoped (`App.axaml` lines 15/21), so no local resource declarations are needed.

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:CSUploader.ViewModels;assembly=CSUploader.Core"
        x:Class="CSUploader.Views.ToastWindow"
        x:DataType="vm:ToastViewModel"
        Width="360" Height="80"
        SystemDecorations="None"
        TransparencyLevelHint="Transparent"
        Background="Transparent"
        ShowInTaskbar="False"
        Topmost="True"
        CanResize="False"
        ShowActivated="False"
        WindowStartupLocation="Manual"
        PointerPressed="OnBodyPressed"
        PointerEntered="OnPointerEntered"
        PointerExited="OnPointerExited">
  <Border CornerRadius="6"
          Background="{DynamicResource SurfaceBrush}"
          BorderBrush="{DynamicResource AccentBrush}"
          BorderThickness="1"
          Margin="6"
          BoxShadow="0 2 12 0 #66000000">
    <Grid ColumnDefinitions="4,36,*,20">
      <Rectangle Grid.Column="0" Fill="{DynamicResource AccentBrush}" />
      <Image Grid.Column="1" Width="24" Height="24"
             VerticalAlignment="Center" HorizontalAlignment="Center"
             Source="{Binding IconKey, Converter={StaticResource ResourceKeyToImageConverter}}" />
      <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="6,0,4,0">
        <TextBlock Text="{Binding Title}" FontWeight="Bold"
                   Foreground="{DynamicResource TextPrimaryBrush}" TextTrimming="CharacterEllipsis" />
        <TextBlock Text="{Binding Message}" Foreground="{DynamicResource TextPrimaryBrush}"
                   TextTrimming="CharacterEllipsis" Margin="0,2,0,0" />
      </StackPanel>
      <Button Grid.Column="3" Content="X" Width="20" Height="20"
              Margin="0,4,4,0" VerticalAlignment="Top" HorizontalAlignment="Right"
              Background="Transparent" BorderThickness="0"
              Foreground="{DynamicResource TextPrimaryBrush}"
              Cursor="Hand" Command="{Binding CloseCommand}" Click="OnCloseClicked" />
    </Grid>
  </Border>
</Window>
```

Note: the WPF close-button glyph is the Unicode multiplication-X character; copy it verbatim from `src/Views/ToastWindow.xaml:66` (shown as `X` above only to keep this plan file ASCII).

- [x] **Step 2: Create `ToastWindow.axaml.cs`** (port of `src/Views/ToastWindow.xaml.cs`; `DispatcherTimer` auto-dismiss; hover pauses; body-click activates; close-button closes).

```csharp
// <copyright file="ToastWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// Avalonia bottom-right completion-toast window (port of the WPF ToastWindow, rule 42). Auto-dismisses
/// after 5s; hovering pauses the timer; a body click runs ActivateCommand; the close button runs CloseCommand.
/// Positioned by AvaloniaToastHost via Window.Position (physical px).
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);
    private readonly DispatcherTimer? _dismissTimer;
    private readonly ToastViewModel? _viewModel;

    // Loader/design-time ctor (AVLN3001); the app always uses the VM overload. DataContext stays null here.
    public ToastWindow()
    {
        InitializeComponent();
    }

    public ToastWindow(ToastViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoDismissDelay };
        _dismissTimer.Tick += OnDismissTick;

        // WPF used Loaded -> Start; Avalonia's Window lifecycle event is Opened.
        Opened += (_, _) => _dismissTimer.Start();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void OnDismissTick(object? sender, EventArgs e)
    {
        _dismissTimer!.Stop();
        Close();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => _dismissTimer?.Stop();

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _dismissTimer?.Stop();
        _dismissTimer?.Start();
    }

    private void OnBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left-button guard (rule 10). The close button handles its own press, so a close click does NOT
        // bubble to this window handler (Avalonia stops bubbling at a handled event); OnCloseClicked's
        // e.Handled is belt-and-braces parity with the WPF stop-propagation.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && _viewModel?.ActivateCommand.CanExecute(null) == true)
        {
            _viewModel.ActivateCommand.Execute(null);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
```

- [x] **Step 3: Create `AvaloniaToastWindowFactory`.**

```csharp
// <copyright file="AvaloniaToastWindowFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of IToastWindowFactory — builds a ToastWindow behind an AvaloniaToastHost,
/// the mirror of the WPF head's DefaultToastWindowFactory.
/// </summary>
public sealed class AvaloniaToastWindowFactory : IToastWindowFactory
{
    public IToastHost Create(ToastViewModel viewModel)
    {
        ToastWindow window = new(viewModel);
        return new AvaloniaToastHost(window);
    }
}
```

- [x] **Step 4: Create `AvaloniaToastHost`.** The one Avalonia-specific twist over `ToastWindowHost`: `Top`/`Left` are DIPs (what the service computes), driven onto `Window.Position` (physical) via `ToastPlacement` and the primary screen's scaling — matching the primary-work-area `workAreaProvider` (Task 3). Primary-monitor-only, exactly as the WPF head.

```csharp
// <copyright file="AvaloniaToastHost.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CSUploader.Lib.UI;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Adapts ToastWindow to IToastHost. Top/Left are DIPs (the service computes the stack in DIPs); this host
/// converts to physical Window.Position via the PRIMARY screen's Scaling — the same primary screen the DI
/// workAreaProvider reads, so the two agree. Primary-monitor-only, matching the WPF head's SystemParameters.WorkArea.
/// </summary>
internal sealed class AvaloniaToastHost : IToastHost
{
    private readonly ToastWindow _window;
    private double _dipTop;
    private double _dipLeft;

    public AvaloniaToastHost(ToastWindow window)
    {
        _window = window;
        _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    // ToastWindow.Height is the fixed DIP height (Avalonia Height is logical/DIP) — what the service stacks from.
    public double Height => _window.Height;

    public double Top
    {
        get => _dipTop;
        set
        {
            _dipTop = value;
            ApplyPosition();
        }
    }

    public double Left
    {
        get => _dipLeft;
        set
        {
            _dipLeft = value;
            ApplyPosition();
        }
    }

    public event EventHandler? Closed;

    public void Show() => _window.Show();

    public void Close() => _window.Close();

    private void ApplyPosition()
    {
        double scaling = ResolvePrimaryScaling();
        _window.Position = ToastPlacement.DipToPhysical(_dipLeft, _dipTop, scaling);
    }

    private static double ResolvePrimaryScaling()
    {
        Window? main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return main?.Screens?.Primary?.Scaling ?? 1.0;
    }
}
```

- [x] **Step 5: Write the failing `ToastWindowTests`.**

```csharp
// tests/CSUploader.Avalonia.Tests/Views/ToastWindowTests.cs
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Services;
using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

public class ToastWindowTests
{
    private static ToastViewModel MakeVm(Action? activate = null, Action? close = null)
        => new(new RelayCommand(activate ?? (() => { })), new RelayCommand(close ?? (() => { })))
        {
            Title = "Upload finished",
            Message = "holiday_clip.mkv",
            IconKey = "StatusSuccessImage",
        };

    [AvaloniaFact]
    public void Factory_CreatesHost_HeightIsWindowDipHeight()
    {
        IToastHost host = new AvaloniaToastWindowFactory().Create(MakeVm());
        try
        {
            Assert.Equal(80, host.Height); // ToastWindow Height="80" (DIP)
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void CloseCommand_RunsAndWindowCloses()
    {
        bool closed = false;
        var vm = MakeVm(close: () => closed = true);
        var w = new ToastWindow(vm);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            vm.CloseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void ActivateCommand_Runs()
    {
        bool activated = false;
        var vm = MakeVm(activate: () => activated = true);
        var w = new ToastWindow(vm);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            vm.ActivateCommand.Execute(null);
            Assert.True(activated);
        }
        finally
        {
            w.Close();
        }
    }
}
```

- [x] **Step 6: Run — red then green.** Write the test first; watch it fail on the missing `ToastWindow`/factory; implement Steps 1-4; watch it pass. Filter `ToastWindowTests`.

- [x] **Step 7: Replace the Task 1 throwaway with the real gallery button.** In `GalleryWindow.axaml` rename `ToastProbeButton` -> `ToastButton` (Content "Show toast"); in `GalleryWindow.axaml.cs` delete `OnToastProbe` and add:

```csharp
// Builds a real toast via the production factory + host and shows it bottom-right, so the bridge
// screenshots the exact ToastWindow the notification listener raises (Task 2).
private void OnShowToast(object? sender, RoutedEventArgs e)
{
    var screen = Screens.Primary; // Screen (element type) is Avalonia.Platform; var avoids importing it
    double scaling = screen?.Scaling ?? 1.0;
    DipRect work = screen is null
        ? new DipRect(0, 0, 1920, 1080)
        : ToastPlacement.WorkAreaToDip(screen.WorkingArea, scaling);

    var vm = new ToastViewModel(new RelayCommand(() => { }), new RelayCommand(() => { }))
    {
        Title = Localizer.Instance["Toast_FileCompleted_Title"],
        Message = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Toast_FileCompleted_Body"], "holiday_clip.mkv"),
        IconKey = "StatusSuccessImage",
    };
    IToastHost host = new AvaloniaToastWindowFactory().Create(vm);
    host.Left = work.Right - 360 - 12;
    host.Top = work.Bottom - 80 - 12;
    host.Show();
}
```

Wire `ToastButton.Click += OnShowToast;` in the ctor (`using CommunityToolkit.Mvvm.Input;`).

- [x] **Step 8: Capture the Avalonia toast reference shots via the bridge.** Launch `--agent --gallery`; drive `ToastButton`; `ava_screenshot` the toast bottom-right in light+dark -> `toast-light-ava.png` / `toast-dark-ava.png`. Confirm the toast does not steal focus.

- [x] **Step 9: Suite gate + commit.** Both suites (Avalonia +3), both heads build 0-warning Debug + Release.

```bash
git add src/CSUploader.Avalonia/Views/ToastWindow.axaml src/CSUploader.Avalonia/Views/ToastWindow.axaml.cs src/CSUploader.Avalonia/Services/AvaloniaToastHost.cs src/CSUploader.Avalonia/Services/AvaloniaToastWindowFactory.cs tests/CSUploader.Avalonia.Tests/Views/ToastWindowTests.cs src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs
git commit -m "feat(avalonia): Phase 7 Task 2 - ToastWindow + AvaloniaToastHost + factory (gallery-verified, DIP->physical)"
```

**Task 2 executed (2026-07-14) — pending reviewer gate.** ToastWindow (`.axaml`+`.axaml.cs`), `AvaloniaToastHost`, `AvaloniaToastWindowFactory` shipped; the Task 1 throwaway `ToastProbeButton`/`OnToastProbe` is DELETED and replaced with the real production `ToastButton`/`OnShowToast` (builds a toast via the factory+host). Gates: Avalonia **389 → 395** (+6), WPF **1200/1200** (untouched — zero Core/WPF changes), head **0-warning Debug AND Release**. Bridge (`--agent --gallery`, scratch DB): `toast-light-ava.png` + `toast-dark-ava.png` captured; the real ToastWindow at physical (2188,1287) = work.Right−360−12, work.Bottom−80−12 → exact bottom-right of the primary work area at 100% scaling; **no-focus-steal RE-CONFIRMED on the REAL ToastWindow** (`ava_windows`: toast `isActive:false`, gallery stayed `isActive:true` in both themes); the prior toast auto-dismissed live (5s). Arbitration vs the WPF ref cells: **parity match** (blue `AccentBrush` #2563EB stripe/border — identical hex both heads, the green is only the success checkmark; bold title + message + ✕). The ava cell shows the BoxShadow; the WPF cell clips its DropShadow (the recorded framing note — expected, not a regression). **Deviations (recorded):** (1) the close glyph is written as the `&#x2715;` XML numeric entity, not the literal U+2715 character — the repo's a11y edit hook rejects non-ASCII bytes in the write payload; the entity renders the identical glyph, a source-representation choice with no runtime behavior change. (2) **+6 tests, not the plan's +3** — the plan's 3 (`Factory_CreatesHost_HeightIsWindowDipHeight`, `CloseCommand_RunsAndWindowCloses`, `ActivateCommand_Runs`) PLUS the team-lead-requested non-vacuous coverage: `Host_TopLeftDip_WriteThroughToWindowPosition_ViaPlacement` (positioning-math delegation — host Top/Left DIP → Window.Position via `ToastPlacement`, scaling 1.0 under headless) and two auto-dismiss/expiry tests (`AutoDismiss_ArmsOnOpen_PausesOnHover_ResumesOnLeave`, `AutoDismiss_StopsOnClose`). The expiry tests required three `internal` test seams on ToastWindow — `IsAutoDismissRunning`, `PauseAutoDismiss()`, `RestartAutoDismiss()` (the pointer-enter/exit handlers now delegate to the latter two; same behavior as the plan's inline bodies) — because headless does not advance a 5s `DispatcherTimer` and `PointerEventArgs` cannot be synthesized peer-side; this matches the codebase's established InternalsVisibleTo test-seam pattern (`AvaloniaDialogService`/`MessageBoxWindow`). Step 9's "+3" note is therefore superseded by "+6". (3) removed the now-orphaned `using Avalonia.Media;` from `GalleryWindow.axaml.cs` (only the deleted probe used `Brushes`). No plan-code semantic deviations in the window/host/factory themselves.

---

## Task 3: Wire the real `ToastNotificationService` — completion toasts live

**Files:**
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (`ConfigureServices` — replace the NoOp registration with the real service + factory)
- Delete: `src/CSUploader.Avalonia/Services/NoOpToastNotificationService.cs`
- Create: `tests/CSUploader.Avalonia.Tests/Services/AvaloniaToastWiringTests.cs`

**Interfaces:**
- Consumes: `ToastNotificationService` (Core), `AvaloniaToastWindowFactory` (Task 2), `ToastPlacement` (Task 1), `MainViewModel.ActivateAndShowUploadedTab` (Core), `IUiDispatcher`, `AppSettings`, `Screen`/`Screens`.
- Produces: the composed provider now resolves `IToastNotificationService` to the real `ToastNotificationService`; `UploadNotificationListener` (eagerly resolved in `WireRuntime`) now raises real toasts on `FileState.Completed` / package completion.

- [x] **Step 1: Write the failing wiring test.**

```csharp
// tests/CSUploader.Avalonia.Tests/Services/AvaloniaToastWiringTests.cs
using System.IO;
using CSUploader.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Tests.Avalonia.Services;

public class AvaloniaToastWiringTests
{
    [Fact]
    public void ToastService_ResolvesToRealService_NotNoOp()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "csu-toast-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var services = new ServiceCollection();
            App.ConfigureServices(services, baseDir);
            using ServiceProvider sp = services.BuildServiceProvider();

            Assert.IsType<ToastNotificationService>(sp.GetRequiredService<IToastNotificationService>());
            Assert.IsType<AvaloniaToastWindowFactory>(sp.GetRequiredService<IToastWindowFactory>());
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
```

- [x] **Step 2: Run — verify it fails** (`IToastNotificationService` still resolves to `NoOpToastNotificationService`; `IToastWindowFactory` is unregistered).

- [x] **Step 3: Replace the DI registration** in `App.axaml.cs` `ConfigureServices`. Remove `services.AddSingleton<IToastNotificationService, NoOpToastNotificationService>();` and add (mirror the WPF `App.xaml.cs:95-105`, with the Avalonia `Screens`->DIP `workAreaProvider`):

```csharp
services.AddSingleton<IToastWindowFactory, AvaloniaToastWindowFactory>();
services.AddSingleton<IToastNotificationService>(sp => new ToastNotificationService(
    sp.GetRequiredService<AppSettings>(),
    sp.GetRequiredService<IToastWindowFactory>(),
    workAreaProvider: () =>
    {
        // Primary-screen work area in DIPs (design: ALL toast geometry is in DIPs). MainWindow is shown
        // by the time any toast fires, so its Screens.Primary is valid; fall back on the rare null.
        Window? main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var screen = main?.Screens?.Primary; // Screen (element type) is Avalonia.Platform; var avoids importing it
        return screen is null
            ? new DipRect(0, 0, 1920, 1080)
            : Lib.UI.ToastPlacement.WorkAreaToDip(screen.WorkingArea, screen.Scaling);
    },
    activate: () => sp.GetRequiredService<MainViewModel>().ActivateAndShowUploadedTab(),
    dispatcher: sp.GetRequiredService<IUiDispatcher>()));
```

`Window` and `IClassicDesktopStyleApplicationLifetime` (`Avalonia.Controls` / `Avalonia.Controls.ApplicationLifetimes`) are already imported at the top of `App.axaml.cs`. The `Screens` collection is reached only as a member (`main.Screens`), and the primary screen is read via `var` — so its element type `Screen`, which lives in **`Avalonia.Platform`** (NOT `Avalonia.Controls` — verified by reflection on 11.3.18), is never named and NO extra `using` is required. (If you prefer an explicit `Screen?` decl, add `using Avalonia.Platform;` to this file and the two GalleryWindow sites instead.)

- [x] **Step 4: Delete `NoOpToastNotificationService.cs`.** `git rm src/CSUploader.Avalonia/Services/NoOpToastNotificationService.cs`.

- [x] **Step 5: Run — verify green.** Filter `AvaloniaToastWiringTests`; then the full Avalonia suite (the DI smoke test still builds the graph without a cycle — the `activate`/`workAreaProvider` lambdas defer `MainViewModel`/`MainWindow` resolution, so no ctor cycle).

- [x] **Step 6: Live-verify completion toasts via the bridge (no real upload).** Launch `--agent --gallery`. The `--agent` guard pauses the scheduler, so drive the notification path directly: via `ava_eval`, resolve `IToastNotificationService` from `((App)Application.Current).Services` and call `ShowFileCompleted` on a synthesized `PackageFile` (or reuse the seed's completed rows) -> confirm a real toast appears bottom-right and stacks with a second call, and clicking it switches to the Uploaded tab (`ActivateAndShowUploadedTab`). Record.

- [x] **Step 7: Suite gate + commit.** Both suites (Avalonia +1), both heads 0-warning Debug + Release. Avalonia Release launched WITHOUT flags shows no gallery/probe surface.

```bash
git add src/CSUploader.Avalonia/App.axaml.cs tests/CSUploader.Avalonia.Tests/Services/AvaloniaToastWiringTests.cs
git rm src/CSUploader.Avalonia/Services/NoOpToastNotificationService.cs
git commit -m "feat(avalonia): Phase 7 Task 3 - wire real ToastNotificationService; completion toasts live; drop NoOp"
```

**Task 3 executed (2026-07-14) — pending reviewer gate.** `App.ConfigureServices` now registers `IToastWindowFactory` -> `AvaloniaToastWindowFactory` and `IToastNotificationService` -> the real Core `ToastNotificationService` (WPF `App.xaml.cs:95-105` shape, with the Avalonia `Screens`->DIP `workAreaProvider` closure verbatim from the plan). `NoOpToastNotificationService.cs` is `git rm`-ed; `grep -rn NoOpToastNotificationService src/` -> zero. Completion toasts are live: `WireRuntime` eagerly resolves `UploadNotificationListener` (verified chain unchanged), which now raises the real `ToastNotificationService` on `FileState.Completed` / package completion. Gates: Avalonia **395 -> 397** (+2), WPF **1200/1200** (untouched — zero Core/WPF changes), both heads **0-warning** (Avalonia Debug AND Release; WPF Debug). **Live verification without a real upload:** the current bridge has **no `ava_eval`/method-invoke tool** (tool set is `ava_action`/`ava_dispatch`(ICommand-on-DataContext)/`ava_vm`(read-only)/...), so the plan's Step 6 "resolve `IToastNotificationService` and call `ShowFileCompleted`" could not be driven from the bridge. Instead: launched the composed `--agent --gallery` head (scratch DB beside the exe at `D:\temp2\cbuild-mig\ava`) — it **booted cleanly with the real service wired + NoOp deleted** (`ava_windows`: MainWindow + Gallery up; `ava_logs` Error/Warning: only the pre-existing `Package.StartedDate` DataGrid binding warnings, unrelated to toasts — no DI/composition/wiring error), which exercises the real startup path (`OnFrameworkInitializationCompleted` -> `WireRuntime` -> eager `UploadNotificationListener` -> real `IToastNotificationService` factory lambda). The gallery `ToastButton` then rendered a real `ToastWindow` (360x80) at physical **(2188,1287)** = work.Right-360-12, work.Bottom-80-12, `isActive:false` with the gallery still `isActive:true` (no focus-steal) — identical to Task 2's recorded placement. The DI-wiring correctness itself (real service resolved; `workAreaProvider` invokes to the documented non-degenerate fallback rect) is proven by the two headless tests below (the gallery button builds via the factory directly, so it re-confirms render, not the wired service). **Deviations (recorded):** (1) **+2 tests, not the plan's +1.** The plan's `ToastService_ResolvesToRealService_NotNoOp` (`[Fact]`, DI resolution) PLUS the team-lead-requested non-vacuous `workAreaProvider`-shape coverage: `WorkAreaProvider_InvokesCleanly_ReturnsNonDegenerateDipRect` (`[AvaloniaFact]`) pulls the wired `Func<DipRect>` off the resolved real service by its unique field type and invokes it, asserting the documented headless fallback `DipRect(0,0,1920,1080)`. Reflection is used because the closure is private to the DI factory and is not observable through any public seam under the headless lifetime, and an end-to-end toast fire would leak an uncloseable ownerless toast window (no `IClassicDesktopStyleApplicationLifetime.Windows` to snapshot/close, rule 25); the closure's math itself is already covered by Task 1's `ToastPlacementTests`. Step 7's "+1" is therefore superseded by "+2" (Avalonia 397). (2) The registration adds a trailing `// real bottom-right completion toasts (Phase 7)` comment on the `AddSingleton<IToastNotificationService>` line (the NoOp line's inline comment relocated); no code-semantic deviation from the plan's wiring block.

---

## Task 4: Balloon->toast routing (`ShowInfo` + tray `NotifyHidden`)

**Files:**
- Modify: `src/CSUploader.Core/Services/IToastNotificationService.cs` (add `ShowInfo`) — the one Core touch this phase; see section Open questions
- Modify: `src/CSUploader.Core/Services/ToastNotificationService.cs` (implement `ShowInfo`)
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaTrayIconService.cs` (inject `IToastNotificationService`; implement `NotifyHidden`)
- Modify: `tests/Services/ToastNotificationServiceTests.cs` (WPF/shared suite — add a `ShowInfo` test using the existing fakes)
- Create: `tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs`

**Interfaces:**
- Produces: `IToastNotificationService.ShowInfo(string title, string body)` — raises a general info toast that is NOT gated on `ShowCompletionToasts` (it is a tray-discovery notice, not a completion). `ToastNotificationService.ShowInfo` posts `ShowToast(title, body, "StatusRunningImage")` on the dispatcher.
- Consumes (tray): `IToastNotificationService`, `Localizer` (`Tray_Balloon_Title`/`Tray_Balloon_Body`).

- [x] **Step 1: Add the Core `ShowInfo` test (WPF/shared suite, `tests/Services/ToastNotificationServiceTests.cs`)** using the file's existing fake factory + inline dispatcher fakes (open the file and reuse whatever names it defines — do NOT introduce new fakes if the file has them):

```csharp
[Fact]
public void ShowInfo_RaisesToast_EvenWhenCompletionToastsDisabled()
{
    // ShowInfo is the tray "still running" route — it must fire regardless of the completion-toast gate.
    var settings = new AppSettings { ShowCompletionToasts = false };
    var factory = new FakeToastWindowFactory(); // the existing test fake in this file
    var service = new ToastNotificationService(
        settings, factory,
        workAreaProvider: () => new DipRect(0, 0, 1920, 1080),
        activate: () => { },
        dispatcher: new InlineUiDispatcher()); // the existing inline dispatcher fake

    service.ShowInfo("CSUploader", "Still running in the tray.");

    Assert.Single(factory.Created); // a toast was built despite ShowCompletionToasts=false
}
```

Adapt the fake type/property names to whatever `ToastNotificationServiceTests.cs` already declares.

- [x] **Step 2: Run — verify it fails** (`ShowInfo` not on the interface).

- [x] **Step 3: Add `ShowInfo` to `IToastNotificationService`** (Core):

```csharp
/// <summary>
/// Raises a general-purpose informational toast (title + body). Unlike the completion methods this is
/// NOT gated on AppSettings.ShowCompletionToasts — it is a tray-discovery notice, not an upload completion.
/// The Avalonia head routes the "still running in the tray" tip here (design section Tray balloon tip:
/// Avalonia's TrayIcon has no balloon API); the WPF head keeps its native NotifyIcon balloon and does not call this.
/// </summary>
void ShowInfo(string title, string body);
```

- [x] **Step 4: Implement `ShowInfo` in `ToastNotificationService`** (Core) — reuses the private `ShowToast`, no `ShowCompletionToasts` check:

```csharp
public void ShowInfo(string title, string body) =>
    dispatcher.Post(() => ShowToast(title, body, "StatusRunningImage"));
```

- [x] **Step 5: Run — verify green** in the WPF/shared suite (1200 -> 1201). The WPF head's `ToastNotificationService` (same Core class) now has `ShowInfo`; nothing in the WPF head calls it.

- [x] **Step 6: Write the failing tray test** (Avalonia suite):

```csharp
// tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

public class AvaloniaTrayIconServiceTests
{
    private sealed class RecordingToasts : IToastNotificationService
    {
        public int InfoCount { get; private set; }
        public void ShowFileCompleted(PackageFile file) { }
        public void ShowPackageCompleted(Package package, int succeeded, int total) { }
        public void ShowInfo(string title, string body) => InfoCount++;
    }

    [Fact]
    public void NotifyHidden_ShowsInfoToast_OncePerSession()
    {
        var toasts = new RecordingToasts();
        var svc = new AvaloniaTrayIconService(new AppSettings(), Mock.Of<IAppLogger>(), toasts);

        svc.NotifyHidden();
        svc.NotifyHidden(); // second hide in the same session is silent

        Assert.Equal(1, toasts.InfoCount);
    }

    [Fact]
    public void NotifyHidden_AfterDispose_DoesNothing()
    {
        var toasts = new RecordingToasts();
        var svc = new AvaloniaTrayIconService(new AppSettings(), Mock.Of<IAppLogger>(), toasts);
        svc.Dispose();

        svc.NotifyHidden();

        Assert.Equal(0, toasts.InfoCount);
    }
}
```

- [x] **Step 7: Run — verify it fails** (`AvaloniaTrayIconService` has no toast ctor param; `NotifyHidden` is a no-op).

- [x] **Step 8: Update `AvaloniaTrayIconService`.** Add the ctor dependency + the first-hide guard + the `NotifyHidden` body:

```csharp
public sealed class AvaloniaTrayIconService(AppSettings settings, IAppLogger logger, IToastNotificationService toasts)
    : IDisposable, ITrayIconService
{
    // ... existing fields ...
    private bool _firstHideTipShown;
```

```csharp
/// <summary>
/// Shows the one-shot "we're in the tray" notice the first time the window hides this session. Avalonia's
/// TrayIcon has no balloon API, so this routes through the app's own toast system (design section Tray
/// balloon tip) — consistent styling, same i18n keys. The flag isn't persisted; every fresh process gets one tip.
/// </summary>
public void NotifyHidden()
{
    if (_disposed || _firstHideTipShown)
    {
        return;
    }

    _firstHideTipShown = true;
    toasts.ShowInfo(
        Localizer.Instance["Tray_Balloon_Title"],
        Localizer.Instance["Tray_Balloon_Body"]);
}
```

(No DI change is required in `App.axaml.cs` — `AddSingleton<AvaloniaTrayIconService>()` resolves ctor deps from the container, and `IToastNotificationService` is registered in Task 3. Confirm the head builds.)

- [x] **Step 9: Run — verify green** (Avalonia suite +2). DI smoke test still builds (no cycle: `AvaloniaTrayIconService` -> `IToastNotificationService`; the toast service does not depend on `ITrayIconService`).

- [x] **Step 10: Suite gate + commit.** Both suites (WPF 1201, Avalonia +2), both heads 0-warning Debug + Release.

```bash
git add src/CSUploader.Core/Services/IToastNotificationService.cs src/CSUploader.Core/Services/ToastNotificationService.cs src/CSUploader.Avalonia/Services/AvaloniaTrayIconService.cs tests/Services/ToastNotificationServiceTests.cs tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs
git commit -m "feat(avalonia): Phase 7 Task 4 - balloon->toast routing (Core ShowInfo + tray NotifyHidden)"
```

**Task 4 executed (2026-07-14) — pending reviewer gate.** The one sanctioned Core touch landed exactly minimal: `IToastNotificationService.ShowInfo(string title, string body)` (interface line + doc) + `ToastNotificationService.ShowInfo` (`=> dispatcher.Post(() => ShowToast(title, body, "StatusRunningImage"))`, UNGATED by `ShowCompletionToasts`, reusing the private `ShowToast`). `git diff phase6-hard-views-ready..HEAD -- src/CSUploader.Core/` = those 2 files ONLY, 14 insertions / 0 deletions, ONLY the `ShowInfo` member. `AvaloniaTrayIconService` gained the `IToastNotificationService toasts` ctor dep + a `_firstHideTipShown` field + the `NotifyHidden` body; **first-hide guard mirrors WPF `TrayIconManager.NotifyHidden` (`src/Services/TrayIconManager.cs:58-78`)**: `if (_disposed || _firstHideTipShown) return; _firstHideTipShown = true; toasts.ShowInfo(Tray_Balloon_Title, Tray_Balloon_Body);` — once per session, flag not persisted (every fresh process gets one tip). Icon key = `StatusRunningImage` (recorded arbitration item, plan's choice kept). **Interface interlock:** the only concrete implementer is Core `ToastNotificationService` (updated); the sole other reference is `Mock<IToastNotificationService>` (Moq auto-implements the new void). Gates: WPF/shared **1200 -> 1201** (+1: `ShowInfo_RaisesToast_EvenWhenCompletionToastsDisabled`, adapted to the file's existing `FakeToastWindowFactory`/`InlineUiDispatcher`), Avalonia **397 -> 399** (+2: `NotifyHidden_ShowsInfoToast_OncePerSession`, `NotifyHidden_AfterDispose_DoesNothing`, non-vacuous via `RecordingToasts.InfoCount`), both heads **0-warning Debug AND Release** (Core changed -> BOTH heads built). **Live-verification (bridge has no method-invoke tool — Task 3 finding; plan prescribes no Task 4 bridge drive):** the Avalonia DI smoke test (`AvaloniaStartupDISmokeTests.ConfigureServices_ResolvesAllHeadRegistrationsAndViewModels`, part of the 399) resolves `ITrayIconService` -> `AvaloniaTrayIconService` (now carrying the `IToastNotificationService` dep) AND `IToastNotificationService` from the REAL composed provider, proving the tray->toast wiring composes with no cycle; the 2 tray tests prove the routing + once-per-session + post-dispose guards; the shared test proves the ungated route fires despite `ShowCompletionToasts=false`. **Deviations (recorded):** (1) the Avalonia `NotifyHidden` drops the WPF original's `_notifyIcon is null` guard AND its `try/catch`+log — deliberate per the plan: the toast route is independent of the tray-icon handle (`ShowInfo` posts to the dispatcher and cannot depend on `_trayIcon`), and it is only ever called from the close-to-tray path where an icon exists anyway. (2) No `App.axaml.cs` DI edit — `AddSingleton<AvaloniaTrayIconService>()` resolves the new ctor dep from the container (`IToastNotificationService` registered in Task 3), confirmed by the DI smoke test. **Deviation for Tasks 5+ (HARNESS, important):** PowerShell's cwd is the MAIN tree `E:\Projects\CSUploader\CSUploader`, NOT the worktree — relative-path `dotnet build/test` silently builds the maintainer's tree (which lacks the worktree's changes, so builds falsely pass and "up-to-date" checks mislead). ALWAYS pass the ABSOLUTE worktree csproj path (`E:\Projects\CSUploader\CSUploader-avalonia\...`) to every PowerShell build/test, or `Set-Location` to the worktree first.

---

## Task 5: File/View/Help menu bar on MainWindow + handlers

**Files:**
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml` (Grid + Menu + TabControl)
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml.cs` (`_forceClose` field + `MenuExit_Click`, `MenuCheckForUpdates_Click`, `MenuAbout_Click`)
- Create: `tests/CSUploader.Avalonia.Tests/Views/MainWindowMenuTests.cs`

**Interfaces:**
- Consumes: `MainViewModel` (`WindowTitle`, `SelectedTabIndex`, `UploadsViewModel.ShowUploadOverview`, `ThemeMenuLabel`, `ToggleThemeCommand`, `InstallUpdateCommand`, `IsUpdateAvailable`, `AvailableVersion`, `CheckForUpdatesAsync`), `MessageBoxWindow.ShowErrorAsync` (existing internal static), `AboutWindow`, `Localizer`.
- Produces: `MainWindow._forceClose` (private field, consumed by Task 6's `Closing` handler); the three menu `Click` handlers.

- [x] **Step 1: Restructure `MainWindow.axaml`** — wrap the TabControl in a `Grid` with a `Menu` row (port of `src/Views/MainWindow.xaml:19-62`, rule 41: `IsCheckable`->`ToggleType="CheckBox"`, gesture text dropped, `NativeMenu` NOT used):

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:loc="clr-namespace:CSUploader.Lib.Localization"
        xmlns:views="clr-namespace:CSUploader.Views"
        x:Class="CSUploader.Views.MainWindow"
        Title="{Binding WindowTitle}"
        Width="1024" Height="800"
        WindowStartupLocation="CenterScreen"
        Icon="/Assets/icon.ico">
  <Grid RowDefinitions="Auto,*">
    <Menu Grid.Row="0" Background="{DynamicResource SurfaceBrush}" FontSize="12">
      <MenuItem Header="{loc:Loc Main_Menu_File}">
        <MenuItem Header="{loc:Loc Main_Menu_File_Exit}" Click="MenuExit_Click" />
      </MenuItem>
      <MenuItem Header="{loc:Loc Main_Menu_View}">
        <MenuItem Header="{loc:Loc Main_Menu_View_UploadOverview}"
                  ToggleType="CheckBox"
                  IsChecked="{Binding UploadsViewModel.ShowUploadOverview}" />
        <Separator />
        <MenuItem Header="{Binding ThemeMenuLabel}" Command="{Binding ToggleThemeCommand}" />
      </MenuItem>
      <MenuItem Header="{loc:Loc Main_Menu_Help}">
        <MenuItem Header="{loc:Loc Main_Menu_Help_CheckForUpdates}" Click="MenuCheckForUpdates_Click" />
        <MenuItem Header="{loc:Loc Main_Menu_Help_InstallUpdate}"
                  Command="{Binding InstallUpdateCommand}"
                  IsEnabled="{Binding IsUpdateAvailable}" />
        <Separator />
        <MenuItem Header="{loc:Loc Main_Menu_Help_About}" Click="MenuAbout_Click" />
      </MenuItem>
    </Menu>

    <TabControl Grid.Row="1" SelectedIndex="{Binding SelectedTabIndex}">
      <TabItem Header="{loc:Loc Main_Tab_Uploads}"><views:UploadsView DataContext="{Binding UploadsViewModel}" /></TabItem>
      <TabItem Header="{loc:Loc Main_Tab_Uploaded}"><views:UploadedView DataContext="{Binding UploadedViewModel}" /></TabItem>
      <TabItem Header="{loc:Loc Main_Tab_Settings}"><views:SettingsView DataContext="{Binding SettingsViewModel}" /></TabItem>
      <TabItem Header="{loc:Loc Main_Tab_Logs}"><views:LogsView DataContext="{Binding LogsViewModel}" /></TabItem>
    </TabControl>
  </Grid>
</Window>
```

- [x] **Step 2: Add the code-behind handlers + `_forceClose`** to `MainWindow.axaml.cs` (port of `src/Views/MainWindow.xaml.cs:133-158`; the update-result box uses `MessageBoxWindow.ShowErrorAsync`, the head's OK-notification — parity with WPF `MessageBox.Show(Information)`, icon dropped per the head's message-box divergence). This is the Task-5 shape; Task 6 adds the DI ctor + Closing to the same class.

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class MainWindow : Window
{
    // Set when the user (menu Exit) or the close-to-tray Exit choice really wants to quit, bypassing
    // the close-to-tray rerouting in OnClosing (Task 6). Mirrors the WPF MainWindow._forceClose.
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.CheckForUpdatesAsync();
            string message = vm.IsUpdateAvailable
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Instance["Main_CheckForUpdates_Available_Format"], vm.AvailableVersion)
                : Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"];
            await MessageBoxWindow.ShowErrorAsync(this, message, Localizer.Instance["Main_CheckForUpdates_DialogTitle"]);
        }
    }

    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
```

- [x] **Step 3: Write the failing menu tests.**

```csharp
// tests/CSUploader.Avalonia.Tests/Views/MainWindowMenuTests.cs
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

public class MainWindowMenuTests
{
    [AvaloniaFact]
    public void MainWindow_HasFileViewHelpMenu_AndFourTabs()
    {
        // DataContext left null: {loc:Loc} headers resolve without one; the {Binding} items still exist.
        var w = new MainWindow();
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu? menu = w.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
            Assert.NotNull(menu);
            Assert.Equal(3, menu!.Items.Count); // File / View / Help

            TabControl? tabs = w.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            Assert.NotNull(tabs);
            Assert.Equal(4, tabs!.Items.Count); // Uploads / Uploaded / Settings / Logs (the restructure kept them)
        }
        finally
        {
            w.Close();
        }
    }
}
```

- [x] **Step 4: Run — red then green** (write the test first, watch it fail on the missing menu, implement Steps 1-2, watch it pass). Filter `MainWindowMenuTests`.

- [x] **Step 5: Re-capture the Avalonia `mainwindow-uploads` shots (now with the menu).** Against the seeded app (`--agent`, seeded scratch DB): `ava_screenshot` the Uploads tab light+dark -> overwrite `mainwindow-uploads-light-ava.png` / `mainwindow-uploads-dark-ava.png`. The WPF `mainwindow-uploads-*-wpf.png` already shows the menu (no re-capture). Compare the menu-bar strip against the WPF cell for the Fluent-vs-WPF density arbitration (recorded at Task 8).

- [x] **Step 6: Suite gate + commit.** Both suites (Avalonia +1), both heads 0-warning Debug + Release.

```bash
git add src/CSUploader.Avalonia/Views/MainWindow.axaml src/CSUploader.Avalonia/Views/MainWindow.axaml.cs tests/CSUploader.Avalonia.Tests/Views/MainWindowMenuTests.cs
git commit -m "feat(avalonia): Phase 7 Task 5 - File/View/Help menu bar + handlers (theme toggle, updates, about)"
```

**Task 5 executed (2026-07-14) — pending reviewer gate.** `MainWindow.axaml` restructured into a `Grid RowDefinitions="Auto,*"` with the in-window `<Menu>` on row 0 (rule 41: `NativeMenu` NOT used; `IsCheckable`->`ToggleType="CheckBox"`; the `Main_Menu_File_Exit_Gesture` "Alt+F4" `InputGestureText` DROPPED) and the TabControl on row 1; three handlers + staged `_forceClose` added to `MainWindow.axaml.cs` (`MenuExit_Click`, `MenuCheckForUpdates_Click` -> `MessageBoxWindow.ShowErrorAsync`, `MenuAbout_Click` -> `AboutWindow.ShowDialog`). Gates: Avalonia **399 -> 404** (+5), WPF/shared **1201/1201** (untouched — diff is ONLY the 3 Task-5 files; zero Core/WPF/resx), Avalonia head **0-warning Debug AND Release**. Bridge (`--agent`, seeded scratch DB via `ava-drive.cs` direct-TCP driver — the MCP server is not loaded this session): re-captured `mainwindow-uploads-{light,dark}-ava.png` (maxWidth 2500) WITH the menu bar; theme flipped via `ava_dispatch {"ref":"ref_1","command":"ToggleThemeCommand"}`. Parity vs the WPF ref cells: **close match** — same File/View/Help strip, same left-alignment, same 12px font, same `SurfaceBrush` background, identical tabs/toolbar/grid/Upload-Overview; the only divergences are the known Fluent-vs-WPF theming (accent-underline active tab; marginally more Fluent menu-item padding) arbitrated at Task 8. `ava_logs` Error/Warning after driving: ONLY the pre-existing `Package.StartedDate` DataGrid binding warnings (same as Task 3) — no menu/theme/binding error from Task 5. **Deviations (recorded):** (1) **`Mode=TwoWay` added** to the Upload-Overview `IsChecked="{Binding UploadsViewModel.ShowUploadOverview}"` — the plan's literal XAML omits it, but Avalonia's `MenuItem.IsCheckedProperty` defaults to **OneWay** (unlike WPF's `BindsTwoWayByDefault`), so without it the View-menu checkbox reads the VM but never writes back (the panel wouldn't hide on toggle) — a functional regression vs WPF. Caught by the `UI->VM` half of the two-way test. Rule 41 mandates the two-way binding, so the explicit mode is a necessary correction, not a semantic change. (2) **`#pragma warning disable CS0414`** wraps the `_forceClose` field: the plan stages Exit as `_forceClose = true; Close();` in Task 5 but the READER (the `Closing` reroute) doesn't land until Task 6, so the assigned-but-unread field trips CS0414 and breaks the 0-warning gate. The pragma is scoped to the one field and carries a Task-6 comment; **Task 6's full-class rewrite (its Step 3) drops the pragma naturally** when it adds the reader. Exit staged EXACTLY as the plan prescribes (`_forceClose = true; Close();`); verified headlessly (`MenuExit_Click_ClosesWindow` test — never clicked File->Exit in the bridge, which would kill the drive session). (3) **+5 tests, not the plan's +1.** The plan's `MainWindow_HasFileViewHelpMenu_AndFourTabs` PLUS four team-lead-requested non-vacuous tests: `Menu_SubItemStructure_MatchesWpf_WithCheckableOverviewAndSeparators` (WPF sub-item shape + the sole `CheckBox` toggle + the two Separators), `UploadOverviewMenuItem_TwoWayBinds_And_View_Help_CommandsBound` (the checkable two-way BOTH directions + View/Help `Command` bindings resolve to the VM commands), `MenuExit_Click_ClosesWindow` (Exit wiring closes the window — the Task 5 staging), `MenuCheckForUpdates_WithoutMainViewModel_NoOps` (the `DataContext is MainViewModel` guard). Step 6's "+1" is superseded by "+5" (Avalonia 404). **Coverage limit (recorded for the reviewer):** the update-check POSITIVE path (compose `Available_Format`/`AlreadyLatest` -> modal `MessageBoxWindow.ShowErrorAsync(this,...)`) is NOT driven end-to-end — a full drive needs a real `MainViewModel` (whose `CheckForUpdatesAsync` hits GitHub, non-deterministic in CI) and closing a modal opened *inside* the static seam with no headless window-enumeration handle (the codebase never enumerates windows in headless tests). It is covered instead by: the guard test, the handler being a verbatim port of the WPF `MenuCheckForUpdates_Click` (`src/Views/MainWindow.xaml.cs:139-152`), and `MessageBoxWindow.ShowErrorAsync`'s own modal tests (`MessageBoxWindowTests`). (4) Test-double is a partial fake exposing only the asserted members (dropped `ThemeMenuLabel`/`WindowTitle` to avoid CA1822 — their Header/Title bindings resolve to null, untested). No other semantic deviations from the plan's code.

---

## Task 6: Close / minimize-to-tray behavior

**Files:**
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml.cs` (DI ctor + `Closing` + `OnPropertyChanged` WindowState watch + async close-action prompt + persistence)
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (construct `MainWindow` through the new ctor)
- Create: `tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs`

**Interfaces:**
- Consumes: `AppSettings` (`CloseAction`, `MinimizeToTray`), `ITrayIconService` (`UpdateVisibility`, `NotifyHidden`), `SettingRepository` (`FindByKeyAsync`/`InsertAsync`/`UpdateAsync`), `SettingKey.CloseAction`, `CloseActionDialog`/`CloseActionChoice` (Phase 4 port), `CloseAction` enum, `Logger.Current`.
- Produces: `MainWindow(AppSettings, ITrayIconService, SettingRepository)` internal ctor (used by `App.axaml.cs`); reuses `_forceClose` from Task 5.

- [x] **Step 1: Write the failing close-to-tray tests.** (`StubRepo()` reuses the in-memory Sqlite `SettingRepository` harness from `tests/CSUploader.Avalonia.Tests/Lib/DataGridColumnPersistenceTests.cs:26-56` — copy that `SqliteConnection(":memory:")` + `TestDbContextFactory` + `EnsureCreated` snippet into a local helper.)

```csharp
// tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

public class MainWindowCloseToTrayTests
{
    [AvaloniaFact]
    public void Close_WithMinimizeToTray_ReroutesToTray_NotClosed()
    {
        var settings = new AppSettings { CloseAction = CloseAction.MinimizeToTray };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            w.Close(); // triggers Closing -> MinimizeToTray reroute (e.Cancel = true)
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);            // hidden, not closed
            tray.Verify(t => t.UpdateVisibility(), Times.AtLeastOnce);
            tray.Verify(t => t.NotifyHidden(), Times.Once);
        }
        finally
        {
            settings.CloseAction = CloseAction.Exit; // let the finally Close() actually close
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public void Close_WithExit_ActuallyCloses()
    {
        var settings = new AppSettings { CloseAction = CloseAction.Exit };
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, Mock.Of<ITrayIconService>(), repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            w.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(w.IsVisible);
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public void Minimize_WithMinimizeToTray_Hides()
    {
        var settings = new AppSettings { MinimizeToTray = true, CloseAction = CloseAction.Exit };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            w.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);
            tray.Verify(t => t.UpdateVisibility(), Times.AtLeastOnce);
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    private static (SettingRepository, SqliteConnection) StubRepo()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(conn).Options;
        var factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        return (new SettingRepository(factory), conn);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
```

- [x] **Step 2: Run — verify it fails** (no `MainWindow(AppSettings, ITrayIconService, SettingRepository)` ctor; no reroute).

- [x] **Step 3: Extend `MainWindow.axaml.cs`** with the DI ctor, `Closing`, WindowState watch, async prompt, and persistence (port of `src/Views/MainWindow.xaml.cs:25-131`, rules 43 + 44). Merge with the Task 5 handlers so there is ONE `_forceClose` and ONE parameterless ctor. Full class:

```csharp
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings? _settings;
    private readonly ITrayIconService? _tray;
    private readonly SettingRepository? _settingRepo;
    private bool _forceClose;

    // Loader/design-time ctor (AVLN3001); the menu tests use this too (DataContext null).
    public MainWindow()
    {
        InitializeComponent();
    }

    // Production ctor: App.axaml.cs supplies the services close/minimize-to-tray needs.
    internal MainWindow(AppSettings settings, ITrayIconService tray, SettingRepository settingRepo)
    {
        _settings = settings;
        _tray = tray;
        _settingRepo = settingRepo;
        InitializeComponent();

        Closing += MainWindow_Closing;
    }

    // Avalonia has no StateChanged event (rule 43): react to WindowState via OnPropertyChanged.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty
            && WindowState == WindowState.Minimized
            && _settings is { MinimizeToTray: true })
        {
            Hide();
            _tray?.UpdateVisibility();
        }
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || _settings is null)
        {
            return;
        }

        switch (_settings.CloseAction)
        {
            case CloseAction.Exit:
                return;

            case CloseAction.MinimizeToTray:
                e.Cancel = true;
                Hide();
                _tray?.UpdateVisibility();
                _tray?.NotifyHidden();
                return;

            case CloseAction.Ask:
            default:
                // Rule 44: Closing can't await ShowDialog. Cancel, prompt async, re-close on Exit.
                e.Cancel = true;
                _ = PromptCloseActionAsync();
                return;
        }
    }

    private async Task PromptCloseActionAsync()
    {
        CloseActionChoice? choice = await new CloseActionDialog().ShowDialog<CloseActionChoice?>(this);
        if (choice is not { } result)
        {
            return; // cancelled — keep the window open, setting unchanged.
        }

        if (result.Remember && _settings is not null)
        {
            _settings.CloseAction = result.Action;
            await PersistCloseActionAsync(result.Action);
        }

        if (result.Action == CloseAction.MinimizeToTray)
        {
            // Parity with the WPF Ask->Minimize branch: hide + refresh, NO first-hide balloon here.
            Hide();
            _tray?.UpdateVisibility();
            return;
        }

        // Exit: bypass the reroute and really close.
        _forceClose = true;
        Close();
    }

    private async Task PersistCloseActionAsync(CloseAction chosen)
    {
        if (_settingRepo is null)
        {
            return;
        }

        try
        {
            string value = chosen.ToString();
            SettingDto? existing = await _settingRepo.FindByKeyAsync(SettingKey.CloseAction);
            if (existing is null)
            {
                await _settingRepo.InsertAsync(new SettingDto { Key = SettingKey.CloseAction, Value = value });
            }
            else
            {
                existing.Value = value;
                await _settingRepo.UpdateAsync(existing);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the in-memory AppSettings already updated, so the choice applies this session.
            Logger.Current.Log(this, LogType.Error, $"Failed to persist close action: {ex.Message}");
        }
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.CheckForUpdatesAsync();
            string message = vm.IsUpdateAvailable
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Instance["Main_CheckForUpdates_Available_Format"], vm.AvailableVersion)
                : Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"];
            await MessageBoxWindow.ShowErrorAsync(this, message, Localizer.Instance["Main_CheckForUpdates_DialogTitle"]);
        }
    }

    private async void MenuAbout_Click(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
```

- [x] **Step 4: Wire the new ctor in `App.axaml.cs`.** Replace `Views.MainWindow mainWindow = new() { DataContext = ... };` with:

```csharp
Views.MainWindow mainWindow = new(
    _serviceProvider.GetRequiredService<AppSettings>(),
    _serviceProvider.GetRequiredService<ITrayIconService>(),
    _serviceProvider.GetRequiredService<Dal.SettingRepository>())
{
    DataContext = _serviceProvider.GetRequiredService<MainViewModel>(),
};
```

(`AppSettings` is already resolved for the agent guard a few lines above; `SettingRepository` is Core.Dal — use the fully-qualified `Dal.SettingRepository` as shown or add `using CSUploader.Dal;`.)

- [x] **Step 5: Run — verify green** (Avalonia suite +3). DI smoke still builds.

- [x] **Step 6: Bridge-verify** (optional visual): launch the seeded app `--agent`; minimize -> window hides + tray appears (seed `MinimizeToTray=true` or toggle in Settings). Clicking the tray restores. Close with `CloseAction=Ask` -> the ported `CloseActionDialog` opens. Record.

- [x] **Step 7: Suite gate + commit.** Both suites (Avalonia +3), both heads 0-warning Debug + Release.

```bash
git add src/CSUploader.Avalonia/Views/MainWindow.axaml.cs src/CSUploader.Avalonia/App.axaml.cs tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs
git commit -m "feat(avalonia): Phase 7 Task 6 - close/minimize-to-tray (async close-action prompt, WindowState watch)"
```

**Task 6 executed (2026-07-14) — pending reviewer gate.** `MainWindow.axaml.cs` was FULL-CLASS-REWRITTEN (plan Step 3, not incremental): the DI ctor `internal MainWindow(AppSettings, ITrayIconService, SettingRepository)` (wires `Closing`), the parameterless loader/test ctor (wires NO reroute — keeps the Task 5 menu tests green), `OnPropertyChanged(WindowStateProperty)` minimize->hide watch (rule 43), the `Closing` reroute (rule 44: Exit returns; MinimizeToTray cancels+Hide+UpdateVisibility+**NotifyHidden**; Ask cancels + kicks the async prompt), `PromptCloseActionAsync` (awaits the modal `CloseActionDialog.ShowDialog<CloseActionChoice?>` then applies), `PersistCloseActionAsync` (SettingRepository upsert, best-effort), and the three Task-5 menu handlers. The **CS0414 pragma on `_forceClose` is GONE** — the rewrite's `MainWindow_Closing` now READS the field (`grep pragma MainWindow.axaml.cs` -> none). `App.axaml.cs` constructs MainWindow through the new 3-arg ctor (resolving `AppSettings`/`ITrayIconService`/`Dal.SettingRepository`; DataContext object-initializer unchanged). Gates: Avalonia **404 -> 411** (+7), WPF/shared **1201/1201** (untouched — `git diff` is ONLY App.axaml.cs + MainWindow.axaml.cs + the new test; zero Core/WPF/resx), Avalonia head **0-warning clean Debug build** (pragma gone, nothing re-declared). **NotifyHidden call-site discipline (mirrors WPF `src/Views/MainWindow.xaml.cs` EXACTLY):** fires on the DIRECT `MinimizeToTray` `Closing` branch ONLY (WPF line 76); NOT on the `Ask`->Minimize branch (WPF lines 95-100 hide without a balloon), NOT on the WindowState minimize watch (WPF `MainWindow_StateChanged` lines 51-58 hide without a balloon). **Bridge (`--agent`, seeded scratch DB beside the head):** launched the composed head — it stayed UP (`ava_windows`: MainWindow "CSUploader" 1024x800, isMain) which exercises the REAL startup path constructing MainWindow through the new 3-arg ctor (a bad resolve would crash boot); `ava_logs` Warning: ONLY the pre-existing `Package.StartedDate` DataGrid binding warnings (same as Tasks 3/5) — no DI/composition/close-to-tray error. The destructive close/minimize paths were **deliberately NOT driven** (a close kills the drive session; a minimize-to-tray hide strands it behind the native tray the bridge can't click) — they are fully headless-covered (below); split recorded per the team-lead constraint. **Deviations (recorded):** (1) **+7 tests, not the plan's +3.** The plan's 3 (`Close_WithMinimizeToTray_ReroutesToTray_NotClosed`, `Close_WithExit_ActuallyCloses`, `Minimize_WithMinimizeToTray_Hides`) — the last STRENGTHENED with a `NotifyHidden` `Times.Never` assertion (the pinned WindowState-minimize no-balloon discipline) — PLUS four team-lead-requested non-vacuous Closing-matrix tests: `AskExit_WithRemember_PersistsCloseAction_AndCloses` (Ask->Exit persists to AppSettings AND the DB, closes), `AskMinimize_NoRemember_HidesWithoutBalloon_AndDoesNotPersist` (Ask->Minimize hides + `UpdateVisibility` once + **`NotifyHidden` `Times.Never`** + not persisted + AppSettings stays `Ask`), `AskCancelled_KeepsWindowOpen_AndDoesNotPersist` (null choice = window stays open, nothing touched), `ForceClose_ViaMenuExit_BypassesMinimizeToTrayReroute` (File->Exit `_forceClose` closes outright with `CloseAction=MinimizeToTray`, tray reroute `Times.Never` — drives the REAL `Closing` handler via the Exit menu item's `RaiseEvent(MenuItem.ClickEvent)`, the Task-5 pattern). Step 7's "+3" is superseded by "+7" (Avalonia 411). (2) **`ApplyCloseActionChoiceAsync(CloseActionChoice?)` test seam:** the plan inlines the post-dialog decision inside `PromptCloseActionAsync`; the rewrite factors that decision into an `internal` awaitable method (`PromptCloseActionAsync` = `await dialog` then `await ApplyCloseActionChoiceAsync(choice)`). Behaviour is byte-identical; the seam lets the Ask outcomes be driven headlessly and deterministically (Avalonia.Headless can't click a modal `ShowDialog`, and the `Closing` Ask branch is fire-and-forget `_ = PromptCloseActionAsync()` — not awaitable from a test) — matching the codebase's established InternalsVisibleTo seam pattern (`AvaloniaDialogService`/`MessageBoxWindow`/`ToastWindow`). (3) **Dropped the plan's redundant `using System;`** from the rewrite — `System` comes from `ImplicitUsings` and the repo's `.editorconfig` sets `IDE0005` (unnecessary using) to `warning`, which would break the 0-warning gate; `Exception`/`Task` still resolve. No other semantic deviations from the plan's code. **Coverage limit (for the reviewer):** the literal `new CloseActionDialog().ShowDialog<CloseActionChoice?>(this)` modal line inside `PromptCloseActionAsync` is not driven end-to-end (a modal `ShowDialog` can't be clicked under Avalonia.Headless); it is covered by the `ApplyCloseActionChoiceAsync` seam (the decision it feeds), `CloseActionDialog`'s own Phase-4 dialog tests, and the verbatim WPF-port shape.

---

## Task 7: Dark-title-bar Win10 fallback + theme-applier sole writer

**Files:**
- Create: `src/CSUploader.Avalonia/Lib/UI/AvaloniaImmersiveDarkMode.cs`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaThemeApplier.cs` (`ApplyTheme` also calls `SetIsDark`)
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (`RegisterGlobalHandler()` once at startup)
- Create: `tests/CSUploader.Avalonia.Tests/Lib/AvaloniaImmersiveDarkModeTests.cs`
- Modify: `tests/CSUploader.Avalonia.Tests/Theming/ThemeTests.cs` (restore `AvaloniaImmersiveDarkMode.IsDark` in the `ApplyTheme_*` finally, now that `ApplyTheme` mutates the global cache)

**Interfaces:**
- Produces: `CSUploader.Lib.UI.AvaloniaImmersiveDarkMode` — `static bool IsDark`, `static void RegisterGlobalHandler()` (idempotent; registers a `Control.LoadedEvent` class handler on `Window`), `static void SetIsDark(bool)` (updates the cache + reapplies to all open windows), `static void Apply(Window, bool)` (per-window DWM write, best-effort).
- Consumes (applier): `AvaloniaImmersiveDarkMode.SetIsDark`. Consumes (App): `AvaloniaImmersiveDarkMode.RegisterGlobalHandler`.

- [x] **Step 1: Pin Reality-check 4-7** via ILSpy on Avalonia 11.3.18: `Control.LoadedEvent` is a `RoutedEvent` exposing `AddClassHandler<TTarget>(Action<TTarget, TEventArgs>)`; `Window.TryGetPlatformHandle()` returns `IPlatformHandle?` with `.Handle` (`IntPtr`); `DispatcherPriority.Background` exists; `WindowClosingEventArgs` was pinned in Task 6. If `AddClassHandler` is absent, record the fallback (subscribe `Window.Opened` in `App.axaml.cs` for the main window only; dialogs accept the Win10 light-chrome divergence). The Win10 DWM visual is maintainer-verified — the dev machine is Win11 (auto-recolors from the variant).

- [x] **Step 2: Write the failing cache tests.**

```csharp
// tests/CSUploader.Avalonia.Tests/Lib/AvaloniaImmersiveDarkModeTests.cs
using CSUploader.Lib.UI;

namespace CSUploader.Tests.Avalonia.Lib;

public class AvaloniaImmersiveDarkModeTests
{
    [Fact]
    public void SetIsDark_UpdatesCache()
    {
        bool original = AvaloniaImmersiveDarkMode.IsDark;
        try
        {
            AvaloniaImmersiveDarkMode.SetIsDark(true);
            Assert.True(AvaloniaImmersiveDarkMode.IsDark);

            AvaloniaImmersiveDarkMode.SetIsDark(false);
            Assert.False(AvaloniaImmersiveDarkMode.IsDark);
        }
        finally
        {
            AvaloniaImmersiveDarkMode.SetIsDark(original);
        }
    }

    [Fact]
    public void RegisterGlobalHandler_IsIdempotent()
    {
        // Registering twice must not throw or double-register (guarded by a static flag).
        AvaloniaImmersiveDarkMode.RegisterGlobalHandler();
        AvaloniaImmersiveDarkMode.RegisterGlobalHandler();
    }
}
```

- [x] **Step 3: Run — verify it fails** (`AvaloniaImmersiveDarkMode` does not exist).

- [x] **Step 4: Implement `AvaloniaImmersiveDarkMode`** (port of `src/Lib/UI/ImmersiveDarkMode.cs`, rule 45 — Win32 P/Invokes verbatim; HWND via `TryGetPlatformHandle`; class handler via `Control.LoadedEvent.AddClassHandler`; bounce via `Dispatcher.UIThread.Post`).

```csharp
// <copyright file="AvaloniaImmersiveDarkMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CSUploader.Lib.UI;

/// <summary>
/// Toggles the Windows "immersive dark mode" title-bar attribute on top-level Avalonia windows so the
/// non-client title bar matches the in-app dark theme on Windows 10 (Win11 recolors it automatically from
/// the ThemeVariant). Direct port of the WPF head's ImmersiveDarkMode (rule 45): the DWM/user32 P/Invokes
/// are framework-agnostic; only the HWND acquisition (Window.TryGetPlatformHandle) and the global new-window
/// hook (Control.LoadedEvent class handler) are Avalonia-idiomatic. AvaloniaThemeApplier is the SOLE writer
/// of IsDark (design Phase 1-gate note).
/// </summary>
public static class AvaloniaImmersiveDarkMode
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WmNcActivate = 0x0086;

    private static bool _registered;

    /// <summary>Current dark-mode preference; updated via SetIsDark, read by the Loaded class handler.</summary>
    public static bool IsDark { get; private set; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Registers a class handler on Control.LoadedEvent so every Window (existing and future) picks
    /// up the current theme without opting in — the Avalonia analog of WPF's EventManager.RegisterClassHandler.
    /// Idempotent.</summary>
    public static void RegisterGlobalHandler()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        Control.LoadedEvent.AddClassHandler<Window>((window, _) => Apply(window, IsDark));
    }

    /// <summary>Updates the cached preference and reapplies to every currently open window. Called from the
    /// theme-applier's theme-change path (the sole writer).</summary>
    public static void SetIsDark(bool dark)
    {
        IsDark = dark;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (Window window in desktop.Windows)
        {
            Apply(window, dark);
        }
    }

    /// <summary>Applies dark/light immersive title bar to a single window. Best-effort — no-ops (harmlessly)
    /// where the HWND is unavailable (headless) or the OS predates the attribute.</summary>
    public static void Apply(Window window, bool dark)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            {
                return;
            }

            int value = dark ? 1 : 0;

            // Write BOTH attribute ids (the WPF comment: some Win10 1909 DWMs accept 20 with HRESULT 0 but
            // do nothing, others reject it — writing 19 and 20 lands the right value on every DWM).
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));

            // Force DWM to re-query the immersive attribute on the next NC repaint (older Win10 DWMs cache the
            // frame until the window loses/regains NC-active). Scheduled off the current call so the OS
            // activation sequence for a just-shown modal lands first (the WPF ContextIdle bounce -> Post).
            Dispatcher.UIThread.Post(
                () =>
                {
                    _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpFrameChanged | SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate);
                    _ = SendMessage(hwnd, WmNcActivate, IntPtr.Zero, new IntPtr(-1));
                    _ = SendMessage(hwnd, WmNcActivate, new IntPtr(1), new IntPtr(-1));
                },
                DispatcherPriority.Background);
        }
        catch
        {
            // Best-effort: pre-Win10 returns E_INVALIDARG; the title bar just stays the OS default.
        }
    }
}
```

- [x] **Step 5: Run — verify green** (`AvaloniaImmersiveDarkModeTests`). Under headless, `SetIsDark`'s loop no-ops (no classic-desktop lifetime) and `Apply`'s `TryGetPlatformHandle` returns null -> no-op; only the cache changes, which the tests assert.

- [x] **Step 6: Make `AvaloniaThemeApplier` the sole writer.** In `ApplyTheme`, after setting `RequestedThemeVariant`, add the `SetIsDark` call:

```csharp
public void ApplyTheme(bool isDark)
{
    Application? app = Application.Current;
    if (app is null)
    {
        return;
    }

    app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

    // Win11 recolors the title bar from the variant automatically; on Win10 the DWM P/Invoke is the fallback.
    // This applier is the SOLE writer of the cached new-window dark-chrome preference (design Phase 1-gate
    // note) — mirrors WpfThemeApplier.ApplyTheme -> ImmersiveDarkMode.SetIsDark.
    Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(isDark);
}
```

- [x] **Step 7: Register the global handler at startup.** In `App.axaml.cs` `OnFrameworkInitializationCompleted`, just before `Views.MainWindow mainWindow = new(...)`, add (mirror `src/App.xaml.cs:43`):

```csharp
// Register the global Window.Loaded class handler so every window picks up the dark title bar automatically
// (Win10 fallback; Win11 auto-recolors). MainViewModel.InitializeAsync sets the initial IsDark via
// IThemeApplier.ApplyTheme once the persisted setting is read.
Lib.UI.AvaloniaImmersiveDarkMode.RegisterGlobalHandler();
```

- [x] **Step 8: Update the existing `ThemeTests`.** In `ThemeTests.ApplyTheme_FlipsRequestedThemeVariant`, extend the `finally` to also restore the now-mutated global cache, and add a coupling test:

```csharp
finally
{
    Application.Current!.RequestedThemeVariant = original;
    CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(original == ThemeVariant.Dark);
}
```

```csharp
[AvaloniaFact]
public void ApplyTheme_AlsoSetsImmersiveDarkCache()
{
    ThemeVariant? original = Application.Current!.RequestedThemeVariant;
    bool originalDark = CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.IsDark;
    try
    {
        new AvaloniaThemeApplier(Mock.Of<IAppLogger>()).ApplyTheme(true);
        Assert.True(CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.IsDark);
    }
    finally
    {
        Application.Current!.RequestedThemeVariant = original;
        CSUploader.Lib.UI.AvaloniaImmersiveDarkMode.SetIsDark(originalDark);
    }
}
```

- [x] **Step 9: Run — verify green** (Avalonia suite +3; the existing ThemeTests still pass with the restored finally).

- [x] **Step 10: Suite gate + commit.** Both suites, both heads 0-warning Debug + Release.

```bash
git add src/CSUploader.Avalonia/Lib/UI/AvaloniaImmersiveDarkMode.cs src/CSUploader.Avalonia/Services/AvaloniaThemeApplier.cs src/CSUploader.Avalonia/App.axaml.cs tests/CSUploader.Avalonia.Tests/Lib/AvaloniaImmersiveDarkModeTests.cs tests/CSUploader.Avalonia.Tests/Theming/ThemeTests.cs
git commit -m "feat(avalonia): Phase 7 Task 7 - Win10 dark-title-bar DWM fallback; theme-applier sole writer"
```

**Task 7 executed (2026-07-14) — pending reviewer gate.** `AvaloniaImmersiveDarkMode` shipped (`src/CSUploader.Avalonia/Lib/UI/AvaloniaImmersiveDarkMode.cs`) — the three P/Invokes (`DwmSetWindowAttribute` PreserveSig, `SetWindowPos` Bool, `SendMessage` Unicode) and all constants (attr 20 + the 19 fallback; SWP_* ; WM_NCACTIVATE 0x0086) ported **VERBATIM** from `src/Lib/UI/ImmersiveDarkMode.cs`; only the two rule-45 substitutions differ (HWND via `TryGetPlatformHandle()?.Handle` instead of `WindowInteropHelper.EnsureHandle`; new-window hook via `Control.LoadedEvent.AddClassHandler<Window>` instead of `EventManager.RegisterClassHandler`; ContextIdle bounce -> `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)`). `SetIsDark` iterates the classic-desktop lifetime's `Windows` (null-guarded); `RegisterGlobalHandler` gained a `_registered` once-guard (WPF relies on single-call-site instead). **Sole-writer invariant HELD (grep-verified):** `AvaloniaImmersiveDarkMode.SetIsDark` has exactly ONE production caller — `AvaloniaThemeApplier.cs:38` (mirrors `WpfThemeApplier.cs:81`); `RegisterGlobalHandler` has exactly ONE caller — `App.axaml.cs:113`, once at startup before MainWindow (mirrors `App.xaml.cs:43`); `Apply` has no external production caller. Gates: Avalonia **411 -> 415** (+4), WPF/shared **1201/1201** (untouched — zero Core/WPF changes), both heads **0-warning Debug AND Release**. **Deviations (recorded):** (1) **+4 tests, not the plan's +3** — the plan's 3 (`SetIsDark_UpdatesCache`, `RegisterGlobalHandler_IsIdempotent`, `ApplyTheme_AlsoSetsImmersiveDarkCache`) PLUS the team-lead-requested NRE-safety test `Apply_WithNoPlatformHandle_IsSilentNoOp` (`[AvaloniaFact]`: an unshown Window has a null platform handle -> `Apply` early-returns before any DWM call for both `dark` values, and does NOT disturb the cache — the plan Step-5 headless no-op made non-vacuous). Step 9's "+3" is therefore superseded by "+4". (2) The `AvaloniaThemeApplier` class-header doc-comment ("...sole writer of the Phase 7 new-window dark-chrome preference **when that lands**") was left as-is — the plan's Step 6 prescribes only the `SetIsDark` call, not a comment edit; the future-tense phrasing is now mildly stale but untouched under minimal-change discipline (reviewer may tidy). (3) **Live title-bar flip NOT bridge-verified** — the DWM immersive-dark attribute recolors the OS NON-CLIENT title bar, which the bridge's client-area `ava_screenshot` cannot capture (team-lead-flagged caveat); on the Win11 dev box the title bar auto-recolors from the `ThemeVariant` natively (Avalonia platform behavior) INDEPENDENT of the P/Invoke, so a Win11 shot would not isolate this code's effect anyway — attribute-20 succeeds on Win11 but the DWM path is specifically the **Win10 fallback**. The plan prescribes no desktop-level shot for Task 7, so none was improvised. Mechanism confidence rests on: verbatim P/Invoke port from the shipping WPF head; the Reality-check register CONFIRMED shapes (`AddClassHandler<Window>` + `TryGetPlatformHandle`, re-proven by clean compile on 11.3.18); and the headless tests (writer discipline + once-guard + NRE-safety). Remaining **maintainer-only** (recorded in the register): the Win10 attribute-19 fallback path + the Win10 DWM VISUAL; and the maintainer's 125%/150% DPI toast-placement smoke. The `catch {}` in `Apply` is the WPF original's documented best-effort swallow ported verbatim (pre-Win10 E_INVALIDARG), not new slop.

**Task 7 reviewer follow-up (2026-07-14) — APPROVED with two polish fixes landed as a fixup commit:** (1) **NC-repaint bounce priority `Background` -> `ContextIdle`** — the reviewer verified by reflection that 11.3.18 DOES expose `DispatcherPriority.ContextIdle` (Reality-check item 7's "Avalonia lacks ContextIdle -> Background is the nearest deferral" was WRONG); since Avalonia mirrors WPF's ordering where Background runs SOONER than ContextIdle, `ContextIdle` restores strict fidelity to the WPF original's deliberate choice (engineered against a child-dialog first-open light-chrome flash on Win10) and removes a maintainer-only Win10 smoke risk. One token; comment updated. (2) `AvaloniaThemeApplier` class-header doc-comment reworded from future tense ("...when that lands") to present ("...sole writer of AvaloniaImmersiveDarkMode.IsDark — wired in ApplyTheme via SetIsDark"), now that :38 is the live call site (deviation #2 above resolved). No behavior/test change (still 415/415, 0-warning both heads). Reviewer-recorded accepted limitation (no action): `RegisterGlobalHandler_IsIdempotent` is a no-throw assertion only — Avalonia exposes no headless seam to COUNT registered static class handlers, so the once-guard's effect can't be asserted directly beyond "second call doesn't throw."

---

## Task 8: Phase gate — review, tag, reconcile

- [ ] **Step 1: Whole-diff review.** `git diff phase6-hard-views-ready..HEAD` by a fresh adversarial reviewer (whole-diff panels catch cross-task issues). Special attention: the DIP<->physical toast geometry (no double-scaling; primary-screen consistency between `workAreaProvider` and `AvaloniaToastHost`); the async close-action prompt (rule 44 — `_forceClose` re-entrancy correct; the Ask->Minimize branch matches WPF's NO-balloon parity while the direct Minimize branch DOES balloon); `ShowInfo` NOT gated on `ShowCompletionToasts`; the theme-applier sole-writer coupling; the menu bindings (`ToggleThemeCommand`/`ShowUploadOverview` two-way / `InstallUpdateCommand`+`IsUpdateAvailable`); every recorded deviation (dropped gesture text, message-box icons, tray toast icon choice).
- [ ] **Step 2: Mechanical gates.**
  - `grep -rn "System.Windows" src/CSUploader.Avalonia/` -> zero (the DWM port uses only `System.Runtime.InteropServices` + Avalonia types).
  - `grep -rn "BoolToVisibility" src/CSUploader.Avalonia/` -> zero (rule 33 unchanged).
  - `grep -rn "NoOpToastNotificationService" src/` -> zero (deleted; no stale reference).
  - Both suites green; final counts recorded (WPF 1201; Avalonia = 385 + 3 (T1) + 3 (T2) + 1 (T3) + 2 (T4) + 1 (T5) + 3 (T6) + 3 (T7) = 401, adjusted to the real running totals recorded per task).
  - i18n gate green (`scripts/md-to-resx.py --check`); the phase diff shows zero `Strings*.resx` changes.
  - Core-touch gate (the phase's one deliberate exception): `git diff phase6-hard-views-ready..HEAD -- src/CSUploader.Core/` touches ONLY `Services/IToastNotificationService.cs` + `Services/ToastNotificationService.cs`, and ONLY the additive `ShowInfo` member. Record it explicitly (Phase 6's "Core untouched" no longer holds — this is the sanctioned balloon-routing addition).
  - WPF-head safety: `git diff phase6-hard-views-ready..HEAD -- src/` outside `src/CSUploader.Avalonia/**` and `src/CSUploader.Core/**`-as-above touches ONLY `src/Services/ReferenceShotCapture.cs` (inside `#if DEBUG`). Release WPF build succeeds; WPF suite 1201.
  - Avalonia Release build succeeds; launched WITHOUT flags: no gallery/probe surface, four MainWindow tabs live WITH the menu bar, toasts fire on completion, close/minimize routes to tray per settings.
- [ ] **Step 3: Column-persistence verification (prep item 3 — verify-only).** Confirm the Avalonia UploadsView still wires `DataGridColumnVisibilityPersistence` (Apply/Persist/column menu/ColumnDisplayIndexChanged) — grep `src/CSUploader.Avalonia/Views/UploadsView.axaml.cs` for `DataGridColumnVisibilityPersistence` and `DataGridColumnMenu`; the Phase 6 column-persistence tests still pass. No new work; record "already shipped in Phase 6 Task 10".
- [ ] **Step 4: Contact sheet.** `toast` (light+dark, WPF+ava) and the re-captured `mainwindow-uploads` (Avalonia now with menu). Every pair Read and arbitrated; append to the accepted-divergence list (at minimum): the Fluent Menu chrome/density vs WPF's compact menu; the dropped Exit gesture text (rule 41, established ruling); the message-box has no info icon (existing divergence, reused by Check-for-updates); the tray toast icon (`StatusRunningImage` chosen for "still running"); the Win10 dark title bar is maintainer-verified (dev machine is Win11); and the toast-cell FRAMING difference — the WPF `toast-*-wpf.png` renders only the Border (`root.ActualWidth/Height`) so it looks tighter and CLIPS the drop shadow, while the ava `toast-*-ava.png` is a full-window shot with the `BoxShadow` visible (expected, NOT a regression — do not log it as one). Confirm the toast ShowActivated verdict (no focus steal) from Task 1.
- [ ] **Step 5:** `git tag phase7-shell-ready`.
- [ ] **Step 6: Reconcile the design doc** (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`) with Phase 7's outcomes — at minimum: the toast DIP<->physical recipe + the `ShowActivated`/topmost GO verdict; the balloon->toast `ShowInfo` addition (the one Core touch, ungated); the menu port (rule 41; `NativeMenu` rejected; gesture text dropped); the close-to-tray async-prompt rule (44) + WindowState-via-OnPropertyChanged (43); the dark-title-bar Win10 fallback (rule 45, `Control.LoadedEvent.AddClassHandler` + `TryGetPlatformHandle`, applier sole writer); column persistence already-done note; and carry forward the Phase 9 deferrals (prep items 4/5/6: register `UploadWizardViewModel`, `MainViewModel` IDisposable, the header-metrics pass, the two parity-checklist items). Commit — `"docs: reconcile design with Phase 7 outcomes (toast geometry, balloon route, menu, close-to-tray, dark title bar)"`.
- [ ] **Step 7: Surface to the maintainer** (via the team lead): the contact-sheet path; the toast GO/NO-GO verdict; the one Core touch (`ShowInfo`) + why; the accepted divergences; the standing manual checks that need a Win10 machine (dark title bar on new dialogs) and the maintainer's DPI (125%/150%) toast placement smoke; the Phase 9 deferrals still open.

**Task 8 gate definition of done:** whole-diff reviewed; all mechanical gates green; the one Core touch recorded and justified; WPF-head touched only by `ReferenceShotCapture.cs`; contact sheet complete + divergences listed; `phase7-shell-ready` tagged; design reconciled; the maintainer surfaced. After this the Avalonia head is feature-complete except Phase 8 WebView login.

**Phase 7 gate — reconcile ledger (recorded from the whole-diff gate panel):**

- **CROSS-HEAD LEDGER item (Phase 9):** Ask->Minimize WITHOUT Remember strands the app hidden with NO tray icon (`UpdateVisibility` disposes the icon because the persisted settings are unchanged — the in-memory `CloseAction` stays `Ask`, which `UpdateVisibility` reads as "no tray needed"). This is BYTE-IDENTICAL WPF behavior (`MainWindow.xaml.cs:95-100` + `TrayIconManager.cs:40-51`) — a shared pre-existing bug that Phase 7's close-to-tray merely makes newly reachable on the Avalonia head. Fix BOTH heads together at Phase 9 via a new `ITrayIconService.EnsureIconForSession()` (a Core interface touch, NOT sanctioned this phase). Do NOT paper over it by silently mutating the in-memory `CloseAction` — that would diverge from WPF and lose the user's real setting.
- **ACCEPTED DIVERGENCE (Phase 7 gate):** the "still running in the tray" info-toast's BODY click activates through the shared `activate` callback (`MainViewModel.ActivateAndShowUploadedTab`), so it restores the window AND lands on the Uploaded tab, whereas WPF's balloon click is inert. Accepted at the Phase 7 gate: the restore works and the tab flip is a once-per-session context nudge (the info toast fires only on the first hide of a session). Revisit only if the maintainer objects.

---

## Reality-check register

Items 1-8 were **PINNED by reflection on the installed Avalonia 11.3.18 bits during plan review** and are **CONFIRMED — executors must NOT re-derive them** (recorded fallbacks are unneeded). The ONLY genuinely-open items — verified at RUNTIME, not by reflection — are: (a) **transparent-popup rendering + no-focus-steal** — **RESOLVED GO (2026-07-14, commit fd06e46)**: ShowActivated=false holds (gallery kept isActive), chrome-less transparent rendering clean (no black frame), topmost + bottom-right placement exact at 100% scaling; remaining maintainer-only: 125%/150% visual + Win10 DWM (the Task 1 bridge probe proved render + no-focus-steal — do NOT re-probe), and (b) **the Win10 DWM dark-title-bar VISUAL** (maintainer-only; the dev box is Win11, which auto-recolors from the variant).

1. **`Window.ShowActivated`** — CONFIRMED present (`bool`). Fallback (unneeded): re-activate `desktop.MainWindow` after `host.Show()`. (The RUNTIME no-focus-steal — **RESOLVED GO (2026-07-14, commit fd06e46)**: ShowActivated=false holds, the gallery kept `isActive` with the non-activated probe topmost; no re-activation fallback needed.)
2. **`Window.SystemDecorations` (`SystemDecorations.None`), `TransparencyLevelHint` (`IReadOnlyList<WindowTransparencyLevel>`), `Background`, `CanResize`, `Border.BoxShadow` (`Avalonia.Media.BoxShadows`)** — all CONFIRMED present. The RUNTIME chrome-less-transparent RENDERING — **RESOLVED GO (2026-07-14, commit fd06e46)**: rendered clean, no black frame (see item (a); do NOT re-probe).
3. **`Window.Position` (`PixelPoint`), `Window.Screens` (`Screens`, `Avalonia.Controls`), `Screens.Primary`/`ScreenFromWindow`, `Screen` (element type — **`Avalonia.Platform`**, NOT `Avalonia.Controls`) `.WorkingArea` (`PixelRect`, physical px), `.Scaling` (`double`)** — all CONFIRMED present. The element-type namespace is why Tasks 1-3 read the primary screen via `var`.
4. **`Control.LoadedEvent`** is `RoutedEvent<RoutedEventArgs>` and the generic-instance `AddClassHandler<Window>(Action<Window, RoutedEventArgs>)` compiles — CONFIRMED (Task 7; the WPF `RegisterClassHandler` analog). Fallback (unneeded): hook `Window.Opened` for the main window only.
5. **`TopLevel.TryGetPlatformHandle()` -> `IPlatformHandle` (`.Handle`, `IntPtr`)** — CONFIRMED (Task 7); the Win32 HWND on the desktop backend, null under headless (Apply no-ops).
6. **`Window.Closing` is `EventHandler<WindowClosingEventArgs>`; `WindowClosingEventArgs.Cancel` is settable** — CONFIRMED (Task 6); rule 44 sound.
7. **`DispatcherPriority.Background`** — CONFIRMED present (Task 7; WPF used `ContextIdle`, which Avalonia lacks — `Background` is the nearest deferral).
8. **`MainViewModel.AvailableVersion` / `IsUpdateAvailable` / `CheckForUpdatesAsync` / `ToggleThemeCommand` / `ThemeMenuLabel` / `InstallUpdateCommand`; `UploadsViewModel.ShowUploadOverview` two-way-settable** (Task 5) — all confirmed present in `src/CSUploader.Core/ViewModels/MainViewModel.cs` at plan time; re-confirm the exact names when binding.

## Open questions (for the plan reviewer / team lead)

1. **The one Core touch — `IToastNotificationService.ShowInfo`.** **RESOLVED — APPROVED by the team lead (plan review 2026-07-14): keep it exactly this minimal — `ShowInfo(string title, string body)`, NO icon/kind parameter.** `ShowInfo` is an additive service-interface method (not a VM), required by the design's explicit balloon-routing deliverable (design line: "NotifyHidden routes through the app's own toast system"). The plan implements it as one interface line + one impl line, reusing the private `ShowToast` (fixed `StatusRunningImage`), ungated. (The rejected alternative — a head-side single toast that doesn't stack — is recorded only for context.)
2. **Tray toast icon.** The WPF balloon used `ToolTipIcon.Info`; there is no dedicated "info" bitmap key. The plan uses `StatusRunningImage` (semantically "still running"). Alternative: `StatusOkImage`. Arbitrated at the contact sheet — flag if you have a preference.
3. **Win10 dark-title-bar visual verification.** The dev machine is Win11 (auto-recolors), so the Win10 DWM fallback path is built faithfully + mechanism-tested but its VISUAL is manual-only. Acceptable as a manual cutover check, or de-scope to Phase 9's manual smoke?
4. **Exit gesture text.** Rule 41 drops `Main_Menu_File_Exit_Gesture` ("Alt+F4") from the Avalonia menu (Avalonia's `InputGesture` is a real accelerator, not display text). This matches the Phase 6 gate's accepted gesture-text ruling. Confirm you're OK carrying that divergence onto the menu bar.

## Self-review

- **Spec coverage** (design Phase 7 line + 6 prep items): ToastWindow+notifications -> Tasks 1-3; close/minimize-to-tray -> Task 6; balloon->toast routing -> Task 4; column persistence -> verified (Task 8 Step 3, already shipped Phase 6 Task 10); dark-title-bar Win10 fallback -> Task 7; menu bar + theme toggle (prep 1) -> Task 5; KeyBinding-vs-editor (prep 2) -> N/A recorded; prep 4/5/6 -> Phase 9 deferrals recorded. All covered.
- **Placeholder scan:** every code step carries complete code; no "TBD"/"add error handling"/"similar to Task N". Test code is complete and named.
- **Type consistency:** `ToastPlacement.WorkAreaToDip`/`DipToPhysical` signatures identical across Tasks 1/2/3; `AvaloniaToastHost.Height`/`Top`/`Left` match `IToastHost`; `IToastNotificationService.ShowInfo(string,string)` used identically in Task 4's Core impl, tray call, and both test fakes; `MainWindow(AppSettings, ITrayIconService, SettingRepository)` ctor identical in Task 6's code + tests + `App.axaml.cs`; `AvaloniaImmersiveDarkMode.SetIsDark`/`RegisterGlobalHandler`/`Apply`/`IsDark` consistent across Task 7 + the applier + `ThemeTests`.
