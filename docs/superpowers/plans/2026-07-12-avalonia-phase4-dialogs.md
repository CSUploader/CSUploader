# Avalonia Migration Phase 4: Simple Dialogs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Avalonia head's dialog surface real: the ten simple WPF dialogs ported (Confirmation, CloseAction, SpeedLimit, ProxyText, ErrorDetails, Progress, UpdateProgress, About, LogDetails, HttpDetails), `AvaloniaDialogService` rewritten onto `DialogServiceBase` with a shared owner resolver and StorageProvider pickers, `AvaloniaUpdateProgressSink` made real, and the startup-hydration failure upgraded to a real error dialog — each dialog verified by headless interaction tests and by WPF-vs-Avalonia reference shots in the phase contact sheet, driven through the bridge.

**Architecture:** Strangler step 4 (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, §The Avalonia head "Dialogs" bullet — StorageProvider pickers, small custom message-box window with **no MsBox.Avalonia dependency**, `ShowDialog<T>` async — plus the IDialogService owner contract and the `DialogServiceBase` suppression architecture). The design's Phase 4 line carries **7 PREP ITEMS** from the Phase 3 gate; every one is a task or an explicit step here (§Prep-item coverage). Dialogs whose IDialogService member constructs a Phase 5 window (`ShowAddAccountDialogAsync`, `ShowEditAccountDialogAsync`, `ShowEditProxyDialogAsync`) stay `NotImplementedException` — they are EditAccountWindow/EditProxyWindow's Phase 5 items, and the phase gate pins that exactly those three remain.

**Tech Stack:** unchanged from Phase 3 — .NET 10, Avalonia **11.3.18** + Avalonia.Controls.DataGrid **11.3.13** + Avalonia.Themes.Fluent + Avalonia.Svg.Skia 11.3.0, Avalonia.Headless.XUnit 11.3.18 (+ Markup.Xaml.Loader), CommunityToolkit.Mvvm 8.4.2 (Core). **This phase adds NO packages** (the design explicitly rejects MsBox.Avalonia; pickers are the built-in StorageProvider). Bridge via `scripts/ava-drive.cs`; contact sheet via `scripts/contact-sheet.py`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase3-primitives-ready`. Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree, has uncommitted Buzzheavier work).
- **Suite gate after every task** (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — **1178 green at phase start**; the count only goes up, never down.
  - `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — **147 green at phase start** (confirm the exact number at Task 1's gate and correct it here if it drifted); most Phase 4 tasks raise this count — record each new baseline and carry it forward.
  - Separate OutDirs are mandatory (shared OutDir mixes WPF and Avalonia assemblies and breaks discovery). Never run bare solution-level `dotnet test -p:OutDir=…`.
- Head builds: Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`; WPF `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf`. Scratch DBs live beside those exes; seed with `dotnet run scripts/seed-fake-data.cs -- <outdir>` (idempotent).
- Every csproj keeps `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`. Version pins are hard; do not bump anything.
- **The WPF head is touched by exactly two files this phase** (Task 1: `ReferenceShotCapture.cs` gains the dialogs mode; `App.xaml.cs` forwards the `--dialogs` switch), both inside the existing `#if DEBUG` envelope, sanctioned by design prep item 1. Release behavior byte-identical; the full existing suite is the regression net.
- **i18n: NO new keys this phase.** The message box, dialogs, and drivers reuse existing keys (`Common_OK/Common_Error/Common_Confirm/Common_Close/Common_Copy/Common_SelectFolder/Common_SelectFiles`, `Confirmation_*`, plus each dialog's own `X_*` family — all already in `docs/i18n-inventory.md`). Gallery/driver text stays hardcoded English (dev tool convention). The phase-gate diff must show **zero `Strings*.resx` changes**. Never hand-edit resx.
- **Agent-safety** (unchanged): Avalonia launches for bridge work always pass `--agent`; scratch DBs only; never copy a real `CSUploader.db`; the dialog drivers synthesize ALL data (bogus errors, fake transactions, fake log events) — no dialog driver reads real account state.
- **ava-drive gotchas** (Phase 2/3 experience): `ava_action`'s argument is **`verb`, not `action`**; find-style tools return a **bare JSON array**; handshake discovery picks the newest live handshake — close forgotten bridge apps first; single-driver lock (no MCP attach while ava-drive runs).
- **Shots convention** (extends Phase 3): `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png`. Phase 4 view names (identical on both sides so the contact sheet pairs them): `confirmation`, `closeaction`, `speedlimit`, `proxytext-edit`, `proxytext-export`, `errordetails`, `progress`, `updateprogress`, `about`, `logdetails`, `httpdetails`; plus Avalonia-only `messagebox-error`, `messagebox-confirm` (their WPF counterpart is the native `MessageBox.Show`, which RenderTargetBitmap cannot capture — the wpf cell renders "missing", expected and noted).
- `[AvaloniaFact]` discipline (Phase 3 rule): tests that flip theme/culture/`SuppressedConfirmations` restore process-global state in `finally`.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited file:line and copy the semantics exactly. Where this plan could not pin an Avalonia API shape against the installed bits, the step says so and §Reality-check register lists it.

### Prep-item coverage (the 7 items from the Phase 3 gate, design §Phases "Phase 4")

| # | Prep item | Where |
|---|-----------|-------|
| 1 | Dialog reference-shot drivers on BOTH heads first; verify ava_screenshot addresses an open modal early | Task 1 |
| 2 | Owner policy, stated once, shared resolver on the real AvaloniaDialogService | Task 2 |
| 3 | Picker mapping wrinkles (DefaultExtension TrimStart, defaultExt no-op on open, TryGetFolderFromPathAsync, FilterEntry 1:1) | Task 4 |
| 4 | PORT RULE: window-local keyed styles → Window.Styles class selectors, BasedOn dropped | §Port rules row 12; exercised in Task 7 (LogDetailsWindow) |
| 5 | TabControl + ProgressBar into the gallery FIRST (their first Fluent-vs-WPF arbitration) | Task 1 |
| 6 | Startup-hydration failure → real error dialog; UpdateProgressWindow through IUpdateProgressSink, NOT IDialogService | Task 3 Step 6 (startup); Task 8 (sink) |
| 7 | PORT RULE: brush-returning converters don't track theme flips — Classes + DynamicResource for theme-live cells | §Port rules row 13 (verified at plan time: none of the 10 dialogs uses a brush-returning converter — all brushes are direct DynamicResource references, theme-live by construction) |

### Port rules (standing table for every Phase 4-6 dialog/window port)

WPF dialog XAML → Avalonia mapping, resolved once here so each port is mechanical:

| # | WPF | Avalonia |
|---|-----|----------|
| 1 | `ResizeMode="NoResize"` | `CanResize="False"` |
| 2 | `ResizeMode="CanResizeWithGrip"` | `CanResize="True"` (no grip visual — accepted, contact sheet arbitrates) |
| 3 | `Icon="pack://application:,,,/Properties/Images/Logo/icon.ico"` | `Icon="/Assets/icon.ico"` (the existing MainWindow.axaml:8 convention — root-relative avares) |
| 4 | `WindowStyle="ToolWindow"` | no equivalent — normal decorations (client-area shots are chrome-free, so the contact sheet is unaffected; note only) |
| 5 | `Visibility="Collapsed"` / `Visibility.Visible` toggles | `IsVisible="False"` / `IsVisible = true` (Avalonia IsVisible=false collapses layout, same as WPF Collapsed) |
| 6 | `DialogResult = true/false` + result properties | `Close(result)` with `await ShowDialog<T>(owner)`; window-X / Esc yields `default(T)` = the WPF `DialogResult != true` path. Design §The Avalonia head: single-completion — `Close(x)` is the only completion call per handler |
| 7 | `IsCancel="True"` auto-closes the WPF dialog | Avalonia `IsCancel` only routes Esc to the button's Click — **every IsCancel button needs an explicit Click handler that calls `Close()`/`Close(default)`** (bit AboutWindow, whose WPF button has NO handler at all). Verify the Esc routing on 11.3.18 (§Reality-check #1) |
| 8 | `VerticalScrollBarVisibility="Auto"` on TextBox | `ScrollViewer.VerticalScrollBarVisibility="Auto"` attached property (same for Horizontal) |
| 9 | `Clipboard.SetText(...)` in code-behind | `this.Clipboard?.SetTextAsync(...)` (Window is a TopLevel), keep the swallow-failures try/catch (parity); §Reality-check #2 |
| 10 | `MouseLeftButtonUp` | `PointerReleased` + `e.InitialPressMouseButton == MouseButton.Left` guard |
| 11 | `RenderOptions.BitmapScalingMode="HighQuality"` | `RenderOptions.BitmapInterpolationMode="HighQuality"` |
| 12 | Window-local keyed styles (`FieldLabel`, `FieldValue` `BasedOn={x:Type}`) | `<Window.Styles>` class selectors (`TextBlock.field-label`, `TextBox.field-value`); **BasedOn dropped** — class styles layer on the theme style (prep item 4) |
| 13 | brush via converter | not in this phase's dialogs (verified); if a later port needs a theme-coded value, use Classes + DynamicResource setters, never a converter-resolved brush (prep item 7) |
| 14 | `Style="{StaticResource PrimaryButtonStyle}"` | `Classes="primary"` (Phase 3 BaseStyles) |
| 15 | `{loc:Loc Key}` | identical — the Avalonia LocExtension shares the namespace on purpose |
| 16 | Focus/SelectAll in ctor (SpeedLimitDialog) | move to the `Opened` event (controls aren't attached at ctor time in Avalonia) |
| 17 | `MessageBox.Show(...)` inside a dialog | `MessageBoxWindow` static helpers (Task 3) |

### Dialog disposition (all 10, dependency order)

| Dialog (src/Views/) | Avalonia file (src/CSUploader.Avalonia/Views/) | IDialogService member / consumer today | Task |
|---|---|---|---|
| ConfirmationDialog | **merged into `MessageBoxWindow`** (YesNo + opt-out-checkbox mode; no separate ConfirmationDialog file) | ShowOptOutConfirmationCoreAsync; ShowError/ShowConfirmation are its OK / YesNo modes | 3 |
| SpeedLimitDialog | SpeedLimitDialog.axaml | ShowSpeedLimitDialogAsync ← UploadsViewModel:424 | 5 |
| ProxyTextDialog | ProxyTextDialog.axaml | ShowProxyTextDialogAsync ← ConnectionManagerViewModel ×2 (read-only + editable) | 5 |
| ErrorDetailsWindow | ErrorDetailsWindow.axaml | none — opened by EditAccountWindow (Phase 5); driver-only until then | 5 |
| CloseActionDialog | CloseActionDialog.axaml | none — MainWindow close-to-tray is Phase 7; driver-only until then | 6 |
| AboutWindow | AboutWindow.axaml | none — MainWindow menu is Phase 6/7; driver-only until then | 6 |
| LogDetailsWindow | LogDetailsWindow.axaml | none — LogsView Enter/double-click is Phase 5; driver-only until then | 7 |
| HttpDetailsWindow | HttpDetailsWindow.axaml | ShowHttpDetailsAsync ← ConnectionManagerViewModel:381; also LogsView (Phase 5) via the LogEntryViewModel ctor | 7 |
| UpdateProgressWindow | UpdateProgressWindow.axaml | IUpdateProgressSink ← MainViewModel:129-143 (NOT IDialogService — prep item 6) | 8 |
| ProgressWindow | ProgressWindow.axaml | **⚠ ZERO callers on either head** (plan-time grep: `ProgressWindow.ExecuteAsync` unused since the WinForms→WPF migration, commit 6d68070). Ported anyway — the design names it and it's the reusable modal-progress primitive — but minimal, last, and flagged (§Reality-check #13) | 8 |

---

### Task 1: Dialog-shot drivers on both heads + TabControl/ProgressBar gallery arbitration + modal-addressing proof

Prep items 1 and 5. Everything later in the phase verifies against what this task builds, and the two Fluent controls Phase 4 consumes first (3 TabControls in HttpDetails alone; ProgressBar in Progress/UpdateProgress — its brushes were ported in Phase 3 but are so far unconsumed) get their WPF-vs-Fluent arbitration BEFORE any dialog that depends on them is ported.

**Files:**
- Modify: `src/Services/ReferenceShotCapture.cs` (dialogs mode), `src/App.xaml.cs` (forward `--dialogs`, inside the existing `#if DEBUG` block at the `--shots` trigger)
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml` + `.axaml.cs` (TabControl/ProgressBar section + "Dialogs (Phase 4)" launcher section scaffold + placeholder modal)

**Interfaces:**
- Produces: `wpf`-side reference shots for all 11 dialog view names, light + dark (22 PNGs); the gallery launcher-button convention every dialog task extends (`Dialog<Name>Button`, one per dialog, added in the task that ports the dialog); the recorded TabControl/ProgressBar arbitration verdict; proof that `ava_screenshot` captures an open modal.
- Consumes: `ReferenceShotCapture.CaptureWindow` + `WaitForRenderAsync` (same class — the design's "make WaitForRenderAsync accessible" is moot in-class; widen to `internal` only if the runner ends up in another file), `IThemeApplier`, the ten WPF dialog types.

- [x] **Step 1: WPF dialogs mode.** In `ReferenceShotCapture`, add `RunDialogsAndShutdownAsync(Window mainWindow, string dir)` and switch on a `--dialogs` companion flag in `App.xaml.cs` (`--shots [dir] --dialogs` → dialogs set instead of the tab set; bare `--shots` keeps capturing tabs). Same settle-delay preamble, then per theme (light, dark) per dialog: construct with synthesized args, `Show()` (NOT ShowDialog — nothing pumps results here, we only need pixels; §Reality-check #3), `await WaitForRenderAsync(dialog)`, `CaptureWindow(dialog, $"{name}-{theme}-wpf.png")`, `dialog.Close()`. The synthesized-args table (one local factory each; all data fake):

| view name | construction |
|---|---|
| `confirmation` | `new ConfirmationDialog("Delete 3 selected packages?\nThis cannot be undone.", Localizer.Instance["Confirmation_WindowTitle"])` |
| `closeaction` | `new CloseActionDialog()` |
| `speedlimit` | `new SpeedLimitDialog(512)` |
| `proxytext-edit` | `new ProxyTextDialog("Import proxies", "One proxy per line, host:port[:user:pass].", "127.0.0.1:8080\n10.0.0.1:1080:user:pass", readOnly: false)` |
| `proxytext-export` | same text, `readOnly: true` |
| `errordetails` | `new ErrorDetailsWindow("Sign-in failed: invalid credentials\n\n<html><body>…~600 chars of synthesized HTML snippet…</body></html>")` |
| `progress` | `new ProgressWindow()`; set `LabelText.Text` to a two-line label (mirror ExecuteAsync's `labelText + NewLine + Progress_LabelSuffix`), make `CancelButton` visible (the allowCancel look) |
| `updateprogress` | `new UpdateProgressWindow()`; `SetStatus(string.Format(Localizer.Instance["UpdateProgress_StatusDownloading_Format"], "1.2.3"))`; `SetProgress(42)` |
| `about` | `new AboutWindow()` |
| `logdetails` | `new LogDetailsWindow(new LogEntryViewModel(SynthLogEvent()))` — a LogEvent with date/thread/file/function/line and a multi-line message (§Reality-check #4 pins LogEvent's construction shape) |
| `httpdetails` | `new HttpDetailsWindow(SynthTransaction())` — POST url, request+response headers dictionaries, small JSON bodies (so the Body-JSON sub-tabs pretty-print), `ResponseBodyBytes` set (so Hex renders), StatusCode 200, `StartTime`/`EndTime` set (`Duration` is computed from them, not settable) |

  `SynthTransaction()`/`SynthLogEvent()` are private statics in `ReferenceShotCapture` (HttpTransaction is all init-settable properties — verified at plan time; the Avalonia gallery will carry its own copies in later tasks — small deliberate duplication between two DEBUG-only dev tools).
  Note: dialogs whose ctor resolves an active owner (ConfirmationDialog, CloseActionDialog) find the shown MainWindow — fine. Windows with `SizeToContent` need the render-settle before capture (already the sequence).
- [x] **Step 2: Run it.** `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf` (seeded dir), then `D:\temp2\cbuild-mig\wpf\CSUploader.exe --shots --dialogs`. Expected: 22 new PNGs; Read two (one SizeToContent one, e.g. `confirmation-dark-wpf.png`, and `httpdetails-light-wpf.png`) — full client area, correct theme, text legible. Rebuild the contact sheet (`python scripts/contact-sheet.py`) — the new rows are single-sided (ava missing), expected.
- [x] **Step 3: Gallery — TabControl + ProgressBar section** (before any dialog port). New section in `GalleryWindow.axaml` after the DataGrid section: a `TabControl` with 3 tabs whose middle tab contains a nested 2-tab `TabControl` holding a read-only Consolas TextBox on `CodeBackgroundBrush` (the exact HttpDetails shape); a determinate `ProgressBar` (Minimum 0, Maximum 100, Value 42, Height 18 — the UpdateProgress shape) and an indeterminate one (Height 20 — the ProgressWindow shape). Hardcoded English labels.
- [x] **Step 4: Gallery — dialog-launcher scaffold + placeholder modal.** New last section "Dialogs (Phase 4)" (`section-header`): a short explanatory TextBlock and, for now, ONE button `DialogPlaceholderButton` whose handler shows a trivial inline modal (`new Window { Title = "Placeholder modal", Width = 300, Height = 150, Content = new TextBlock { Text = "modal test" } }.ShowDialog(this)`). Each later task replaces/extends this section with its real `Dialog<Name>Button`s. The buttons call through the REAL service/window paths when they land (that is the point — the drivers exercise production dialog plumbing).
- [x] **Step 5: Bridge session — modal addressing proof (the "early" verification the prep item demands).** Build, seed, launch `--agent --gallery` (background), then:
  - `ava_find` the `DialogPlaceholderButton`, `ava_action` click (verb!) → placeholder modal opens (modal over the gallery).
  - `ava_screenshot '{"maxWidth":2500}'` → Read the PNG: **is the open modal in the frame?** Bridge semantics say the screenshot targets the focused window (Phase 3 Task 9 note) — if the modal is captured, the per-dialog shot flow is proven. Also `ava_tree`/`ava_find` the modal's content (proves the bridge can address controls INSIDE a modal — needed to click Cancel/Close buttons in later tasks) and close it via a click or `ava_action` on the window if the schema offers close.
  - **If ava_screenshot cannot address the modal** (only main/gallery captured): fall back per §Reality-check #5 — add a DEBUG in-app Avalonia capture helper (`RenderTargetBitmap` on the dialog window → PNG, the WPF hook's twin) that each launcher invokes after open; record the fallback and adjust the per-dialog verification steps to use it.
  - Capture `gallery-light-ava.png`/`gallery-dark-ava.png` refreshes (theme toggle button) so the new TabControl/ProgressBar section lands in the sheet.
- [x] **Step 6: Arbitration verdict (recorded in task notes).** Compare the gallery's TabControl against `httpdetails-*-wpf.png` / `logdetails-*-wpf.png` and the ProgressBars against `progress-*-wpf.png` / `updateprogress-*-wpf.png` (from Step 2), both themes. Rule: "close and consistent" beats pixel-perfect; re-template ONLY on visible divergence, recorded with evidence. Expected friction points: Fluent's underline-style tab strip vs WPF's classic bordered tabs; Fluent ProgressBar corner rounding/height. Any re-template decision lands here (a targeted style in BaseStyles.axaml or a `Window.Styles` scope), BEFORE Tasks 5-8 consume the controls.
- [x] **Step 7:** Full suite gate (both, confirm the 1178/147 baselines). **Commit** — `"dev: dialog reference-shot drivers (WPF --shots --dialogs, gallery launcher scaffold) + TabControl/ProgressBar arbitration"`

---


**Task 1 verdict (executed 2026-07-11, commit 71cefec):**
- **TabControl — ACCEPT** Fluent's underline tab strip vs WPF's classic bordered tabs (close and consistent; re-templating would fight Fluent for a dialog-internal control).
- **ProgressBar — ACCEPT Fluent's SystemAccentColor blue AS-IS** (deliberately NOT binding Foreground to ProgressBarFillBrush). Root cause of the divergence: WPF's ProgressWindow/UpdateProgressWindow use a bare `<ProgressBar>` with no Foreground → OS-default Aero GREEN, an unstyled Win32 default, not a design choice; the app's own progress accent (ProgressBarFillBrush, UploadsView custom bar) is blue. Fluent blue is closer to the app's real design language. Task 5/8: consume `<ProgressBar>` as-is, do not re-decide.
- **Modal addressing — RESOLVED, no fallback needed**: no-ref `ava_screenshot` captures the topmost open modal (evidence `_modal-proof.png`); `ava_search`/`ava_action verb:invoke` reach controls inside modals. Later tasks screenshot modals directly; the in-app RenderTargetBitmap fallback is dead.
- Baselines confirmed at this gate: WPF 1178, Avalonia 147 (no drift).

### Task 2: The shared owner resolver on AvaloniaDialogService

Prep item 2 — the owner policy, stated once, implemented once, tested headlessly. Avalonia `ShowDialog` **requires** an owner and throws on null/hidden owners, and this app hides MainWindow to the tray (WPF tolerated ownerless dialogs; the WPF service's `ActiveOwner` at `src/Services/DialogService.cs:22-23` could return null harmlessly). The same resolver feeds StorageProvider and (already, by convention) mirrors `AvaloniaClipboardService.ResolveClipboard` (`src/CSUploader.Avalonia/Services/AvaloniaClipboardService.cs:28-37`).

**Policy (design wording, made operational):**
1. `active window` — first `desktop.Windows` where `IsActive && IsVisible` (a dialog opened from the modal wizard parents to the wizard, per the IDialogService owner contract in Core).
2. `?? visible MainWindow` — `desktop.MainWindow` if `IsVisible`.
3. `?? null` — and the CALLER decides:
   - **Message box** (Task 3): ownerless `Show()` + await `Closed` (never yank the tray-hidden main window up for a mere notification).
   - **ShowDialog<T> dialogs and pickers** (Tasks 4, 5, 7): reveal via `ITrayIconService.ShowMainWindow()` (restores + activates, `AvaloniaTrayIconService.cs:68`), then use MainWindow as owner — a modal interaction demands a visible parent.

**Files:**
- Create: `src/CSUploader.Avalonia/Services/DialogOwnerResolver.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Services/DialogOwnerResolverTests.cs`

**Interfaces:**
- Produces: `internal static class DialogOwnerResolver` with a PURE core `internal static Window? Resolve(IEnumerable<Window> windows, Window? mainWindow)` (the testable policy chain) and a lifetime-reading wrapper `internal static Window? ResolveFromLifetime()` (reads `IClassicDesktopStyleApplicationLifetime.Windows`/`.MainWindow`; returns null under non-desktop lifetimes — i.e. headless — which is why the core is separate). Task 3 adds the reveal-or-ownerless composition on top.
- Consumes: `Avalonia.Controls.Window`, the desktop lifetime.

- [ ] **Step 1: Implement** exactly the two-method shape above. The wrapper is 5 lines (lifetime cast → `Resolve(desktop.Windows, desktop.MainWindow)`). No logging, no service state — static, like the clipboard resolver.
- [ ] **Step 2: Headless tests** (`[AvaloniaFact]`, real windows via the real App — Phase 3's TestAppBuilder):
  - `ActiveVisibleWindow_Wins` — two shown windows, `Activate()` the second, assert `Resolve` returns it (if headless `Activate()` doesn't flip `IsActive` — §Reality-check #6 — drive the state via the pure core with a hand-rolled window list and assert the CHAIN instead; record which).
  - `NoActiveWindow_FallsBackToVisibleMainWindow` — main shown but not active, resolver returns it.
  - `TrayHiddenMainWindow_YieldsNull` — the load-bearing case: main window `Hide()`den, no other windows → null (this is what forces the reveal/ownerless branches; the WPF head never had to make this decision).
  - `HiddenActiveIsSkipped` — a hidden window never wins even if `IsActive` lingers.
  - All windows closed in `finally` (headless windows are process-global for the session).
- [ ] **Step 3:** Full suite gate; record the Avalonia count. **Commit** — `"feat(avalonia): dialog owner resolver (active ?? visible-main ?? null) + headless policy tests"`

---

### Task 3: MessageBoxWindow + AvaloniaDialogService onto DialogServiceBase + the startup error dialog

The custom message-box window (design: "MessageBox.Show sites → small custom message-box window (no MsBox.Avalonia dep)") — and it IS the ConfirmationDialog port: the WPF ConfirmationDialog (`src/Views/ConfirmationDialog.xaml`) is visually the message box's YesNo+checkbox mode, so one window with three modes replaces both WPF surfaces. `AvaloniaDialogService` is rewritten from the all-throwing Phase 2 stub onto `DialogServiceBase` (`src/CSUploader.Core/Services/DialogServiceBase.cs` — the base owns suppression lookup + "don't ask again" persistence; the head implements only `ShowOptOutConfirmationCoreAsync`).

**Files:**
- Create: `src/CSUploader.Avalonia/Views/MessageBoxWindow.axaml` + `.axaml.cs`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` (stub → `DialogServiceBase` subclass; ShowError/ShowConfirmation/OptOutCore real, pickers+dialog members still throwing until Tasks 4/5/7), `src/CSUploader.Avalonia/App.axaml.cs` (DI comment + startup-failure upgrade), `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (3 launcher buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Views/MessageBoxWindowTests.cs`, `tests/CSUploader.Avalonia.Tests/Services/AvaloniaDialogServiceTests.cs`

**Interfaces:**
- Produces: `MessageBoxWindow` — ctor `(string message, string title, MessageBoxMode mode)` where `internal enum MessageBoxMode { Ok, YesNo, YesNoDontAskAgain }`; result `internal readonly record struct MessageBoxOutcome(bool Confirmed, bool DontAskAgain)`; an `internal MessageBoxOutcome Outcome { get; }` property set by every button handler before `Close(...)` — the ownerless `Show()`+await-`Closed` path reads it (nothing else carries the result there), while the modal path gets the same value through `ShowDialog<MessageBoxOutcome>`'s dialog result; both completion sources are thus defined. Static composition helpers that OWN the null-owner branch:
  - `static Task ShowErrorAsync(Window? owner, string message, string title)`
  - `static Task<bool> ShowConfirmationAsync(Window? owner, string message, string title)`
  - `static Task<MessageBoxOutcome> ShowOptOutAsync(Window? owner, string message, string title)`
  each doing `owner is not null ? await ShowDialog<MessageBoxOutcome>(owner) : (Show() + await Closed via TCS, then read the window's Outcome property)` — the design's "ownerless Show()+await Closed for the message box".
- Consumes: `DialogOwnerResolver` (Task 2), Localizer keys `Common_OK`, `Confirmation_BtnYes/BtnNo/DontAskAgain` (existing), Phase 3 theme brushes.

- [ ] **Step 1: XAML** — port `ConfirmationDialog.xaml` mechanically per §Port rules (Width 440, SizeToContent Height, CanResize False, CenterOwner, ShowInTaskbar False, `SurfaceBrush`/`TextPrimaryBrush`, Icon per rule 3; message TextBlock wrap 13px; checkbox `Confirmation_DontAskAgain` in `TextSecondaryBrush`; button row right-aligned). Mode plumbing in code-behind, with keyboard roles explicit for ALL modes (Avalonia routes Enter through the IsDefault button's Click and Esc through the IsCancel button's Click — rule 7 — so this preserves WPF's Enter→Yes / Esc→No parity, mirroring `ConfirmationDialog.xaml:37-38`):
  - `Ok` → single `{loc:Loc Common_OK}` button, **IsDefault + IsCancel**, Click → `Close(new MessageBoxOutcome(true, false))`; checkbox hidden.
  - `YesNo` → Yes (`Confirmation_BtnYes`, **IsDefault**) and No (`Confirmation_BtnNo`, **IsCancel**), each with its own Click handler; checkbox hidden.
  - `YesNoDontAskAgain` → same two buttons/roles + checkbox visible.
  Yes → `Close(new MessageBoxOutcome(true, DontAskAgainCheck.IsChecked == true))`; No → `Close(new MessageBoxOutcome(false, false))` (not `Close(default)` — same value, honest intent); window-X → no `Close(result)` call, so `ShowDialog<MessageBoxOutcome>` yields `default` = not confirmed, and the `Outcome` property's initializer default covers the ownerless path. **Deviation, noted for the reviewer:** WPF `MessageBox.Show` displayed system Error/Question icons; the custom box shows none (ConfirmationDialog styling for all modes) — "close and consistent" arbitration at the phase gate, add a Status-bitmap icon slot only if the reviewer asks.
- [ ] **Step 2: Service flip.** `public sealed class AvaloniaDialogService(AppSettings settings, SettingRepository settingRepository, ITrayIconService trayIcon) : DialogServiceBase(settings, settingRepository), IDialogService`. Implement:

```csharp
public Task ShowErrorAsync(string message, string? title = null) =>
    MessageBoxWindow.ShowErrorAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title ?? Localizer.Instance["Common_Error"]);

public Task<bool> ShowConfirmationAsync(string message, string? title = null) =>
    MessageBoxWindow.ShowConfirmationAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title ?? Localizer.Instance["Common_Confirm"]);

protected override async Task<(bool Confirmed, bool DontAskAgain)> ShowOptOutConfirmationCoreAsync(string message, string title)
{
    MessageBoxOutcome o = await MessageBoxWindow.ShowOptOutAsync(DialogOwnerResolver.ResolveFromLifetime(), message, title);
    return (o.Confirmed, o.DontAskAgain);
}
```

  Also add the shared owner-or-reveal helper the LATER tasks consume (modal dialogs + pickers): `private async Task<Window> GetOwnerOrRevealAsync() { Window? o = DialogOwnerResolver.ResolveFromLifetime(); if (o is null) { trayIcon.ShowMainWindow(); o = DialogOwnerResolver.ResolveFromLifetime() ?? throw new InvalidOperationException("No window available to own a dialog."); } return o; }` (§Reality-check #7 — whether ShowMainWindow's `Show()` is synchronous enough for the re-resolve, else pump `Dispatcher.UIThread.RunJobs`-equivalent / await an Opened hop). The remaining unimplemented members keep throwing but with updated messages ("arrives with EditProxyWindow/EditAccountWindow in Phase 5").
- [ ] **Step 3: DI comment** in `App.axaml.cs:194` — drop "throws per member until Phase 4", note the three Phase 5 members.
- [ ] **Step 4: Headless tests.**
  - MessageBoxWindow: Yes-click → `(true, false)`; Yes+ticked-checkbox → `(true, true)`; No → default; Ok mode has exactly one button and checkbox invisible; window-close (X) → default. Drive via Phase 3 Task 8's input simulation (`window.MouseDown/MouseUp` on the button's translated center; ShowDialog-await + `Dispatcher.UIThread.RunJobs()` interplay is §Reality-check #8 — if awaiting `ShowDialog<T>` deadlocks the test loop, fall back to `Show()` + raising the Click handler + asserting `Close` outcome via `Closed`).
  - AvaloniaDialogService (subclass of the real service or the real one with a scratch SettingRepository — mirror how the WPF suite tests DialogServiceBase, reuse its harness pattern if one exists in tests/): suppressed key returns true WITHOUT showing (assert no new window in `desktop`-less headless: the resolver returns null and the ownerless path would open a window — assert none opened); Yes+dontAskAgain adds to `settings.SuppressedConfirmations` (restore in `finally`).
- [ ] **Step 5: Gallery buttons.** Replace the Task 1 placeholder with `DialogErrorButton`, `DialogConfirmButton`, `DialogOptOutButton` calling the REAL resolved `IDialogService` (add it to GalleryWindow's ctor injections like IThemeApplier at `App.axaml.cs:149`). The opt-out button uses a FRESH key per click (`"gallery-" + Guid.NewGuid().ToString("N")`) so Yes+tick doesn't silently suppress the next drive (it persists to the scratch DB — harmless but confusing).
- [ ] **Step 6: Startup-failure upgrade** (prep item 6). In the `mainWindow.Opened` catch (`App.axaml.cs:128-139`): keep the log and the title-mark, then `await _serviceProvider.GetRequiredService<IDialogService>().ShowErrorAsync($"Startup initialization failed:\n\n{ex.Message}");` and update the comment (drop "Phase 4 upgrades this…"). MainWindow is shown at that point, so the resolver finds it. No new i18n key (the exception text is not localizable; the title falls back to `Common_Error`). This path can't be forced live without breaking the scratch DB — verified by inspection + reviewer (§Reality-check #9).
- [ ] **Step 7: Bridge session.** Launch `--agent --gallery`; click each of the three buttons; per dialog: `ava_screenshot` (modal addressing proven in Task 1) → `confirmation-light-ava.png` from the opt-out button (this is the WPF ConfirmationDialog's pair), `messagebox-error-light-ava.png`, `messagebox-confirm-light-ava.png`; close via clicking No/OK **through the bridge** (the open/interact/close loop); theme-toggle and repeat for dark. Rebuild contact sheet; Read the `confirmation` pair vs WPF.
- [ ] **Step 8:** Full suite gate; record counts. **Commit** — `"feat(avalonia): MessageBoxWindow (Ok/YesNo/opt-out modes) + AvaloniaDialogService onto DialogServiceBase; startup failure gets a real error dialog"`

---

### Task 4: StorageProvider pickers

Prep item 3. The four Browse members, replacing Ookii + Microsoft.Win32 (WPF impl `src/Services/DialogService.cs:49-126`). All wrinkles resolved here once:

- `BrowseSaveFileAsync`: `SaveFilePicker.DefaultExtension` must get `defaultExt?.TrimStart('.')` — every WPF caller passes `".txt"`/`".json"` and Avalonia expects the bare extension.
- `BrowseOpenFileAsync(defaultExt:)`: **documented no-op on Avalonia** (open pickers have no extension-append concept) — keep the parameter, add the doc comment, drop it silently.
- `initialDirectory` → `await sp.TryGetFolderFromPathAsync(dir)` → `SuggestedStartLocation` (null result = no suggestion, never throw — §Reality-check #10).
- Win32 `filter` → `FileDialogFilterParser.Parse` (Core, already unit-tested) → `FilePickerFileType(name) { Patterns = patterns }` 1:1.
- Titles default from Localizer (`Common_SelectFolder` / `Common_SelectFiles`) — same keys as WPF.
- Return mapping: `IStorageFile/Folder.TryGetLocalPath()`; null/empty selection → null (the WPF cancel contract).
- Owner/StorageProvider via `GetOwnerOrRevealAsync()` (Task 3) → `owner.StorageProvider`.

**Files:**
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs`
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (4 picker buttons + a result TextBlock)
- Test: `tests/CSUploader.Avalonia.Tests/Services/PickerOptionsTests.cs`

- [ ] **Step 1: Implement the four members** + extract the option construction into `internal static` builders (`BuildFolderOptions(title, IStorageFolder?)`, `BuildOpenOptions(title, filter, multiple)`, `BuildSaveOptions(suggestedName, filter, defaultExt)`) — the pickers themselves cannot run headlessly (§Reality-check #11), the builders can be pinned exactly.
- [ ] **Step 2: Unit tests on the builders**: `".txt"` → DefaultExtension `"txt"`; bare `"json"` unchanged; null filter → null/empty FileTypeFilter; `"All files|*.*|Text|*.txt;*.log"` → 2 FilePickerFileTypes with the right names/patterns; multiselect flag; suggested name pass-through. Plus `ShowOverwritePrompt` left at Avalonia's default (true — matches WPF SaveFileDialog).
- [ ] **Step 3: Gallery picker buttons** (`PickFolderButton` etc.) writing the returned path(s) into `PickerResultText`. These open NATIVE dialogs the bridge cannot drive or screenshot — they exist for manual smoke and for a crash-free open check; the agent does NOT click them in bridge sessions (a native modal would wedge the drive loop). Mark them with a tooltip "manual only".
- [ ] **Step 4:** Full suite gate; record counts. **Commit** — `"feat(avalonia): StorageProvider pickers (filter parser mapping, TrimStart defaultExt, reveal-owner policy)"`

---

### Task 5: ErrorDetailsWindow + ProxyTextDialog + SpeedLimitDialog

Three small text-centric modals, mechanical under §Port rules. SpeedLimit consumes the Task 3 message box for its validation warning; ProxyText/SpeedLimit go live through IDialogService (their VM callers already await them — Phase 1 purification).

**Files:**
- Create: `src/CSUploader.Avalonia/Views/ErrorDetailsWindow.axaml(+.cs)`, `ProxyTextDialog.axaml(+.cs)`, `SpeedLimitDialog.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` (`ShowProxyTextDialogAsync`, `ShowSpeedLimitDialogAsync` real), gallery (+4 buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Views/SimpleDialogTests.cs`

**Port deltas beyond the rules table:**
- **ErrorDetails** (`src/Views/ErrorDetailsWindow.xaml`): read-only wrapping Consolas TextBox on `ContentBackgroundBrush`, Copy via rule 9, Close via rule 7. No service member — Phase 5's EditAccountWindow consumes it; the gallery button is its only opener until then (say so in the class doc).
- **ProxyText** (`src/Views/ProxyTextDialog.xaml(.cs)`): dual-mode ctor `(title, description, initialText, readOnly)`; read-only mode hides OK, shows Copy, and swaps Cancel's content — WPF hardcodes `"Close"` (`ProxyTextDialog.xaml.cs:30`); the port uses `Localizer.Instance["Common_Close"]` (existing key; English-identical, strictly better — note the deviation). Result via `ShowDialog<string?>`: OK → `Close(BodyBox.Text)`, Cancel/Esc/X → `Close(null)` — which collapses the WPF `DialogResult`+`ResultText` pair into one value; the service member becomes `return await dialog.ShowDialog<string?>(await GetOwnerOrRevealAsync());` (read-only mode always yields null, caller ignores — keep the WPF comment).
- **SpeedLimit** (`src/Views/SpeedLimitDialog.xaml(.cs)`): `ShowDialog<SpeedLimitSelection?>` — OK-valid → `Close(new SpeedLimitSelection(limit))`, OK-empty and Clear → `Close(new SpeedLimitSelection(null))` (the "cleared" outcome), Cancel/Esc/X → `Close(null)`. This preserves the two-level nullability contract (`IDialogService.cs:108-116`) WITHOUT the WPF DialogResult dance — the service member is a pass-through. Validation failure (non-positive/garbage int) → `MessageBoxWindow.ShowErrorAsync(this, Localizer["SpeedLimit_Validation_Message"], Localizer["SpeedLimit_Validation_Title"])`, dialog stays open (WPF parity). Focus+SelectAll move to `Opened` (rule 16). `PrimaryButtonStyle` → `Classes="primary"` (rule 14).
- [ ] **Step 1-3:** Port each (XAML + code-behind), wire the two service members, add gallery buttons (`DialogErrorDetailsButton` → direct `ShowDialog(this)` with the synthesized long error; `DialogProxyTextEditButton`/`DialogProxyTextExportButton` and `DialogSpeedLimitButton` → through the injected `IDialogService`).
- [ ] **Step 4: Headless tests:** SpeedLimit — "512"+OK → `LimitKBps == 512`; empty+OK → selection with null; Clear → selection with null; Cancel → null; invalid input keeps the dialog open (assert still visible after OK with "abc" — and that a MessageBoxWindow appeared and can be dismissed). ProxyText — editable OK returns edited text; Cancel → null; read-only mode: OK button `IsVisible == false`, Copy visible. ErrorDetails — ctor text lands in the box; Copy doesn't throw headlessly (clipboard may be a stub — accept no-throw).
- [ ] **Step 5: Bridge session:** shots for `errordetails`, `proxytext-edit`, `proxytext-export`, `speedlimit`, light+dark; interact where drivable (type into SpeedLimit's box via `ava_action` typing verb if available, click Cancel to close). Rebuild + Read contact-sheet pairs vs WPF; fix visible drift or record arbitration notes.
- [ ] **Step 6:** Full suite gate; record counts. **Commit** — `"feat(avalonia): ErrorDetails/ProxyText/SpeedLimit dialogs + ShowProxyTextDialog/ShowSpeedLimitDialog wired"`

---

### Task 6: AboutWindow + CloseActionDialog

Two self-contained dialogs with no IDialogService member; their production openers arrive with the MainWindow menu (Phase 6/7) and close-to-tray (Phase 7) — gallery buttons are the consumers until then.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/AboutWindow.axaml(+.cs)`, `CloseActionDialog.axaml(+.cs)`
- Modify: gallery (+2 buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Views/SimpleDialogTests.cs` (extend)

**Port deltas:**
- **About** (`src/Views/AboutWindow.xaml(.cs)`): `Logo128Image` resolves from the Phase 3 bitmap table (`{StaticResource Logo128Image}` — same key); version line via `Assembly.GetExecutingAssembly()` — **version divergence, accepted for Phase 4:** there is no shared version property anywhere (no Directory.Build.props; the WPF csproj hardcodes its Version inline — 0.0.6 today; the Avalonia csproj declares none), so the Avalonia About renders the assembly default `1.0.0`. Accept and NOTE it on the contact-sheet row (About has no production opener until Phase 6/7); real version alignment belongs to the Phase 9 Velopack-continuity cutover — Task 9 Step 4 adds it to the design's Phase 9 checklist; the GitHub link TextBlock via rule 10 (`PointerReleased`) + `Process.Start(UseShellExecute)` unchanged; the OK button (WPF: IsCancel+IsDefault, NO handler) gets an explicit `Close()` handler per rule 7. Bitmap-scaling rule 11.
- **CloseAction** (`src/Views/CloseActionDialog.xaml(.cs)`): result becomes `internal readonly record struct CloseActionChoice(CloseAction Action, bool Remember)` (in the view file; Phase 7 wires it) via `ShowDialog<CloseActionChoice?>` — MinimizeToTray/Exit → `Close(new(action, RememberCheck.IsChecked == true))`, Cancel/Esc/X → `Close(null)` (the WPF "keep window open, setting unchanged" path). Owner resolution moves OUT of the ctor (WPF resolved it inline) — the Phase 7 caller passes the owner to ShowDialog; gallery passes itself.
- [x] **Step 1-2:** Port both + gallery buttons (`DialogAboutButton`, `DialogCloseActionButton`, direct ShowDialog(this)).
- [x] **Step 3: Headless tests:** CloseAction — Minimize click → `(MinimizeToTray, true)` (checkbox defaults checked, WPF parity); untick+Exit → `(Exit, false)`; Cancel → null. About — opens, version text non-empty, closes on OK.
- [x] **Step 4: Bridge session:** `about` + `closeaction` shots light+dark; open/close via bridge. Contact sheet + Read pairs.
- [x] **Step 5:** Full suite gate; record counts. **Commit** — `"feat(avalonia): About + CloseAction dialogs (gallery-driven; production openers land in Phases 6-7)"`

**Task 6 executed 2026-07-11 (commit 8fdfa49; reviewed/APPROVED same day):** Both dialogs ported per §Port rules; 4 headless tests added (Avalonia 197→201, WPF 1178 unchanged, 0-warning); About/CloseAction light+dark pairs match the WPF references (only accepted divergence: About version 1.0.0.0 vs 0.0.6.0); scope 7 files, WPF head untouched.

---

### Task 7: LogDetailsWindow + HttpDetailsWindow

The TabControl carriers (arbitration already settled in Task 1) and the keyed-style port-rule exercise (prep item 4). HttpDetails goes live through `ShowHttpDetailsAsync` (ConnectionManagerViewModel's per-row proxy-test "Details"); LogDetails waits for Phase 5's LogsView but ports now with both ctor shapes.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/LogDetailsWindow.axaml(+.cs)`, `HttpDetailsWindow.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` (`ShowHttpDetailsAsync` real), gallery (+2 buttons + the synthesized-transaction/log-event factories, mirrored from Task 1's WPF driver)
- Test: `tests/CSUploader.Avalonia.Tests/Views/DetailWindowTests.cs`

**Port deltas:**
- **LogDetails** (`src/Views/LogDetailsWindow.xaml`): the window-local `FieldLabel`/`FieldValue` keyed styles become `<Window.Styles>` class selectors `TextBlock.field-label` / `TextBox.field-value` with **BasedOn dropped** (rule 12 — this is the prep-item-4 exemplar; the class style layers over Fluent's TextBox theme, keep only the four setters: IsReadOnly, FontSize, Padding, `SurfaceMutedBrush` background). `DateTimeFormatConverter` as a window resource — the Avalonia twin exists (Phase 3 Task 6). DataContext = `LogEntryViewModel` (Core), bindings unchanged (`Mode=OneWay` kept). Two-tab TabControl (Text/Html — both read-only Consolas boxes on `CodeBackgroundBrush`, one wrapping one not).
- **HttpDetails** (`src/Views/HttpDetailsWindow.xaml(.cs)`): the code-behind is pure formatting against `HttpTransaction` (Core) — ports near-verbatim (both public ctors + the private shared one; the string building, PrettyPrintJson/ToHexDump calls, Localizer formats are framework-free). XAML: summary Border header + 3-tab TabControl with two nested 4-tab TabControls — all rule-mechanical (scrollbar rule 8 on every box).
- [ ] **Step 1-2:** Port both; wire `ShowHttpDetailsAsync` (`new HttpDetailsWindow(transaction).ShowDialog(await GetOwnerOrRevealAsync())` — Task-returning, completes on close, mirroring `DialogService.cs:156-164`). Gallery buttons: `DialogLogDetailsButton` (synth LogEvent), `DialogHttpDetailsButton` (synth transaction through the REAL service member).
- [ ] **Step 3: Headless tests:** HttpDetails — synthesized transaction populates Summary/Timing/Proxy texts and all eight sub-tab boxes (assert a known substring per box: a header name, the pretty-printed JSON's indentation, a hex-dump offset column); null-transaction ctor path shows the fallback message (`HttpDetails_NoData`). LogDetails — fields bind (DateTime formatted via the converter, thread id, multi-line message in the Text tab).
- [ ] **Step 4: Bridge session:** `logdetails` + `httpdetails` shots light+dark; ALSO drive the tab strip via the bridge (click the Response tab, then a sub-tab — the TabControl interaction proof) before capturing a second angle if useful. Contact sheet + Read pairs against the WPF references; this is where the Task 1 tab arbitration verdict gets its real-view confirmation.
- [ ] **Step 5:** Full suite gate; record counts. **Commit** — `"feat(avalonia): LogDetails + HttpDetails windows (keyed-style→class rule; ShowHttpDetailsAsync wired)"`

---

### Task 8: UpdateProgressWindow + real AvaloniaUpdateProgressSink + ProgressWindow

Prep item 6's second half: UpdateProgressWindow ships through `IUpdateProgressSink` (its own non-modal window, driven by MainViewModel:129-143 — Open, then SetStatus/Report pumped by `Progress<int>`), NOT IDialogService. ProgressWindow ports last and minimal (zero current callers — see disposition table and §Reality-check #13).

**Files:**
- Create: `src/CSUploader.Avalonia/Views/UpdateProgressWindow.axaml(+.cs)`, `ProgressWindow.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaUpdateProgressSink.cs` (no-op → real), gallery (+2 buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Services/UpdateProgressSinkTests.cs`

**Port deltas:**
- **UpdateProgressWindow**: trivial (StackPanel: status TextBlock, ProgressBar 0-100, right-aligned percent TextBlock; Width 420 SizeToContent Height, CanResize False, ShowInTaskbar False). `SetStatus`/`SetProgress` methods identical.
- **Sink** (mirror `WpfUpdateProgressSink.cs` semantics): `Open()` creates the window and shows it **non-modally** — owner = `desktop.MainWindow` if `IsVisible` (`Show(main)`; WPF parity: `Owner = Application.Current?.MainWindow`), else ownerless `Show()` (headless/tray-hidden safe — §Reality-check #12 for `Show(owner)` with a hidden owner). SetStatus/Report forward; Close closes (unused by the current caller — keep the interface-remarks reference). Interface contract: UI thread only, Open at most once per attempt — no locking added.
- **ProgressWindow**: `ExecuteAsync<T>` becomes honestly async — `Opened += async … { result = await func(...); } finally { Close(); }`, then `await progressWindow.ShowDialog(owner)`, then the exception surface via `MessageBoxWindow.ShowErrorAsync(owner, capturedException.ToString(), Localizer["Common_Error"])`. ToolWindow chrome → rule 4 (normal chrome, note). Cancel button relabels via `Progress_BtnCancelling` (existing key). Keep both overloads. Class doc states the orphan status: *"No current callers on either head (unused since the WPF migration); ported for parity as the reusable modal-progress primitive — first consumer TBD."*
- [ ] **Step 1-3:** Port the two windows; make the sink real; gallery buttons: `DialogUpdateProgressButton` toggles the sink through the REAL registered `IUpdateProgressSink` (first click `Open()`+`SetStatus(downloading 1.2.3)`+`Report(42)`, second click `Close()` — a toggle, so the bridge can close it); `DialogProgressButton` runs `ProgressWindow.ExecuteAsync(this, "Synthesized long operation", allowCancel: true, ct => Task.Delay(15000, ct))` — self-closing, and the bridge can click Cancel (which exercises the relabel + cts path).
- [ ] **Step 4: Headless tests (sink):** under the headless lifetime `Open()` takes the ownerless branch — window shown; `SetStatus("x")`/`Report(42)` reflected in the controls (InternalsVisibleTo is in place, csproj:96); `Report` before `Open` is a no-op (null-window guard, WPF parity); `Close()` closes. ProgressWindow: `ExecuteAsync` returns the func result; cancel flips the token and yields default; a throwing func surfaces the message box (assert a MessageBoxWindow opened, then dismiss).
- [ ] **Step 5: Bridge session:** `updateprogress` + `progress` shots light+dark (open via buttons, capture, close via toggle/Cancel — all bridge-driven). Contact sheet + Read pairs (ProgressBar look vs the Task 1 arbitration verdict).
- [ ] **Step 6:** Full suite gate; record counts. **Commit** — `"feat(avalonia): UpdateProgress window + real IUpdateProgressSink; ProgressWindow ported (currently caller-less, flagged)"`

---

### Task 9: Phase gate — review, tag, reconcile

- [ ] **Step 1: Whole-diff review**: `git diff phase3-primitives-ready..HEAD` by a fresh adversarial reviewer (whole-diff panels catch cross-task issues — the Order-column precedent; per-task reviews already happened).
- [ ] **Step 2: Gates**:
  - `grep -rn "System.Windows" src/CSUploader.Avalonia/` → zero; `src/CSUploader.Core/` → still zero.
  - `grep -n "NotImplementedException" src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` → **exactly 3** (ShowAddAccountDialogAsync, ShowEditAccountDialogAsync, ShowEditProxyDialogAsync — the Phase 5 members), each message naming its Phase 5 window.
  - Both suites green; final counts recorded (1178+ / 147+additions).
  - i18n gate green; the phase diff shows **zero `Strings*.resx` changes**.
  - WPF-head safety: `git diff phase3-primitives-ready..HEAD -- src/` limited to `src/CSUploader.Avalonia/**` + `src/Services/ReferenceShotCapture.cs` + `src/App.xaml.cs` (+ tests); the two WPF files' deltas are `#if DEBUG`-fenced; Release WPF build succeeds and `--shots --dialogs` is dead in it (launch once, window stays open, close).
  - Avalonia Release build succeeds; launched WITHOUT flags: no gallery, no dialog artifacts, dialogs unreachable except through real app flows.
  - Contact sheet complete: all 11 paired view names have all four cells (both themes × both sides) + the two ava-only messagebox rows; every pair Read and arbitrated (list any accepted divergences).
- [ ] **Step 3:** `git tag phase4-dialogs-ready`.
- [ ] **Step 4: Reconcile the design doc** with Phase 4's outcomes — at minimum: the modal-addressing verdict (or the in-app capture fallback if taken), the TabControl/ProgressBar arbitration verdict + any re-templates, the MessageBox system-icon deviation ruling, the owner-resolver final shape (incl. the reveal helper), the picker manual-verification deferral, ProgressWindow's orphan note, headless ShowDialog test findings, and the About version divergence — **add "Avalonia csproj takes the app Version (About renders 1.0.0 until then)" to the design's Phase 9 cutover checklist** (§Phases step 3, next to the AssemblyName/packId items) if not already present. Commit — `"docs: reconcile design with Phase 4 outcomes (dialogs, owner policy, picker mapping)"`.
- [ ] **Step 5: Surface to the maintainer** (via the team lead): the contact-sheet path + one-line driver usage (`--shots --dialogs` / `--agent --gallery` buttons); the picker buttons awaiting his manual click-through (native dialogs, agent can't drive them); the ProgressWindow orphan finding (delete it instead? his call — it costs ~150 lines to keep); the MessageBox icon deviation; standing reminders (Phase 1 merge-back if still unmerged; Buzzheavier master-merge checklist incl. BitmapImageResources).

---

## Reality-check register

Things this plan cites that the implementer must verify against the installed bits before/while coding — the plan could not pin them from source:

1. **Avalonia `Button.IsCancel`/`IsDefault` semantics on 11.3.18** — assumed: Esc raises the IsCancel button's Click, Enter the IsDefault one, and NEITHER auto-closes the window (hence rule 7's explicit `Close()` handlers, incl. the new one on AboutWindow's OK). Verify with the first ported dialog; if IsCancel DOES auto-close, drop the redundant handlers and update rule 7.
2. **`Window.Clipboard` availability** (rule 9) on 11.3.18 — else `TopLevel.GetTopLevel(this)?.Clipboard`.
3. **WPF `Show()` (not ShowDialog) for reference shots** — dialogs are captured non-modally; verify the first `SizeToContent` dialog renders/measures identically under `Show()` (Task 1 Step 2's Read). If one misbehaves, fall back to the design's alternative: posted capture inside `ShowDialog` (dispatcher-posted CaptureWindow + Close while the modal pump runs).
4. **`LogEvent` construction shape** for the synthesized drivers (Task 1/7) — mirror whatever `IAppLogger` actually builds; adapt the factory, not the window.
5. **`ava_screenshot` modal addressing** — verified EARLY by Task 1 Step 5 with a placeholder modal (also: can `ava_find`/`ava_action` reach controls INSIDE a modal?). Fallback documented in-step: DEBUG in-app Avalonia `RenderTargetBitmap` capture on the launcher path; record whichever holds.
6. **Headless `Activate()`/`IsActive`** for the owner-resolver tests (Task 2 Step 2) — per-test fallback to driving the pure `Resolve` core with hand-built window states; record which cases fell back.
7. **Reveal-then-resolve timing** (Task 3 Step 2) — whether `ITrayIconService.ShowMainWindow()`'s `Show()`+`Activate()` makes the window immediately resolvable, or the helper needs a dispatcher hop / `Opened` await before re-resolving.
8. **Headless `await ShowDialog<T>()` + input-simulation interplay** (Task 3 Step 4) — Phase 3 Task 8 proved MouseDown/MouseUp click delivery; unproven: whether an awaited ShowDialog task completes cleanly in the same test method with `RunJobs` pumping. Per-test fallback: `Show()` + handler invocation + `Closed` assertion; record which.
9. **Startup-failure dialog path** (Task 3 Step 6) is verified by inspection/review only — it cannot be forced live without corrupting a scratch environment mid-startup; if a cheap forcing seam appears during implementation (e.g. an invalid DB path via settings), use it once and record.
10. **`TryGetFolderFromPathAsync` on missing/invalid paths** returns null (not throws) on 11.3.18 — guard regardless (Task 4).
11. **StorageProvider under headless** — assumed unusable (native shell dialogs); hence option-builder-only tests + the maintainer's manual smoke. If headless exposes a fake provider worth asserting against, use it and record.
12. **`Show(owner)` with a hidden owner** (Task 8 sink) — verify it doesn't throw like ShowDialog does; the sink guards with `IsVisible` regardless.
13. **ProgressWindow is caller-less** (plan-time finding: no `ProgressWindow.ExecuteAsync` references anywhere on either head or master; orphaned since commit 6d68070). Ported minimal for design parity. Decision surfaced to the maintainer at the gate: keep as primitive vs delete on both heads. If a master merge lands a caller mid-phase, re-check.
14. **`SizeToContent="Height"` + fixed Width on Avalonia 11.3.18 windows** (Confirmation/CloseAction/UpdateProgress/About) — historically finicky; verify the first such dialog sizes correctly on screen AND in headless tests; fallback: explicit Height per dialog, recorded.
15. **Avalonia suite baseline 147** — confirm at Task 1's first gate (Phase 3's final commits landed after the 137 count was recorded); correct the Global Constraints number if it drifted, then it only goes up.
16. **`TryGetLocalPath()` non-null for picked items** on local-disk picks (Task 4) — null only for non-filesystem providers; treat null as cancel and log nothing (parity with WPF's always-a-path behavior).
