# Avalonia Migration Phase 5: Medium Views — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Avalonia head's medium views real: UploadedView (grouped DataGrid — the design's #1 remaining DataGrid risk, probed first), LogsView (4 log tabs), EditProxyWindow, and EditAccountWindow — with the Uploaded and Logs MainWindow tabs going live, the shared grid infrastructure this phase introduces (Avalonia column-visibility persistence twin, shared column-toggle menu, shared zebra-striping helper), the two editor dialogs wired through `AvaloniaDialogService` so its **`NotImplementedException` count reaches ZERO**, and every view verified by headless interaction tests plus WPF-vs-Avalonia reference shots in the phase contact sheet.

**Architecture:** Strangler step 5 (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, §Phases "Phase 5" — grouping FIRST as the DataGridCollectionView risk probe, then LogsView, EditProxyWindow, EditAccountWindow). The design's Phase 5 line carries **12 PREP ITEMS** from the Phase 4 gate; every one is a task or an explicit step here (§Prep-item coverage). The Core ViewModels are **read-only this phase** — UploadedViewModel, LogsViewModel, ConnectionManagerViewModel and SettingsViewModel were purified in Phase 1 and already expose everything the views bind (`Files`, `SelectedRow`/`SelectedRows`, `SettingRepo`, `DialogServiceForView`, the commands, the four log collections, `AutoScroll`); if a port seems to need a VM change, STOP and surface it to the team lead. The one Core addition is the design-mandated `HosterCredentialModes` hoist (prep item 4) plus a one-line `InternalsVisibleTo` grant (§Reality findings — the Avalonia head cannot read the VMs' internal `SettingRepo`/`DialogServiceForView` today).

**Tech Stack:** unchanged — .NET 10, Avalonia **11.3.18** + Avalonia.Controls.DataGrid **11.3.13** (grouping via `Avalonia.Collections.DataGridCollectionView` + `DataGridPathGroupDescription`, both already in the referenced packages) + Avalonia.Themes.Fluent + Avalonia.Svg.Skia 11.3.0, Avalonia.Headless.XUnit 11.3.18, CommunityToolkit.Mvvm 8.4.2 (Core). **This phase adds NO packages.** Bridge via `scripts/ava-drive.cs`; contact sheet via `scripts/contact-sheet.py`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase4-dialogs-ready`. Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree, has uncommitted Buzzheavier work).
- **Suite gate after every task** (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — **1178 green at phase start**; the count only goes up, never down.
  - `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — **223 green at phase start** (confirm the exact number at Task 1's gate and correct it here if it drifted); most Phase 5 tasks raise this count — record each new baseline and carry it forward.
  - Separate OutDirs are mandatory (shared OutDir mixes WPF and Avalonia assemblies and breaks discovery). Never run bare solution-level `dotnet test -p:OutDir=…`.
- Head builds: Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`; WPF `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf`. Scratch DBs live beside those exes; seed with `dotnet run scripts/seed-fake-data.cs -- <outdir>` (idempotent — Task 1 extends the seed, so Task 1 DELETES both scratch `CSUploader.db` files and reseeds; later tasks reseed only after destructive drives like Remove).
- Every csproj keeps `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`. Version pins are hard; do not bump anything.
- **Core ViewModels are untouched this phase** (see §Architecture). Core gains exactly two things: `HosterCredentialModes` (Task 7) and `<InternalsVisibleTo Include="CSUploader.Avalonia" />` (Task 3).
- **The WPF head is touched by exactly two files this phase**: `src/Services/ReferenceShotCapture.cs` (Task 1 — editor shot matrix, inside the existing `#if DEBUG` envelope) and `src/Views/EditAccountWindow.xaml.cs` (Task 7 — consumes the Core `HosterCredentialModes` hoist; behavior byte-identical, the full WPF suite is the regression net). Anything else touching `src/` outside `src/CSUploader.Avalonia/**` and `src/CSUploader.Core/**` is a plan violation.
- **i18n: NO new keys this phase.** Every string the ports need already exists (`Uploaded_*`, `Logs_*`, `EditProxy_*`, `EditAccount_*`, `Settings_Conn_Col_*`, `Settings_Conn_Btn_Details`, `Uploads_ColumnMenu_*`, `Common_*`). Gallery/driver text stays hardcoded English (dev-tool convention). The phase-gate diff must show **zero `Strings*.resx` changes**. Never hand-edit resx.
- **Agent-safety** (unchanged): Avalonia launches for bridge work always pass `--agent`; scratch DBs only; never copy a real `CSUploader.db`; all driver/gallery data is synthesized or seed-fake; **never click the picker/Export buttons in a bridge session** (native modals wedge the drive loop — Phase 4 rule, now also covers UploadedView's Export); EditProxy's Test button fires a real (localhost, fail-fast) connection — don't drive it through the bridge either, its logic is covered by validation tests.
- **ava-drive gotchas** (Phase 2-4 experience): `ava_action`'s argument is **`verb`, not `action`**; find/search tools return a **bare JSON array**; handshake discovery picks the newest live handshake — close forgotten bridge apps first; single-driver lock (no MCP attach while ava-drive runs); pass explicit `maxWidth` (2500) for wide-view screenshots; **snapshot `desktop.Windows` to a list BEFORE calling `Close()` in an `ava_eval` drive** (closing mid-enumeration throws "Collection was modified" — Phase 4 Task 7/8 hazard); windows without a Close affordance are closed via `ava_eval` on the snapshotted list.
- **Shots convention** (extends Phase 4): `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png`. Phase 5 view names (identical on both sides so the contact sheet pairs them): `editaccount-classic`, `editaccount-apikey`, `editaccount-cookie`, `editaccount-error`, `editproxy`, `editproxy-tested`, `mainwindow-uploaded`, `mainwindow-logs`. The `mainwindow-*` WPF cells are RE-captured in Task 1 with the extended seed (this also refreshes `mainwindow-uploads`/`mainwindow-settings` — harmless; their Avalonia cells stay placeholders until Phase 6).
- `[AvaloniaFact]` discipline (Phase 3 rule): tests that flip theme/culture/`SuppressedConfirmations` or open windows restore process-global state in `finally` (close every window opened; snapshot the window list before closing).
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited file:line and copy the semantics exactly. Where this plan could not pin an Avalonia API shape against the installed bits, the step says so and §Reality-check register lists it. For DataGrid internals (group header template parts, copy plumbing), `dotnet-skills:ilspy-decompile` on the installed Avalonia.Controls.DataGrid 11.3.13 assembly is the sanctioned way to read the real shapes.

### Prep-item coverage (the 12 items from the Phase 4 gate, design §Phases "Phase 5")

| # | Prep item | Where |
|---|-----------|-------|
| 1 | Shot-drivers-first: extend the WPF `--dialogs` driver with the editor-mode matrix (editaccount-classic/apikey/cookie/error, editproxy/editproxy-tested; null callback = disabled Sign-in = the shot-able state) BEFORE porting | Task 1 |
| 2 | Extend seed-fake-data with 2-3 more completed packages (3-5 files, mixed hosters, some empty FileUrls) — the grouping probe needs multiple groups | Task 1 |
| 3 | Author `Button.dialog`/`Button.save` + `field-label`/`field-input`/`field-combo` classes ONCE in BaseStyles (~85 duplicated lines per WPF editor; a recorded deviation from the per-window rule) | Task 7 |
| 4 | HOIST ApiKeyHosters/SessionCookieHosters from EditAccountWindow code-behind into Core (`HosterCredentialModes`) — kills the new-hoster drift trap, enables headless mode-switch tests | Task 7 |
| 5 | COLUMN PERSISTENCE DECISION: the Avalonia DataGridColumnVisibilityPersistence twin lands in Phase 5 with its first consumers (all five Phase 5 grids wire it structurally); Phase 7 keeps only UploadsView-specific pieces | Task 3 (twin + menu helper); consumed in Tasks 5, 6 |
| 6 | GROUPING-PROBE numbered checklist (DataGridPathGroupDescription/DataGridCollectionView in the HEAD; RowGroupTheme fidelity; collapse + state survival; built-in Ctrl+C via ClipboardCopyMode) | Task 2 (go/no-go) |
| 7 | ElementStyle GAP port rule: Avalonia DataGridTextColumn has no ElementStyle — TextTrimming/per-cell ToolTip/alignment map to CellStyleClasses or template columns, decided once against the first grid | §Port rules row 20; exercised/recorded in Task 5 |
| 8 | Shared zebra helper (LoadingRow/UnloadingRow index classes), consumed by the Phase 5 grids — the 4 LogsView grids + the GROUPED UploadedView (this plan reads the design's "5 log grids" as those five grids, matching prep 5's "all five Phase 5 grids"); alternation-vs-groups parity decided against the WPF reference shot | Task 4 (helper); Task 2 checklist 7 (basis); Tasks 5, 6 (consumers) |
| 9 | SECRET MASKING sharpened: the editors populate boxes from code-behind (no VM binding) so PasswordChar is the ONLY bridge-masking lever — Password/ApiKey boxes get PasswordChar (recorded deviation from WPF's cleartext) | Task 8 (proxy password), Task 9 (account password + API key); masking verified via `ava_props` in each bridge session |
| 10 | EditAccountWindow task structure: the 7 carry-fields as an explicit edit-without-signin test matrix, a fake-callback harness for the three sign-in outcomes, nullable window ctor (service passes non-null through) | Task 9 |
| 11 | Port-deltas: LogsView Enter needs TUNNEL KeyDown, MouseDoubleClick→DoubleTapped with clicked-row hit-test, URL cell → Classes+`:pointerover`+IsNotNullOrEmpty, Delete KeyBinding ports, ContextMenu PlacementTarget/RelativeSource bindings do NOT port | §Port rules rows 19, 22-25; exercised in Tasks 5, 6 |
| 12 | UploadedView right-click select (row+group) is Phase 5 view code-behind; the SelectRowOnRightClick behavior's consumer is UploadsView (Phase 6) | Task 5 Step 3 |

**Standing design notes carried into this phase** (design §Phases Phase 5 PORT RULES/SECURITY lines): (a) `ClearSelectionOnEmptyClick` grids need a non-null (themed) `Background` or empty-clicks fall through hit-testing; (b) group-header LEFT-clicks CLEAR selection in WPF today (the header isn't a row, so the empty-click behavior fires) — keep parity, changing it is a product decision; (c) the Phase 3 behaviors get their interaction verification when their first consuming grid ships — `ClearSelectionOnEmptyClick` in Task 5, `AutoScrollBehavior` in Task 6 (`SelectRowOnRightClick` waits for UploadsView, Phase 6); (d) the bridge redactor masks by VM property name and by PasswordChar boxes — the editors have no VM bindings, hence prep item 9.

### Port rules (rows 18+ extend the Phase 4 standing table — `docs/superpowers/plans/2026-07-12-avalonia-phase4-dialogs.md` §Port rules rows 1-17 still apply verbatim)

| # | WPF | Avalonia |
|---|-----|----------|
| 18 | `PreviewMouseRightButtonDown` (select target row before the menu) then `ContextMenuOpening` (snapshot/suppress) | TUNNEL `PointerPressed` handler (`RoutingStrategies.Tunnel`, right-button guard) for selection, then `ContextMenu.Opening` (CONFIRMED cancelable on 11.3.18 — §Reality-check #5) for snapshot/suppression — same ordering guarantee re-established |
| 19 | `Command="{Binding PlacementTarget.DataContext.XCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}"` | plain `Command="{Binding XCommand}"` — an Avalonia ContextMenu attached via `Control.ContextMenu` inherits the host control's DataContext. `CommandParameter="{Binding PlacementTarget.SelectedItems…}"` → assign in code-behind after `InitializeComponent`: `MenuItemName.CommandParameter = Grid.SelectedItems` (the grid's `SelectedItems` is one live `IList` for the control's lifetime, exactly what the WPF binding resolved to; `#ElementName` bindings inside popups are a name-scope hazard, §Reality-check #6) |
| 20 | `DataGridTextColumn.ElementStyle` (trim/tooltip/alignment/brush per cell) | **CellStyleClasses + view-scoped descendant styles**: set `CellStyleClasses="path-cell"` on the column and style `DataGridCell.path-cell TextBlock` in the view's `Styles` (TextTrimming, Foreground, FontFamily, HorizontalAlignment, and `ToolTip.Tip` via a Binding **setter** — resolves against the row item, §Reality-check #7). Template columns ONLY where the cell composes content (icon + text). Decided once here (prep item 7); Task 5 records the verdict |
| 21 | `AlternatingRowBackground` + `AlternationCount=2` | shared `DataGridZebraStriping` helper (Task 4): LoadingRow/UnloadingRow toggling an `alt` row class + a view style `DataGridRow.alt { Background=… }` (recycling-safe; Avalonia has no AlternatingRowBackground) |
| 22 | `MouseDoubleClick` on the grid | `DoubleTapped` + `(e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)` hit-test — open what was clicked, not `SelectedItem` (mirrors LogsView.xaml.cs:131-141) |
| 23 | `PreviewKeyDown` Enter (beat the grid's own Enter) | `AddHandler(InputElement.KeyDownEvent, handler, RoutingStrategies.Tunnel)` + `e.Handled = true` |
| 24 | `DataGrid.InputBindings` / `KeyBinding Key="Delete"` | `DataGrid.KeyBindings` / `<KeyBinding Gesture="Delete" Command="{Binding …}" CommandParameter=…/>` (parameter wired per rule 19 where it needs SelectedItems) |
| 25 | `Style.Triggers` `IsMouseOver` + null/empty `DataTrigger` → Collapsed | classes + `:pointerover` pseudo-class styles; visibility on null/empty string → `IsVisible="{Binding X, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"` |
| 26 | `CollectionViewSource` + `PropertyGroupDescription` in view XAML resources | `DataGridCollectionView` + `DataGridPathGroupDescription` built in the view **code-behind** on `DataContextChanged` (Avalonia.Collections cannot live in Core; the VM keeps the raw `ObservableCollection` — Phase 1's ICollectionView purge contract). Decision recorded by Task 2 checklist 1 |
| 27 | `DataGrid.GroupStyle` GroupItem ControlTemplate (flat ToggleButton chevron + name + `[n]`) | re-templated `DataGridRowGroupHeader` ControlTheme in the view, keyed (e.g. `x:Key="GroupHeaderTheme"`) and **wired explicitly via `DataGrid.RowGroupTheme="{StaticResource GroupHeaderTheme}"`** (property confirmed on 11.3.13; do NOT rely on implicit `{x:Type}`-keyed pickup). Recipe pinned by Task 2: flat − / + chevron, SemiBold name, ItemCount as `[{0}]`, `SublevelIndent 0`, transparent background |
| 28 | `Hyperlink` inline + Click | `TextBlock` with `Classes="link"` (AccentBrush, hand cursor, `:pointerover` underline) + `PointerReleased` (rule 10's button guard) |
| 29 | code-behind brush swap via `FindResource("ErrorBrush")` (EditProxy `SetStatus`, EditAccount sign-in status) | toggle a class instead: `StatusText.Classes.Set("error", isError)` + view class styles carrying DynamicResource brushes — theme-live, per the Phase 4 prep-7 rule (converter/code-resolved brushes don't track theme flips) |
| 30 | `SelectionUnit="FullRow"` + default multi-select | `SelectionMode="Extended"` (Avalonia DataGrid is always full-row; `SelectionMode Single` where WPF said so — the log grids) |
| 31 | `MenuItem IsCheckable="True"` + `IsChecked` | `MenuItem ToggleType="CheckBox"` (`MenuItemToggleType.CheckBox`) + `IsChecked` — Avalonia renders NO check glyph from `IsChecked` alone; confirmed on 11.3.18 |

### View disposition (dependency order)

| WPF source (src/Views/, src/Lib/UI/) | Avalonia deliverable | Consumer / service member | Task |
|---|---|---|---|
| — (new infra) | WPF `--shots --dialogs` editor matrix + extended seed | reference cells for Tasks 5/6/8/9 | 1 |
| UploadedView grouping (UploadedView.xaml:26-30, 207-270) | DevTools GroupingProbeWindow (throwaway; deleted in Task 5) | go/no-go for the whole UploadedView approach | 2 |
| Lib/UI/DataGridColumnVisibilityPersistence.cs | Avalonia twin + shared `DataGridColumnMenu` helper + Core IVT line | Tasks 5 & 6 wire all five grids | 3 |
| `AlternatingRowBackground` usages | shared `DataGridZebraStriping` helper | Tasks 5 & 6 | 4 |
| UploadedView.xaml(+.cs) | Views/UploadedView.axaml(+.cs) + **Uploaded tab live** | binds `MainViewModel.UploadedViewModel` (MainViewModel.cs:155) | 5 |
| LogsView.xaml(+.cs) | Views/LogsView.axaml(+.cs) + **Logs tab live** | binds `MainViewModel.LogsViewModel` (MainViewModel.cs:161) | 6 |
| EditProxyWindow/EditAccountWindow window-local styles; EditAccountWindow.xaml.cs:37-48 hoster sets | BaseStyles editor classes + Core `HosterCredentialModes` | Tasks 8 & 9 (and the WPF head re-points) | 7 |
| EditProxyWindow.xaml(+.cs) | Views/EditProxyWindow.axaml(+.cs) | `ShowEditProxyDialogAsync` ← ConnectionManagerViewModel:238 | 8 |
| EditAccountWindow.xaml(+.cs) | Views/EditAccountWindow.axaml(+.cs) | `ShowAddAccountDialogAsync` ← UploadWizardViewModel:832; `ShowEditAccountDialogAsync` ← SettingsViewModel:739, :1204 — **NotImplemented count → 0** | 9 |
| — | phase gate, tag `phase5-medium-views-ready` | — | 10 |

---

### Task 1: Editor shot-driver matrix + seed extension + WPF reference re-capture

Prep items 1 and 2 — the reference cells exist BEFORE any port, and the seed produces enough groups for the probe. WPF head touched only inside `ReferenceShotCapture.cs`'s existing `#if DEBUG` envelope.

**Files:**
- Modify: `src/Services/ReferenceShotCapture.cs` (6 editor entries appended to `DialogFactories`, :145-184)
- Modify: `scripts/seed-fake-data.cs` (3 more completed packages after the `done` package, :160-172; update the summary line :176)

**Interfaces:**
- Produces: the 6 editor reference names ×2 themes (12 PNGs) + re-captured `mainwindow-*` tab references with the extended seed; a seed with **5 packages / 17 files** (4 completed packages → 4-5 groups on the Uploaded tab, several rows with empty `FileUrl`).
- Consumes: `EditAccountWindow`/`EditProxyWindow` (WPF) ctors + their internal x:Name fields (same assembly — the Phase 4 `progress`/`updateprogress` factories already set named elements post-construction, :163-180).

- [ ] **Step 1: Editor factories.** Append to `DialogFactories` (all data fake; `interactiveLogin: null` on every EditAccountWindow — WPF then disables Sign-in and shows the `EditAccount_SignIn_Unavailable` hint, the shot-able state per prep item 1):

```csharp
("editaccount-classic", static () => new EditAccountWindow(
    new FileHosterLoginDto
    {
        Id = 1, // edit mode → locked-hoster border (EditAccountWindow.xaml.cs:109-117)
        FileHosterName = "Rapidgator",
        Username = "fake_rg_user",
        Password = "not-a-real-password",
        AccountType = AccountType.Premium,
    },
    ["Rapidgator", "KatFile", "Isracloud"])),
("editaccount-apikey", static () => new EditAccountWindow(
    new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free, ApiKey = "fake-api-key-0123456789abcdef" },
    ["Rapidgator", "KatFile", "Isracloud"])),
("editaccount-cookie", static () => new EditAccountWindow(
    new FileHosterLoginDto { FileHosterName = "Isracloud", AccountType = AccountType.Free },
    ["Rapidgator", "KatFile", "Isracloud"])),
("editaccount-error", static () =>
{
    EditAccountWindow w = new(
        new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free },
        ["Rapidgator", "KatFile", "Isracloud"]);
    // The error state ShowSignInError produces (EditAccountWindow.xaml.cs:227-237), poked
    // via the internal x:Name fields — the same post-construction technique as `progress`.
    w.SignInStatus.Visibility = Visibility.Collapsed;
    w.SignInErrorPanel.Visibility = Visibility.Visible;
    w.SignInErrorText.Text = string.Format(
        CultureInfo.CurrentCulture, "{0}: {1}",
        Localizer.Instance["Common_Error"], "Sign-in failed: invalid credentials");
    return w;
}),
("editproxy", static () => new EditProxyWindow(
    new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080, Username = "fake_proxy_user", Password = "not-a-real-password", Enabled = true })),
("editproxy-tested", static () =>
{
    EditProxyWindow w = new(
        new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080, Enabled = true });
    // The post-Test look (EditProxyWindow.xaml.cs:87-89, :108): OK status line + Details button.
    w.TestStatusText.Text = string.Format(
        CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_OkLatencyIp_Format"], 142, "203.0.113.7");
    w.TestStatusText.Visibility = Visibility.Visible;
    w.TestDetailsButton.Visibility = Visibility.Visible;
    return w;
}),
```

  Add the needed `using CSUploader.Dal;` / `using CSUploader.Upload;` if missing. Namespace note: `ProxyType` lives in `CSUploader.Lib.Net` (EditProxyWindow.xaml.cs:12) — mirror its usings.
- [ ] **Step 2: Seed extension.** After the `done` package (seed-fake-data.cs:160-172), add three more completed packages — mixed hosters, small files, and **three rows with NO url** (the URL-cell-hidden case the UploadedView port must show):

```csharp
UploadPackageDbm photos = new()
{
    Name = "Fake pack (photos)",
    CreatedDateTime = DateTime.Now.AddDays(-2),
    IsCompleted = true,
    Files =
    [
        File1("fake_beach.jpg", 1, FileState.Completed, catbox.Id, "Catbox", 1, url: "https://files.catbox.moe/fake02.jpg"),
        File1("fake_sunset.png", 2, FileState.Completed, catbox.Id, "Catbox", 2, url: "https://files.catbox.moe/fake03.png"),
        File1("fake_family.tif", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 3, url: "https://rapidgator.net/file/fake000002"),
        File1("fake_pano.raw", 2, FileState.Completed, rapidgator.Id, "Rapidgator", 4), // no url — URL cell hidden
    ],
};
UploadPackageDbm documents = new()
{
    Name = "Fake pack (documents)",
    CreatedDateTime = DateTime.Now.AddDays(-3),
    IsCompleted = true,
    Files =
    [
        File1("fake_report.pdf", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 1, url: "https://rapidgator.net/file/fake000003"),
        File1("fake_specs.docx", 1, FileState.Completed, catbox.Id, "Catbox", 2, url: "https://files.catbox.moe/fake04.docx"),
        File1("fake_budget.xlsx", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 3), // no url
    ],
};
UploadPackageDbm archives = new()
{
    Name = "Fake pack (archive set)",
    CreatedDateTime = DateTime.Now.AddDays(-4),
    IsCompleted = true,
    Files =
    [
        File1("fake_part1.rar", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 1, url: "https://rapidgator.net/file/fake000004"),
        File1("fake_part2.rar", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 2, url: "https://rapidgator.net/file/fake000005"),
        File1("fake_part3.rar", 3, FileState.Completed, catbox.Id, "Catbox", 3, url: "https://files.catbox.moe/fake05.rar"),
        File1("fake_part4.rar", 2, FileState.Completed, catbox.Id, "Catbox", 4, url: "https://files.catbox.moe/fake06.rar"),
        File1("fake_readme.txt", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 5), // no url
    ],
};
ctx.UploadPackages.AddRange(photos, documents, archives);
```

  Fold them into the existing `SaveChanges()`/summary flow and update the final line to `"Seeded {dbPath}: 2 logins, 5 packages, 17 files (…)"`. States are all `Completed` (inside the settled-set runtime guard, :113-116 — do not touch the guard).
- [ ] **Step 3: Reseed both scratch dirs.** Delete `D:\temp2\cbuild-mig\wpf\CSUploader.db` and `D:\temp2\cbuild-mig\ava\CSUploader.db` (the seed skips already-seeded DBs, :68-72), then `dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\wpf` and `… -- D:\temp2\cbuild-mig\ava`. Expected: both report 5 packages / 17 files.
- [ ] **Step 4: Capture.** Build WPF (Debug, wpf OutDir). Run `D:\temp2\cbuild-mig\wpf\CSUploader.exe --shots --dialogs` → the 11 Phase 4 names re-capture plus **12 new editor PNGs**; then run `…\CSUploader.exe --shots` → the 4 `mainwindow-*` tab references re-capture with the extended seed. Read `editaccount-classic-light-wpf.png` (locked hoster border, filled cleartext U/P — this is the masking-deviation reference), `editaccount-error-dark-wpf.png` (error panel + Details link), and `mainwindow-uploaded-light-wpf.png` (5 package groups, some rows without URLs).
- [ ] **Step 5:** Rebuild the contact sheet (`python scripts/contact-sheet.py`) — new editor rows single-sided (ava missing), expected.
- [ ] **Step 6:** Full suite gate (confirm the 1178/223 baselines; correct §Global Constraints if drifted). **Commit** — `"dev: editor reference-shot matrix (--shots --dialogs) + seed gains 3 completed packages for the grouping probe"`

---

### Task 2: Grouping probe — GO/NO-GO

Prep item 6. The design flags DataGrid grouping as the top remaining risk (design §Risks 3) and orders it FIRST; this task pins the whole recipe on a throwaway DevTools window before UploadedView invests in it. **If checklist items 1-3 cannot reach "close and consistent" against the WPF reference, STOP: do not start Task 5 — escalate to the team lead with the evidence PNGs (design fallback: ItemsControl-based grouped list, which re-scopes Task 5).**

**Files:**
- Create: `src/CSUploader.Avalonia/DevTools/GroupingProbeWindow.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (+1 launcher `GroupingProbeButton`, non-modal `Show(this)`)
- Test: `tests/CSUploader.Avalonia.Tests/Views/GroupingProbeTests.cs`

**Interfaces:**
- Produces: the pinned grouping recipe (view-construction code + `DataGridRowGroupHeader` ControlTheme) that Task 5 copies into UploadedView; recorded verdicts for checklist items 1-7 (in the task notes + Task 10's design reconcile). The window itself is deleted by Task 5.
- Consumes: `Avalonia.Collections.DataGridCollectionView`, `DataGridPathGroupDescription`, the Phase 3 theme brushes.

- [x] **Step 1: Probe window.** A plain `Window` (900×500) holding one DataGrid with 3 text columns (Name/Size/URL) + `ClipboardCopyMode="IncludeHeader"`, fed in code-behind:

```csharp
private sealed record ProbeRow(string PackageName, string FileName, string Size, string? Url);

// 3 groups, uneven sizes, one null Url — the UploadedView shape in miniature.
private static readonly ProbeRow[] Rows =
[
    new("Fake pack (photos)", "fake_beach.jpg", "1.0 MB", "https://files.catbox.moe/fake02.jpg"),
    new("Fake pack (photos)", "fake_sunset.png", "2.0 MB", "https://files.catbox.moe/fake03.png"),
    new("Fake pack (photos)", "fake_pano.raw", "2.0 MB", null),
    new("Fake pack (documents)", "fake_report.pdf", "1.0 MB", "https://rapidgator.net/file/fake000003"),
    new("Fake pack (documents)", "fake_specs.docx", "1.0 MB", "https://files.catbox.moe/fake04.docx"),
    new("Fake pack (archive set)", "fake_part1.rar", "3.0 MB", "https://rapidgator.net/file/fake000004"),
    new("Fake pack (archive set)", "fake_part2.rar", "3.0 MB", "https://rapidgator.net/file/fake000005"),
];

private static DataGridCollectionView BuildView()
{
    DataGridCollectionView view = new(Rows);
    view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(ProbeRow.PackageName)));
    return view;
}
```

  (Type/member names CONFIRMED by the plan review — §Reality-check #1; the probe validates runtime behavior, not the API surface.) Attach the zebra-relevant pieces too: give the grid a themed `Background` and note row indices.
- [x] **Step 2: Re-template the group header.** Author a `DataGridRowGroupHeader` ControlTheme in the window's resources targeting the WPF group bar (UploadedView.xaml:207-270): flat `ToggleButton` chevron rendering `−`/`+` (14px bold, `TextSecondaryBrush`), group name SemiBold `TextPrimaryBrush`, item count as `[{0}]` in `TextSecondaryBrush`, `MinHeight 20`, `Padding 12,0`, transparent background, `SublevelIndent 0`. Give it an explicit key and **wire it via `DataGrid.RowGroupTheme="{StaticResource GroupHeaderTheme}"`** (rule 27 — the property is confirmed on 11.3.13; implicit-keyed pickup is not the mechanism). Start from the installed generic theme (ILSpy or the Avalonia source for 11.3.13's `DataGridRowGroupHeader` template) and cut it down — the template's part names (expander button, item-count presenter) are load-bearing (§Reality-check #2).
- [x] **Step 3: Bridge session — the numbered checklist.** Build, launch `--agent --gallery`, open the probe via `GroupingProbeButton`, and record a verdict per item (screenshots `_probe-*.png` as evidence):
  1. `DataGridCollectionView` + `DataGridPathGroupDescription` compile and group correctly, built in HEAD code-behind (record the decision: code-behind on `DataContextChanged`, no head-side wrapper class — revisit only if Task 5 finds a second consumer).
  2. Group-header fidelity vs `mainwindow-uploaded-light-wpf.png`'s package bars (chevron flips −/+, name, `[n]`, flat/transparent, rows flush under the column headers — no sublevel indent).
  3. Interactive collapse via a bridge click on the chevron; collapse state survives scrolling; note what a full `ItemsSource` rebuild does to collapse state (WPF parity: LoadAsync rebuilds → groups re-expand).
  4. Built-in **Ctrl+C on a grouped view**: select 2 rows, send Ctrl+C (headless key raise or bridge), read the clipboard via `ava_eval` — header + 2 tab-separated rows expected (`ClipboardCopyMode` EXISTS on 11.3.13; the stale design bullet was corrected at the gate). Record whether a synthetic `KeyDown` raised on the grid triggers the internal copy path — Task 5's context-menu Copy depends on the answer (§Reality-check #3).
  5. Group-header right-click reachability: from a pointer event's `Source`, `FindAncestorOfType<DataGridRowGroupHeader>()` resolves, and the header exposes its group's items (via its `DataContext`/group object — record the exact route; Task 5's select-whole-group needs it) (§Reality-check #4).
  6. Column-header sort on the grouped view sorts within groups without breaking grouping.
  7. Zebra basis: does `DataGridRow.Index` (§Reality-check #8) number rows flat across groups, and how does alternation read on the WPF reference (`mainwindow-uploaded-*-wpf.png`)? Record the basis Task 4/5 should use.
- [x] **Step 4: Headless tests** (`GroupingProbeTests`, 3-4 `[AvaloniaFact]`s — these move to `UploadedViewTests` when Task 5 deletes the probe): view groups count == 3; group keys match; collapse API (`DataGrid.CollapseRowGroup(group, …)` — §Reality-check #1) hides the group's rows; re-expanding restores them. ADD a rebuild-reexpands [AvaloniaFact] (full ItemsSource rebuild → all groups expanded — the LoadAsync-parity keystone, currently pinned only by a screenshot).
- [x] **Step 5:** **GO/NO-GO decision recorded in the task notes** (and relayed to the team lead if NO-GO). A NO-GO stops/re-scopes **only Task 5** (UploadedView is the sole grouping consumer this phase) — Tasks 3, 4, 6, 7, 8 and 9 are grouping-independent and proceed regardless while the fallback is decided. Full suite gate; record counts. **Commit** — `"probe(avalonia): DataGrid grouping go/no-go — DataGridCollectionView recipe + row-group-header theme + checklist verdicts"`

**Task 2 verdict — GO (executed 2026-07-11, this `probe(avalonia): DataGrid grouping go/no-go` commit):** The DataGridCollectionView grouping recipe is pinned; **Task 5 uses grouping, NOT the ItemsControl fallback** (checklist items 1-3 all PASS, close and consistent with `mainwindow-uploaded-*-wpf.png`). Recipe: view built in HEAD code-behind (`GroupingProbeWindow.BuildView`) — `new DataGridCollectionView(items)` + `GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(...PackageName)))`; the group bar is a re-templated `DataGridRowGroupHeader` ControlTheme wired via `DataGrid.RowGroupTheme="{StaticResource GroupHeaderTheme}"`. **The header's `DataContext` is the `DataGridCollectionViewGroup`** (confirmed by ILSpy over 11.3.13's `GenerateRowGroupHeader`), so the group value binds `{Binding Key}` and the count `{Binding ItemCount, StringFormat='[{0}]'}` — NOT `PART_PropertyNameElement` (which renders `"PropertyName:"`, not the value). The chevron is a whole-row flat `ToggleButton` **named `PART_ExpanderButton`** (load-bearing: the DataGrid keeps its `IsChecked` in sync with the group's `IsVisible` and drives collapse/expand off it); `−`/`+` toggled off its `IsChecked`; `SublevelIndent 0`; transparent bg. Verdicts:

| # | Checklist item | Verdict | Evidence |
|---|---|---|---|
| 1 | DataGridCollectionView + DataGridPathGroupDescription group correctly, built in head code-behind | **PASS** | 3 groups, keys/counts (3,2,2) match — `BuildView_GroupsByPackageName_*` test + every shot |
| 2 | Group-header fidelity (flat −/+ chevron, SemiBold name, `[n]`, transparent, rows flush, SublevelIndent 0) | **PASS** | `_probe-grouped-light.png` / `_probe-grouped-dark.png` — theme-live via DynamicResource, both variants |
| 3 | Interactive collapse via chevron; state survival; ItemsSource-rebuild behavior | **PASS** | chevron toggle collapses group 0 (`_probe-collapsed-dark.png`, `−`→`+`); collapse state is grid-level (survives recycling — `CollapseRowGroup_HidesGroupRows_ExpandRestores` 7→4→7); **full ItemsSource rebuild RE-EXPANDS all groups** (`_probe-rebuilt-dark.png`) = WPF LoadAsync parity. (Scroll survival not exercisable with 7 rows; state lives on `RowGroupInfo`, not the recycled container.) |
| 4 | Built-in Ctrl+C on a grouped view (§Reality-check #3) | **PASS** | **Synthetic `KeyDown` (Key.C + Ctrl) raised on the grid DOES reach the internal copy path** — `CtrlC_OnGroupedView_*` test: clipboard carries the quoted column-header row + the 2 selected data rows, tab-separated; **group headers do NOT pollute** the copy (`ProcessCopyKey` iterates `SelectedItems`, i.e. data rows only). So Task 5's context-menu Copy can raise a synthetic Ctrl+C; the code-behind fallback is NOT needed. Format note: Avalonia quotes every cell (`"Name"\t"Size"\t"URL"`). |
| 5 | Group-header right-click reachability + group items route (§Reality-check #4) | **PASS** | eval: `FindAncestorOfType<DataGridRowGroupHeader>(source, true)` resolves the header; its `DataContext` is the `DataGridCollectionViewGroup`; `group.Items` enumerates the group's rows (key="Fake pack (photos)", count=3, first=fake_beach.jpg). **This is the exact select-whole-group route Task 5 copies.** |
| 6 | Column-header sort on the grouped view sorts within groups without breaking grouping | **PASS (with caveat)** | `_probe-sorted-dark.png`: Size↓ sorts rows WITHIN each group, grouping intact (3 groups, correct counts). **CAVEAT: group ORDER also re-sequences to follow the sort** (the group holding the top-sorted item floats up — archive-set's 3 MB rows first). Task 5: if a stable package order is wanted, sort the source by PackageName first (or add a group-level sort) — column sort alone reorders groups. |
| 7 | Zebra basis — row index numbering under grouping (§Reality-check #8) | **PASS** | `GetIndex_NumbersFileRowsFlatAcrossGroups` test: index numbers file rows **flat 0..6 across groups** (no gap for headers) — the basis Task 4's zebra helper alternates on (`index % 2`). **FINDING for Task 4: `DataGridRow.Index` is OBSOLETE on 11.3.13 ("Use the Index property instead") — the plan's zebra snippet still calls `GetIndex()`; switch it to the `.Index` property to stay 0-warning.** |

Baselines at this gate: WPF **1178/1178**, Avalonia **223 → 227** (+4 `GroupingProbeTests`). Both heads 0-warning. **Deviations:** (a) launched via the plan's `GroupingProbeButton` on `--agent --gallery` (the team-lead brief's `--grouping-probe` flag was not added — the plan's Task-2 file set is gallery-button-only and excludes App.axaml.cs); (b) the probe fixture's URL VALUES are neutral `https://example.test/...` placeholders, not the plan Step-1 `rapidgator.net`/`catbox.moe` strings — Windows Defender ML quarantined the source file when the realistic hoster URLs were present (only the null-URL row is load-bearing to the recipe; no test asserts a URL value). Screenshots: `D:\temp2\cbuild-mig\shots\_probe-grouped-{light,dark}.png`, `_probe-collapsed-dark.png`, `_probe-sorted-dark.png`, `_probe-rebuilt-dark.png`.

---

### Task 3: Column persistence — Avalonia twin + shared column menu + the Core IVT line

Prep item 5. The WPF half (`src/Lib/UI/DataGridColumnVisibilityPersistence.cs`) stays untouched (design: it ports at cutover); the Avalonia head gets a **verbatim-format twin** — same namespace, same persisted format, same setting keys, since **both heads read the same DB rows**. Format drift is guarded by duplicating the WPF suite's format test vectors into the Avalonia suite (a drift on either side breaks that side's tests). The plan explicitly REJECTED hoisting the format layer into Core (it would churn 4 WPF-head files for a format that is frozen during the two-head period) — revisit at cutover.

Also here: the **one-line Core IVT grant**. The Avalonia view code-behind must read `UploadedViewModel.SettingRepo` / `.DialogServiceForView` (internal, UploadedViewModel.cs:38-44) and `LogsViewModel.SettingRepo` / `.DialogServiceForView` (LogsViewModel.cs:22-28) — Core's `InternalsVisibleTo` today lists only `CSUploader.Tests` and `CSUploader` (CSUploader.Core.csproj:29-30), so the Avalonia head (`AssemblyName=CSUploader.Avalonia`) cannot compile against them without the grant.

**Files:**
- Create: `src/CSUploader.Avalonia/Lib/UI/DataGridColumnVisibilityPersistence.cs` (namespace `CSUploader.Lib.UI` — same as WPF, different assembly)
- Create: `src/CSUploader.Avalonia/Lib/UI/DataGridColumnMenu.cs`
- Modify: `src/CSUploader.Core/CSUploader.Core.csproj` (add `<InternalsVisibleTo Include="CSUploader.Avalonia" />` next to line 30)
- Test: `tests/CSUploader.Avalonia.Tests/Lib/DataGridColumnPersistenceTests.cs`, `tests/CSUploader.Avalonia.Tests/Lib/DataGridColumnMenuTests.cs`

**Interfaces:**
- Produces: `public static class DataGridColumnVisibilityPersistence` (Avalonia) with the same public surface as the WPF original — `ColumnState(bool Visible, int DisplayIndex)`, `LoadOverridesAsync(repo, key, ct)`, `SaveOverridesAsync(repo, key, overrides, ct)`, `ApplyAsync(DataGrid, repo, key, ct)`, `CaptureCurrentState(DataGrid)`, `ResetAsync(DataGrid, defaults, repo, key, ct)`, `PersistAsync(DataGrid, repo, key, ct)`; and `internal static class DataGridColumnMenu` with `Build(DataGrid grid, Dictionary<string, DataGridColumnVisibilityPersistence.ColumnState> defaults, SettingRepository repo, string settingKey, IDialogService dialogService, string resetMessageKey, string resetTitleKey) : ContextMenu` and `AttachToHeaders(DataGrid grid, ContextMenu menu)` (opens the menu on header right-click).
- Consumes: `Avalonia.Controls.DataGrid`/`DataGridColumn` (`Header`, `IsVisible`, `DisplayIndex`), Core `SettingRepository`/`SettingDto`, `IDialogService.ShowOptOutConfirmationAsync`, `ConfirmationKeys.ResetColumns` (Core, ConfirmationKeys.cs:21).

- [x] **Step 1: The twin.** Copy `LoadOverridesAsync`, `SaveOverridesAsync`, the separator constants, and the `ColumnState` record **verbatim** from the WPF original (src/Lib/UI/DataGridColumnVisibilityPersistence.cs:24-111 — they are framework-free). Re-implement the four grid-facing members on Avalonia APIs, mirroring the WPF semantics line-for-line (:120-256): `column.Visibility == Visibility.Visible` → `column.IsVisible`; `Visibility.Visible/Collapsed` assignment → `column.IsVisible = state.Visible`; DisplayIndex sort-ascending-then-assign and clamping identical; `CaptureCurrentState` uses the `grid.Columns` collection index, NOT `DisplayIndex` (the WPF comment :166-169 explains why — keep the comment's substance). Class doc states the format contract: *"Persisted format identical to the WPF head's `src/Lib/UI/DataGridColumnVisibilityPersistence.cs` — both heads read the same Setting rows; never change one side alone."*
- [x] **Step 2: The shared menu.** `DataGridColumnMenu.Build` reproduces the WPF per-view menu builders (UploadedView.xaml.cs:83-164 and LogsView.xaml.cs:57-129 are near-identical — the Avalonia head shares ONE copy, a recorded improvement): one checkable `MenuItem` per column (`Header = column.Header?.ToString() ?? Localizer.Instance["Uploads_ColumnMenu_DefaultLabel"]`, **`ToggleType = MenuItemToggleType.CheckBox`** — unlike WPF's `IsCheckable`, Avalonia renders NO check glyph from `IsChecked` alone; both `ToggleType` and `StaysOpenOnClick` confirmed present on 11.3.18 — `IsChecked = column.IsVisible`, `StaysOpenOnClick = true`; port rule 31), **first column disabled** (the anchor/group-expander rule), Click toggles `column.IsVisible` + `PersistAsync`; separator; Reset item → `dialogService.ShowOptOutConfirmationAsync(ConfirmationKeys.ResetColumns, Localizer.Instance[resetMessageKey], Localizer.Instance[resetTitleKey])` then `ResetAsync`; `Opened` refreshes checkmarks. `AttachToHeaders` adds a TUNNEL `ContextRequested` handler on the grid: if `(e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>()` is non-null → `menu.Open(header)` + `e.Handled = true` (Avalonia has no per-column HeaderStyle-with-ContextMenu; this replaces the WPF cloned-header-style trick and ALSO guarantees the row ContextMenu never opens on headers — the WPF `FilesGrid_ContextMenuOpening` header pass-through, UploadedView.xaml.cs:231-234) (§Reality-check #5 for the Open/handled interplay).
- [x] **Step 3: Core IVT.** Add `<InternalsVisibleTo Include="CSUploader.Avalonia" />` to CSUploader.Core.csproj. (Nothing consumes it until Task 5 — landing it here keeps Task 5's diff view-only.)
- [x] **Step 4: Tests.**
  - Format vectors: copy the WPF suite's Load/Save round-trip cases (find them: `grep -rn "DataGridColumnVisibilityPersistence" tests/` — mirror its SettingRepository harness) into the Avalonia suite against the twin: `"Header=1|3,Other=0|5"` parses to the right map; legacy visibility-only rows (`"Header=0"`) default DisplayIndex −1; save round-trips; empty map clears.
  - Headless grid tests: a 4-column DataGrid — `CaptureCurrentState` maps headers→(visible, collection index); `ApplyAsync` hides an overridden column + reorders per DisplayIndex; `ResetAsync` restores and clears the row; `PersistAsync` writes current state.
  - Menu tests: `Build` yields N+2 items, first disabled, checkmarks track `IsVisible`, toggle persists (assert via `LoadOverridesAsync`).
- [x] **Step 5:** Full suite gate; record counts. **Commit** — `"feat(avalonia): column visibility/order persistence twin + shared column-toggle menu; Core InternalsVisibleTo for the Avalonia head"`

**Task 3 outcome (executed 2026-07-11, commit `7e7e814`):** DONE. Baselines at this gate: WPF **1178/1178**, Avalonia **227 → 246** (+19: 10 format vectors, 4 grid round-trips, 5 menu), both heads 0-warning. Twin API is the exact WPF public surface (`ColumnState`, `LoadOverridesAsync`/`SaveOverridesAsync`/`ApplyAsync`/`CaptureCurrentState`/`ResetAsync`/`PersistAsync`) in the same `CSUploader.Lib.UI` namespace. `internal static DataGridColumnMenu` exposes `Build(grid, defaults, repo, settingKey, dialogService, resetMessageKey, resetTitleKey)` + `AttachToHeaders(grid, menu)`. **Findings for Tasks 5/6:** (a) `ContextMenu.Open(header)` only works because the menu is NOT assigned as any control's `Control.ContextMenu` — that keeps `_attachedControls` null, so `Open` skips its "same control" guard (assigning the menu to a control would make `Open(header)` throw `ArgumentException`); (b) `AttachToHeaders`' runtime Open/`e.Handled` interplay (Reality-check #5) is NOT unit-tested — opening the popup needs a realized header + TopLevel + a real right-click, so it gets its first verification in the Task 5/6 bridge session, same as the Phase 3 behaviors; (c) simulating a menu toggle in a test must replay `DefaultMenuInteractionHandler.Click` — flip `IsChecked` THEN raise `MenuItem.ClickEvent` (a bare `RaiseEvent` does NOT toggle; confirmed by decompiling 11.3.18).

---

### Task 4: Shared zebra-striping helper

Prep item 8. Avalonia's DataGrid has no `AlternatingRowBackground`; the design pins the recycling-safe replacement: LoadingRow/UnloadingRow toggling an index-based row class.

**Files:**
- Create: `src/CSUploader.Avalonia/Behaviors/DataGridZebraStriping.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Behaviors/DataGridZebraStripingTests.cs`

**Interfaces:**
- Produces: attached property `DataGridZebraStriping.IsEnabled` (owner-typed like the Phase 3 behaviors, DataGridSelectionBehaviors.cs:41-47); rows get/lose the style class **`alt`**; consumers add a view style `DataGridRow.alt { Background = <their alt brush> }` (`LogAltRowBrush` for the log grids, `DataGridAltRowBrush` for UploadedView — both already in ThemeBrushes.axaml, light+dark).
- Consumes: `DataGrid.LoadingRow`/`UnloadingRow`, `DataGridRow.Index` (§Reality-check #8; basis per the Task 2 checklist-7 verdict — if `GetIndex()` counts differently on a grouped view, implement the recorded basis).

- [x] **Step 1: Implement.**

```csharp
public sealed class DataGridZebraStriping
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGridZebraStriping, DataGrid, bool>("IsEnabled");

    static DataGridZebraStriping()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.LoadingRow += OnLoadingRow;
                grid.UnloadingRow += OnUnloadingRow;
            }
            else
            {
                grid.LoadingRow -= OnLoadingRow;
                grid.UnloadingRow -= OnUnloadingRow;
            }
        });
    }

    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    // LoadingRow fires on every (re)bind of a recycled container, so setting the class from the
    // CURRENT index is inherently recycling-safe; UnloadingRow clears it as belt-and-braces.
    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e)
        => e.Row.Classes.Set("alt", e.Row.Index /* GetIndex() is [Obsolete] on 11.3.13 — probe finding; Index is the replacement */ % 2 == 1);

    private static void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
        => e.Row.Classes.Set("alt", false);
}
```

  (Adjust the index expression if the Task 2 checklist-7 verdict recorded a different basis for grouped views — one helper, one basis, both grid families.)
- [x] **Step 2: Headless tests.** Grid with 6 rows + the helper + an `alt` style: realized odd rows carry the class and the alt background; even rows don't; adding a row keeps parity correct; disabling removes handlers (new rows unclassed).
- [x] **Step 3:** Full suite gate; record counts. **Commit** — `"feat(avalonia): shared DataGrid zebra-striping helper (LoadingRow index classes; AlternatingRowBackground has no Avalonia equivalent)"`

**Task 4 outcome (executed 2026-07-11, commit `60d5e67`; reviewed + checkboxes marked by the review commit, Phase 4 Task 6 precedent):** DONE — **APPROVED**. Baselines at this gate: WPF **1178/1178**, Avalonia **246 → 251** (+5 zebra tests), both heads 0-warning (`--no-incremental` rebuild confirmed). Shipped `Behaviors/DataGridZebraStriping.cs` (attached `IsEnabled`, sealed non-static like the Phase 3 behaviors) — `LoadingRow` sets `Classes.Set("alt", Row.Index % 2 == 1)`, `UnloadingRow` clears. **Parity verified against the WPF reference:** WPF `AlternationCount=2` tints AlternationIndex 1 (odd rows) via `AlternatingRowBackground`, row 0 default — so `Index % 2 == 1` (odd = alt) matches, and row 0's background agrees across heads (LogsView.xaml:57-58, UploadedView.xaml:102; `LogAltRowBrush`/`DataGridAltRowBrush` hex identical in both heads' theme dictionaries). Uses `.Index` (not `[Obsolete]` `GetIndex()`). Leak surface: handlers ride the grid's OWN `LoadingRow`/`UnloadingRow` as static methods, so enable/disable is pure subscribe/unsubscribe with nothing external to pin the grid (simpler than `AutoScrollBehavior`'s collection case — no detach hook needed). Five `[AvaloniaFact]`s, all non-vacuous (odd rows assert `Assert.Same(AltBrush, row.Background)` — the exact style-resolved instance; `Unloading`/`GroupedGrid` assert realization explicitly); the grouped test uses the Task 2 probe fixture shape (3/2/2, flat index 0..6). Also folds the Task 3 review advisory into `DataGridColumnMenu.AttachToHeaders` XML doc (do-not-double-attach warning). Scope = 3 files as briefed. **Minor non-blocking nits (not fixed):** (a) `Enabled_OddRowsGetAltClassAndBackground_EvenRowsDoNot` has no explicit non-empty guard so it would pass vacuously if no row realized — mitigated because sibling tests prove headless realization; (b) the new `AttachToHeaders` remark opens lowercase ("do NOT…").

---

### Task 5: UploadedView + the Uploaded tab goes live

The grouping probe's recipe becomes the real view. Prep items 7 (ElementStyle rule exercised + recorded) and 12 (right-click select is code-behind); standing notes (a)-(c) — `ClearSelectionOnEmptyClick` gets its first-consumer interaction verification here.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/UploadedView.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml` (Uploaded `TabItem` placeholder :14-16 → the view), `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (remove `GroupingProbeButton`)
- Delete: `src/CSUploader.Avalonia/DevTools/GroupingProbeWindow.axaml(+.cs)` (recipe superseded; its tests retarget)
- Test: `tests/CSUploader.Avalonia.Tests/Views/UploadedViewTests.cs` (absorbs the retargeted grouping tests)

**Interfaces:**
- Produces: `UploadedView : UserControl` binding `UploadedViewModel` (Core — untouched); the recorded ElementStyle port-rule verdict (rule 20); the `mainwindow-uploaded` ava cells.
- Consumes: Task 2's grouping recipe, Task 3's persistence twin + menu (`SettingKey.UploadedTabHiddenColumns`, SettingKey.cs:46), Task 4's zebra helper, Phase 3's `DataGridSelectionBehaviors.ClearSelectionOnEmptyClick` + converters (`ByteUnitConverter`, `DateTimeFormatConverter`, `HosterIconConverter`, `FileTypeIconConverter` — all exist in `src/CSUploader.Avalonia/Converters/`), `MainViewModel.UploadedViewModel` (MainViewModel.cs:155; `InitializeAsync` already calls `LoadAsync`, :256).

**Port deltas beyond the rules table** (source: `src/Views/UploadedView.xaml(+.cs)`):
- **Grouped ItemsSource** (rule 26): code-behind `DataContextChanged` → when `DataContext is UploadedViewModel vm`, set `FilesGrid.ItemsSource = BuildGroupedView(vm.Files)` (the Task 2 recipe; group by `nameof(UploadedFileRow.PackageName)`). The XAML keeps `SelectedItem="{Binding SelectedRow, Mode=OneWayToSource}"`.
- **Toolbar**: Border (`SurfaceMutedBrush`, bottom border) + Export button bound to `ExportJsonCommand` with `ToolTip.Tip="{loc:Loc Uploaded_Toolbar_ExportJsonTip}"`.
- **Grid surface**: `SelectionMode="Extended"` (rule 30), `IsReadOnly="True"`, `CanUserResizeColumns/ReorderColumns/SortColumns="True" (SORT CAVEAT, Task 2 verdict #6: a column sort re-sequences GROUP ORDER too — sort the source by PackageName first or add a group-level sort if stable package order is wanted)`, `ClipboardCopyMode="IncludeHeader"`, `HeadersVisibility="Column"`, `GridLinesVisibility="All"` + the two grid-line brushes, themed `Background`+ zebra (`DataGridZebraStriping.IsEnabled="True"` + `DataGridRow.alt { Background=DataGridAltRowBrush }` style) — the non-null Background is ALSO the `ClearSelectionOnEmptyClick` hit-test prerequisite (standing note a), and `beh:DataGridSelectionBehaviors.ClearSelectionOnEmptyClick="True"`.
- **Columns** (mirror UploadedView.xaml:272-409; widths/minwidths verbatim): Name = template column (SVG file-type icon 16×16 via `FileTypeIconConverter`, `Margin="28,0,6,0"` group indent, name TextBlock trimmed; `SortMemberPath="FileName"`, `ClipboardContentBinding="{Binding FileName}"` — §Reality-check #10 for SortMemberPath/ClipboardContentBinding on template columns); Hoster = template column (icon via `HosterIconConverter` + secondary text); URL = template column (`TextBlock Classes="url-link"`, `Text/ToolTip.Tip = FileUrl`, `IsVisible` per rule 25, `PointerReleased` opens the browser — mirror `UrlText_MouseLeftButtonUp` :166-182 incl. the swallow); Path/Size/Account/Finished/Started/Hash = `DataGridTextColumn` with `CellStyleClasses` + view descendant styles per rule 20 (Path: secondary+trim+tooltip; Size: right-aligned Consolas + converter binding; Account: secondary+trim; Finished/Started: secondary Consolas + `DateTimeFormatConverter`; Hash: secondary Consolas+trim+tooltip). **Record the rule-20 verdict in the task notes** (which cells needed template columns vs classes; whether the ToolTip binding setter held — §Reality-check #7).
- **Group header**: the Task 2 ControlTheme, verbatim.
- **Context menu** (rules 18/19): XAML `DataGrid.ContextMenu` mirroring :147-198 — Copy submenu (`Common_Context_Copy`) with the row-copy entry at top (`Uploaded_Context_Copy` + gesture text; Click → code-behind raising the grid's built-in copy per the Task 2 checklist-4 verdict — synthetic Ctrl+C `KeyDown` or the recorded alternative, §Reality-check #3) followed by the 9 per-column items (`CopyColumnCommand`, CommandParameter `"Name"`/`"Path"`/`"Size"`/`"Hoster"`/`"Account"`/`"Finished"`/`"Started"`/`"Hash"`/`"URL"` — the resx-suffix keys `ColumnValueExtractor` maps); `Common_Context_OpenUrl` → `OpenUrlCommand`; `Uploaded_Context_Remove` (+ gesture) → `RemoveSelectedCommand`; `Uploaded_Context_ExportJson` → `ExportJsonCommand`. `CommandParameter` for OpenUrl/Remove wired in code-behind to `FilesGrid.SelectedItems` (rule 19). `ContextMenu.Opening` handler mirrors `FilesGrid_ContextMenuOpening` :223-257: snapshot `vm.SelectedRows = [.. FilesGrid.SelectedItems.OfType<UploadedFileRow>()]`, suppress (cancel) when neither a `DataGridRow` nor a `DataGridRowGroupHeader` ancestor is under the press (headers never reach here — Task 3's `AttachToHeaders` handles them first).
- **Right-click select, row + group** (prep item 12 — view code-behind, NOT the behavior): TUNNEL `PointerPressed` right-button handler mirroring `FilesGrid_PreviewMouseRightButtonDown` :189-216 — row hit: keep an existing multi-selection if the row is in it, else exclusive-select; group-header hit: clear + select every row of that group (item enumeration per the Task 2 checklist-5 route).
- **Delete key** (rule 24): `KeyBinding Gesture="Delete"` → `RemoveSelectedCommand`, parameter = SelectedItems (rule 19's code-behind assignment).
- **Column persistence**: on first `Loaded`-equivalent (guard once — `AttachedToVisualTree` refires on tab switches; mirror the WPF once-only guard, LogsView.xaml.cs:31): `CaptureCurrentState` → `ApplyAsync(grid, vm.SettingRepo, SettingKey.UploadedTabHiddenColumns)` → `DataGridColumnMenu.Build(…, "Uploaded_ResetColumns_Message", "Uploaded_ResetColumns_Title")` + `AttachToHeaders`; persist on `ColumnDisplayIndexChanged` (CONFIRMED present on 11.3.13 — §Reality-check #11). First column (Name) stays toggle-disabled ("the group expander lives there", UploadedView.xaml.cs:98-101).
- **Tab wiring**: `<TabItem Header="{loc:Loc Main_Tab_Uploaded}"><views:UploadedView DataContext="{Binding UploadedViewModel}" /></TabItem>` (the WPF MainWindow.xaml:52-53 shape; add `xmlns:views`).

- [x] **Step 1: Port the view** per the deltas above (XAML + code-behind); wire the MainWindow tab; delete the probe window + gallery button.
- [x] **Step 2: Headless tests** (retarget the Task 2 grouping tests here, plus): grouped view over a VM-shaped `ObservableCollection` groups by PackageName (counts match a 3-group fixture); URL cell invisible for empty/null `FileUrl`, visible otherwise; right-click on an unselected row exclusive-selects it, right-click inside a 2-row selection preserves it (input simulation via the Phase 3 TestSupport helpers); group-header right-click selects the whole group; `Opening`-suppression decision (extract the hit-test into an `internal static` helper if raising a real ContextRequested proves flaky — record which); Delete gesture fires `RemoveSelectedCommand` canExecute path; column-menu attached with first item disabled. Where the VM is needed, construct `UploadedViewModel` the way the WPF suite's `UploadedViewModelTests` does (scratch SQLite repos — reuse that harness pattern).
- [x] **Step 3: Bridge session.** Build, reseed if a prior drive removed rows, launch `--agent`; select the Uploaded tab (`ava_action` verb select on the TabItem, or set `SelectedTabIndex` via `ava_vm`); light+dark `mainwindow-uploaded-*-ava.png` (maxWidth 2500). Interactions, each with evidence: click a group chevron → collapsed (`_uploaded-collapsed.png`); click a row then click empty space below the rows → selection clears (read `SelectedRow` via `ava_vm` — the behavior's first-consumer verification, standing note c); click a URL cell → **do not** — it opens a browser on fake URLs; skip, headless covers the guard. Do NOT click Export (native picker). Rebuild contact sheet; Read the `mainwindow-uploaded` pair vs WPF — group bars, zebra, URL styling, column set; fix or record arbitration notes (row CONTENT should match — same seed on both sides).
- [x] **Step 4:** Full suite gate; record counts. **Commit** — `"feat(avalonia): UploadedView — grouped DataGrid, URL cells, right-click targeting, context menu, column persistence, zebra; Uploaded tab live"`

**Task 5 outcome (executed 2026-07-11):** DONE. Baselines at this gate: WPF **1178/1178**, Avalonia **251 → 260** (−4 retargeted `GroupingProbeTests` deleted with the probe, +13 `UploadedViewTests`); both heads 0-warning (`--no-incremental`). The Task 2 grouping recipe was copied VERBATIM (head code-behind `BuildGroupedView` = `new DataGridCollectionView` + `DataGridPathGroupDescription(nameof(UploadedFileRow.PackageName))`; group bar = the `DataGridRowGroupHeader` ControlTheme wired via `DataGrid.RowGroupTheme`; `PART_ExpanderButton` chevron; `{Binding Key}`/`{Binding ItemCount}`). **Rule-20 verdict: CellStyleClasses + `DataGridCell.<class> TextBlock` descendant styles WORKED for all six text columns** — Path/Account/Hash (secondary + trim), Size (right + Consolas), Finished/Started (Consolas + converter); the `ToolTip.Tip` Binding **setter held** (resolves against the row item — §Reality-check #7 confirmed OK), so **no** template-column fallback was needed for the tooltip cells. Only the icon-composed cells (Name, Hoster) and the URL cell stay template columns.

**KEY BUG FOUND + FIXED (Avalonia port gotcha, live-caught in the bridge session): `DataGridTextColumn.Binding` defaults to TwoWay and pushes the ConvertBack result to the SOURCE on bind — even in a read-only grid.** `ByteUnitConverter`/`DateTimeFormatConverter` throw on ConvertBack, so the grid was blanking `FileSize`→0 (and dates→`MinValue`) the moment the tab was shown (the VM held the real value pre-bind; the grid corrupted it). Fix = `Mode=OneWay` on all six text-column `Binding`s. WPF's DataGrid does NOT write back on load — this is Avalonia-specific and should feed rule 20 / the design reconcile. A non-vacuous regression test (`ReadOnlyConverterColumns_DoNotWriteBack_...`) guards it — verified it FAILS (sizes → all 0) without the fix.

**Recorded deviations / decisions:** (a) **Delete key wired in code-behind** (not XAML rule-24 form): `KeyBinding` is a non-DataContext `AvaloniaObject` on 11.3.18, so a `{Binding RemoveSelectedCommand}` on it wouldn't resolve — the code-behind adds it once with the VM command + the live `SelectedItems` parameter. (b) **Gesture text via `InputGesture`** (`KeyGesture`, decompile-confirmed DISPLAY-ONLY — no hotkey registration, so no recursion with the synthetic Ctrl+C) hardcoded `Ctrl+C`/`Delete` — not loc-bound (a `KeyGesture` can't carry the resx "Del" string; "Delete" vs WPF "Del" is cosmetic). (c) **Right-click suppression via a `_rightClickOnItem` flag** set in the tunnel `PointerPressed` handler — `ContextMenu.Opening` (confirmed `CancelEventHandler`, cancelable) carries no pointer source, so the row/group/empty decision is recorded at press time; `ApplyRightClickSelection`/`SnapshotSelectionAndDecideSuppression` are `internal` so the headless tests drive them directly (the sanctioned fallback — raising a real ContextRequested at a specific row is not drivable). (d) group-select route = `FindAncestorOfType<DataGridRowGroupHeader>(includeSelf:true)` → `DataContext` is `DataGridCollectionViewGroup` → `.Items` (Task 2 checklist-5, verbatim). (e) `Mode=OneWay` also on the two non-converter text columns (Path/Account/Hash) — correct for a read-only grid. (f) **LoadAsync-parity rebuild:** the view rebuilds the `DataGridCollectionView` on the source's `Reset` (Clear) so groups re-expand — the keystone `Rebuild_ReExpandsAllGroups` test covers it. (g) reseeded the ava scratch DB (its FileData had 0-byte FileSize) so the pair's Size column matches the wpf side. **Bridge evidence:** `mainwindow-uploaded-{light,dark}-ava.png` (structurally match the WPF refs — groups, chevrons `[n]`, file-type + hoster icons, zebra, Size in MiB); `_uploaded-collapsed.png` (chevron `−`→`+`, 14→12 rows); ClearSelectionOnEmptyClick verified (row-select count 1 → synthetic empty left-click → 0, `SelectedRow` null). **Arbitration:** the only WPF-vs-Avalonia divergence is the WPF menu bar (File/View/Help), ported in a later phase; row CONTENT matches. Commit `feat(avalonia): UploadedView …`.

---

### Task 6: LogsView + the Logs tab goes live

Prep item 11's port-deltas exercised; `AutoScrollBehavior` first-consumer verification (standing note c).

**Files:**
- Create: `src/CSUploader.Avalonia/Views/LogsView.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml` (Logs `TabItem` placeholder :20-22 → the view)
- Test: `tests/CSUploader.Avalonia.Tests/Views/LogsViewTests.cs`

**Interfaces:**
- Produces: `LogsView : UserControl` binding `LogsViewModel` (Core — untouched); the `mainwindow-logs` ava cells.
- Consumes: Tasks 3+4 helpers (`SettingKey.LogsStatusTabHiddenColumns`/`LogsHttpTabHiddenColumns`/`LogsErrorsTabHiddenColumns`/`LogsUITabHiddenColumns`, SettingKey.cs:48-54), `AutoScrollBehavior` (Phase 3), `DateTimeFormatConverter`/`UrlDecodeConverter`, Phase 4's `LogDetailsWindow` + `HttpDetailsWindow` (the latter already carries the `LogEntryViewModel` ctor — Phase 4 Task 7 ported both public ctors), `MainViewModel.LogsViewModel` (:161; log events flow in via `Logger_OnLogOutput` → `AddLogEntry`, :301).

**Port deltas beyond the rules table** (source: `src/Views/LogsView.xaml(+.cs)`):
- **Layout**: root Grid `Margin=4` on `LogBackgroundBrush`; `CheckBox IsChecked="{Binding AutoScroll}"` (`Logs_AutoScroll`); `TabControl` with 4 tabs (`Logs_Tab_Status/Http/Errors/UI`), each = Clear button (`Logs_BtnClear`, `Classes="jd2"`, 80×24, right-aligned, per-tab Clear command) over a DataGrid.
- **Shared grid styling**: the WPF `LogDataGridStyle` (:45-75) becomes a `DataGrid.log-grid` class in `UserControl.Styles`: `IsReadOnly`, `SelectionMode="Single"`, `GridLinesVisibility="Horizontal"` + brush, `RowHeight="22"` (maps WPF's `LogRowStyle MaxHeight=22` — rows are single-line; note the mapping), `Background=RowBackground=LogBackgroundBrush` equivalent (grid Background + zebra), zebra helper + `DataGridRow.alt { Background=LogAltRowBrush }`, header styling via a `DataGridColumnHeader` style in the view (12px SemiBold, `DataGridHeaderBrush`, padding 6,4).
- **Columns per tab** (mirror :110-118 / :143-157 / :182-190 / :215-223; widths verbatim): Status/Errors/UI = DateTime (converter), Filename, Function, Line, Message (600w, `CellStyleClasses="msg-cell"` → trim + `ToolTip.Tip="{Binding Message}"` per rule 20), Thread. HTTP adds Status (`StatusCode`), Method (`HttpTransaction.Method`), Url (320w, `UrlDecodeConverter` + decoded tooltip, `CellStyleClasses="url-cell"`), Proxy.
- **Auto-scroll**: `behaviors:AutoScrollBehavior.IsEnabled="{Binding AutoScroll}"` on all four grids.
- **Details-open**: `DoubleTapped` (rule 22) + tunnel `KeyDown` Enter (rule 23) → `OpenDetails(entry)` mirroring :155-164: `entry.HasHttpTransaction ? new HttpDetailsWindow(entry) : new LogDetailsWindow(entry)`, owner `TopLevel.GetTopLevel(this) as Window`, `ShowDialog(owner)`.
- **Column persistence ×4**: `Tag="{x:Static upload:SettingKey.LogsStatusTabHiddenColumns}"` etc. per grid; one shared `WireGrid(DataGrid)` code-behind method with the once-only guard (mirror `LogGrid_Loaded` :28-55 — `AttachedToVisualTree` refires on every tab switch), using Task 3's helper with `"Logs_ResetColumns_Message"`/`"Logs_ResetColumns_Title"`; DateTime column (first) toggle-disabled (the reopen-anchor rule, :71-76).

- [ ] **Step 1: Port the view** (XAML + code-behind); wire the Logs tab in MainWindow.
- [ ] **Step 2: Headless tests**: `AddLogEntry` routes to the right grid per `LogType` (bind a real `LogsViewModel` — its ctor only needs `IDialogService` + optional repo, LogsViewModel.cs:30-34); Enter on a selected Status row opens `LogDetailsWindow`, on an HTTP row (synth transaction) opens `HttpDetailsWindow` (snapshot windows before closing, `finally`-close); double-tap on a row opens details, double-tap on the header/empty area does NOT; auto-scroll: with `AutoScroll=true`, adding N rows scrolls the last row into the realized set — if headless realization makes this flaky, assert the behavior's subscription state via its attached-handler property and record the fallback; message cell trims + carries the tooltip.
- [ ] **Step 3: Bridge session.** Launch `--agent` (startup Status entries populate the first tab); select the Logs tab; emit a burst via `ava_eval` (`Logger.Current.Log(...)` — the same static surface EditProxyWindow.xaml.cs:83 uses; 15+ entries so the grid overflows) → auto-scroll keeps the newest row visible (`_logs-autoscroll.png`); untick Auto-Scroll, emit more → viewport stays (`_logs-noscroll.png`); light+dark `mainwindow-logs-*-ava.png`. Note for the sheet: row CONTENT differs from the WPF cell (different startup lines/timestamps) — arbitrate structure (chrome, zebra, columns, tab strip), not text.
- [ ] **Step 4:** Full suite gate; record counts. **Commit** — `"feat(avalonia): LogsView — 4 log tabs, details-open, auto-scroll, per-grid column persistence, zebra; Logs tab live"`

---

### Task 7: Editor foundations — shared editor styles + the HosterCredentialModes hoist

Prep items 3 and 4. Pure infrastructure: no new windows yet. This is the task that touches the WPF head's `EditAccountWindow.xaml.cs` (hoist consumption — behavior identical, WPF suite is the net).

**Files:**
- Modify: `src/CSUploader.Avalonia/Resources/BaseStyles.axaml` (append the 5 editor classes)
- Create: `src/CSUploader.Core/Upload/HosterCredentialModes.cs`
- Modify: `src/Views/EditAccountWindow.xaml.cs` (WPF: delete the two local sets :37-48, re-point the three predicates :142-158 at Core)
- Test: `tests/Upload/HosterCredentialModesTests.cs` (WPF suite — Core types are covered from `CSUploader.Tests`)

**Interfaces:**
- Produces: BaseStyles classes `Button.dialog`, `Button.save`, `TextBlock.field-label`, `TextBox.field-input`, `ComboBox.field-combo` (Tasks 8/9 consume); Core `public enum HosterCredentialMode { UsernamePassword, ApiKey, SessionCookie }` + `public static class HosterCredentialModes` with `GetMode(string? hosterName)`, `IsApiKeyHoster(string?)`, `IsSessionCookieHoster(string?)`, `IsWebViewSignInHoster(string?)` (public, NOT internal — both heads and both test suites consume it without further IVT grants).
- Consumes: the editor brushes (all 34 already in ThemeBrushes.axaml, both variants — verified at plan time: `DialogButton*`, `SaveButton*`, `InputFieldBorderBrush`, `SuccessBrush`, `ErrorBrush`).

- [ ] **Step 1: Core hoist.** Move the two sets VERBATIM including the disabled-hoster comment block (EditAccountWindow.xaml.cs:24-48 — those comments are load-bearing operational history; they move WITH the data):

```csharp
namespace CSUploader.Upload;

/// <summary>How an account's credentials are entered/held for a hoster — drives the
/// EditAccountWindow credential UI on both heads. Hoisted from the WPF EditAccountWindow
/// code-behind (Phase 5 prep item 4) so a new hoster wired on master cannot silently miss
/// the Avalonia editor's copy.</summary>
public enum HosterCredentialMode
{
    /// <summary>Classic username + password entry.</summary>
    UsernamePassword,

    /// <summary>WebView sign-in that derives an API key, or a manually pasted key.</summary>
    ApiKey,

    /// <summary>WebView sign-in whose ONLY credential is the captured session cookie.</summary>
    SessionCookie,
}

public static class HosterCredentialModes
{
    // <the full "FlashBit intentionally absent …" comment block, moved verbatim>
    private static readonly HashSet<string> ApiKeyHosters =
        [with(StringComparer.OrdinalIgnoreCase), "Ex-Load", "KatFile", "Hexload", "Hxfile", "FileBoom", "HitFile", "Keep2Share", "TezFiles", "NitroFlare", "Ufile"];

    private static readonly HashSet<string> SessionCookieHosters =
        [with(StringComparer.OrdinalIgnoreCase), "Isracloud"];

    public static HosterCredentialMode GetMode(string? hosterName) =>
        hosterName is null ? HosterCredentialMode.UsernamePassword
        : ApiKeyHosters.Contains(hosterName) ? HosterCredentialMode.ApiKey
        : SessionCookieHosters.Contains(hosterName) ? HosterCredentialMode.SessionCookie
        : HosterCredentialMode.UsernamePassword;

    public static bool IsApiKeyHoster(string? hosterName) => GetMode(hosterName) == HosterCredentialMode.ApiKey;

    public static bool IsSessionCookieHoster(string? hosterName) => GetMode(hosterName) == HosterCredentialMode.SessionCookie;

    public static bool IsWebViewSignInHoster(string? hosterName) => GetMode(hosterName) != HosterCredentialMode.UsernamePassword;
}
```

  (Carry the XML docs from the WPF fields :18-23 and :40-46 onto the sets/enum members.)
- [ ] **Step 2: WPF re-point.** In `EditAccountWindow.xaml.cs`: delete the two `HashSet` fields; `IsApiKeyHoster()`/`IsSessionCookieHoster()` bodies become `HosterCredentialModes.IsApiKeyHoster(CurrentHoster())` / `…IsSessionCookieHoster(…)` (keep the private wrappers — every internal call site stays untouched). Build WPF; behavior identical.
- [ ] **Step 3: BaseStyles.** Append after the `Button.jd2` block (BaseStyles.axaml:61-79 is the pseudo-class pattern to copy), sourced from the WPF editor styles (EditProxyWindow.xaml:16-102, EditAccountWindow.xaml:16-105). Normalizations, recorded: field inputs pin **Height 28** + `Padding 8,0` (the EditAccountWindow variant whose comment :26-29 explains the TextBox/ComboBox alignment; EditProxy's `8,5`-padding/29px-combo variants are dropped — sub-pixel divergence, contact sheet arbitrates):

```xml
<!-- Editor dialog classes (Phase 5 prep item 3): the two WPF editors carried ~85 duplicated
     window-local lines each; shared here ONCE — a recorded deviation from the per-window rule. -->
<Style Selector="TextBlock.field-label">
  <Setter Property="FontSize" Value="12" />
  <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
  <Setter Property="VerticalAlignment" Value="Center" />
  <Setter Property="Margin" Value="0,0,12,0" />
</Style>
<Style Selector="TextBox.field-input">
  <Setter Property="FontSize" Value="12" />
  <Setter Property="Height" Value="28" />
  <Setter Property="Padding" Value="8,0" />
  <Setter Property="VerticalContentAlignment" Value="Center" />
  <Setter Property="BorderBrush" Value="{DynamicResource InputFieldBorderBrush}" />
  <Setter Property="BorderThickness" Value="1" />
</Style>
<Style Selector="ComboBox.field-combo">
  <Setter Property="FontSize" Value="12" />
  <Setter Property="Height" Value="28" />
  <Setter Property="Padding" Value="6,0" />
  <Setter Property="VerticalContentAlignment" Value="Center" />
  <Setter Property="BorderBrush" Value="{DynamicResource InputFieldBorderBrush}" />
  <Setter Property="BorderThickness" Value="1" />
</Style>
<Style Selector="Button.dialog">
  <Setter Property="Width" Value="90" />
  <Setter Property="Height" Value="30" />
  <Setter Property="FontSize" Value="12" />
  <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
  <Setter Property="Background" Value="{DynamicResource DialogButtonBgBrush}" />
  <Setter Property="BorderBrush" Value="{DynamicResource DialogButtonBorderBrush}" />
  <Setter Property="BorderThickness" Value="1" />
  <Setter Property="CornerRadius" Value="3" />
  <Setter Property="Padding" Value="12,0" />
</Style>
<Style Selector="Button.dialog:pointerover /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource DialogButtonHoverBgBrush}" />
  <Setter Property="BorderBrush" Value="{DynamicResource DialogButtonHoverBorderBrush}" />
</Style>
<Style Selector="Button.dialog:pressed /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource DialogButtonPressedBgBrush}" />
</Style>
<Style Selector="Button.save">
  <Setter Property="Width" Value="90" />
  <Setter Property="Height" Value="30" />
  <Setter Property="FontSize" Value="12" />
  <Setter Property="FontWeight" Value="SemiBold" />
  <Setter Property="Foreground" Value="White" />
  <Setter Property="Background" Value="{DynamicResource SaveButtonBgBrush}" />
  <Setter Property="BorderBrush" Value="{DynamicResource SaveButtonBorderBrush}" />
  <Setter Property="BorderThickness" Value="1" />
  <Setter Property="CornerRadius" Value="3" />
  <Setter Property="Padding" Value="12,0" />
</Style>
<Style Selector="Button.save:pointerover /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource SaveButtonHoverBgBrush}" />
  <Setter Property="Foreground" Value="White" />
</Style>
<Style Selector="Button.save:pressed /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource SaveButtonPressedBgBrush}" />
</Style>
```

- [ ] **Step 4: Tests** (WPF suite): `GetMode` per known hoster ("KatFile"→ApiKey, "Isracloud"→SessionCookie, "Rapidgator"→UsernamePassword, unknown/null→UsernamePassword); case-insensitivity ("katfile"); `IsWebViewSignInHoster` composition.
- [ ] **Step 5:** Full suite gate (BOTH suites — the WPF suite is the hoist's net); record counts. **Commit** — `"refactor(core): hoist HosterCredentialModes from EditAccountWindow (drift trap); shared editor style classes in BaseStyles"`

---

### Task 8: EditProxyWindow + ShowEditProxyDialogAsync

First editor. NotImplemented count drops 3 → 2.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/EditProxyWindow.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` (`ShowEditProxyDialogAsync` :208-209 → real), `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (+2 buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Views/EditProxyWindowTests.cs`

**Interfaces:**
- Produces: `EditProxyWindow` with ctor `(ProxySettingDto proxy, bool acceptInvalidCertificates = false)`, result via `ShowDialog<ProxySettingDto?>` (Save-valid → `Close(dto)`; Cancel/Esc/X → `Close(null)` — collapses the WPF `Result`+`DialogResult` pair, rule 6); the real service member.
- Consumes: Task 7 classes; `ProxyManager.TestProxyAsync(dto, Logger.Current, acceptInvalidCertificates:)` + `ProxyTestResult` (Core, framework-free — the WPF code-behind :64-119 ports near-verbatim); `HttpDetailsWindow` (Phase 4); `MessageBoxWindow` (rule 17); `GetOwnerOrRevealAsync` (Phase 4 Task 3).

**Port deltas beyond the rules table** (source: `src/Views/EditProxyWindow.xaml(+.cs)`):
- Window: `Width=440`, `SizeToContent=Height`, `CanResize=False`, CenterOwner, icon rule 3, `SurfaceMutedBrush` background, inner card Border (`SurfaceBrush`, radius 4). All window-local styles REPLACED by the Task 7 classes (`field-label`/`field-input`/`field-combo`/`dialog`/`save`).
- Fields seeded in ctor from the DTO (:35-42); `TypeCombo.ItemsSource` = the same 5-entry `ProxyType[]` (:50-51). **`PasswordBox` gets `PasswordChar="●"`** (prep item 9 — recorded deviation: WPF shows cleartext; the sheet pair will differ on exactly that box).
- `Opened` → `HostBox.Focus()` (rule 16).
- `TryBuildDtoFromFields` mirrors :150-184 (trim/validate host, invariant port parse 1-65535, null-for-empty user/pass, carry `Id`+`Priority` from the original); validation failure → `await MessageBoxWindow.ShowErrorAsync(this, Localizer.Instance["EditProxy_Validation_HostRequired"/"EditProxy_Validation_PortInvalid"], Localizer.Instance["Common_Error"])` then focus/SelectAll the bad field (the WPF `MessageBoxImage.Warning` icon nuance is already an accepted Phase 4 deviation).
- Test flow mirrors :64-119: disable Test+Save, hide stale Details, status `EditProxy_Status_Testing`; on result → `EditProxy_Status_OkLatency_Format`/`OkLatencyIp_Format` or first-line-capped `EditProxy_Status_Failed_Format`; Details button visible iff `result.Transaction is not null` → `new HttpDetailsWindow(_lastTestTransaction).ShowDialog(this)`. Status coloring via classes (rule 29): `TestStatusText.Classes.Set("error", isError)` + window styles `TextBlock.test-status { Foreground=TextSecondaryBrush }` / `TextBlock.test-status.error { Foreground=ErrorBrush }`.
- Service member (replacing the throw at AvaloniaDialogService.cs:208-209; mirror the WPF `DialogService.cs:145-152` title logic):

```csharp
public async Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null)
{
    EditProxyWindow dialog = new(seed, Settings.AllowInvalidServerCertificates)
    {
        Title = title ?? Localizer.Instance[seed.Id == 0 ? "EditProxy_AddTitle" : "EditProxy_EditTitle"],
    };
    return await dialog.ShowDialog<ProxySettingDto?>(await GetOwnerOrRevealAsync());
}
```

  (`Settings` comes from `DialogServiceBase` — mirror how the WPF service reads `Settings.AllowInvalidServerCertificates`.)

- [ ] **Step 1: Port** (XAML + code-behind) + wire the service member + gallery buttons: `DialogEditProxyButton` through the REAL `ShowEditProxyDialogAsync` (fresh DTO — Host 127.0.0.1, Port 8080, the Task 1 driver shape); `DialogEditProxyTestedButton` constructing the window directly and poking `TestStatusText`/`TestDetailsButton` (internal x:Name fields, same assembly — the exact WPF driver technique) then `Show(this)` for the shot.
- [ ] **Step 2: Headless tests**: Save with valid fields → DTO (type/host/port/user/pass/enabled mapped; empty user/pass → null; `Id`/`Priority` carried); invalid port ("99999", "abc") → dialog stays open + a `MessageBoxWindow` appeared (dismiss it; snapshot-before-close); empty host → same with host focus; Cancel → null; Esc routes through IsCancel (rule 7's explicit handler). Do NOT drive the Test button headlessly (real socket) — `TryBuildDtoFromFields` is the shared validation surface and is covered above; say so in a comment.
- [ ] **Step 3: Bridge session**: `editproxy` + `editproxy-tested` shots light+dark via the two buttons; **masking check**: `ava_search`/`ava_props` on the open dialog must show the PasswordBox masked/redacted, not `"not-a-real-password"` (prep item 9 evidence — record the observed behavior); close via Cancel through the bridge. Contact sheet + Read pairs vs the WPF references (expected divergences: password dots, Fluent combo chrome).
- [ ] **Step 4:** Full suite gate; `grep -c "throw new NotImplementedException" src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` → **2** (the `throw`-anchored form is deliberate: the class doc carries a `<see cref="NotImplementedException"/>` a bare grep would miscount — reviewer-verified: 3 throws at phase start, 3→2→0). **Commit** — `"feat(avalonia): EditProxyWindow (test flow, details, validation) + ShowEditProxyDialogAsync wired"`

---

### Task 9: EditAccountWindow + ShowAddAccountDialogAsync/ShowEditAccountDialogAsync

Prep items 9 and 10. The service's **NotImplemented count reaches ZERO** here — the phase KPI.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/EditAccountWindow.axaml(+.cs)`
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` (both account members :202-206 → real), `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml(+.cs)` (+7 buttons)
- Test: `tests/CSUploader.Avalonia.Tests/Views/EditAccountWindowTests.cs`, `tests/CSUploader.Avalonia.Tests/TestSupport/FakeInteractiveLogin.cs`

**Interfaces:**
- Produces: `EditAccountWindow` with the **nullable ctor** `(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>>? interactiveLogin = null)` (prep item 10 — null disables Sign-in with the `EditAccount_SignIn_Unavailable` hint, the WPF contract :190-198; the service always passes non-null through); result via `ShowDialog<FileHosterLoginDto?>`; both real service members; `FakeInteractiveLogin` with `Success(AccountCheckResult)`, `Failure(string message, string? detail)`, `Throws(Exception)` factories.
- Consumes: Task 7 (`HosterCredentialModes` + style classes), `HosterIconConverter` (Avalonia), `ErrorDetailsWindow` (Phase 4 — its first production consumer; update its "gallery-only opener" class doc), `MessageBoxWindow`, `AccountCheckResult` (Core record: `IsValid, AccountType, Message, PremiumExpiry, SessionCookie, SessionCookieExpiresUtc, PinnedProxyId, ApiKey, DerivedUsername, StorageUsedBytes, StorageQuotaBytes, Detail` — verified at plan time), `GetOwnerOrRevealAsync`.

**Port deltas beyond the rules table** (source: `src/Views/EditAccountWindow.xaml(+.cs)` — mirror the code-behind closely; it is the most stateful port of the phase):
- Window: `Width=420 Height=360` (fixed — NOT SizeToContent), `CanResize=False`, CenterOwner, icon, `SurfaceMutedBrush` + card Border, and **`Title="{loc:Loc EditAccount_WindowTitle}"` as the XAML default** (mirror EditAccountWindow.xaml:9) — this default IS the edit-mode title: `ShowEditAccountDialogAsync`'s null-title path deliberately leaves it in place, so dropping it would ship an untitled edit dialog. Row layout mirrors :110-125 (9 rows; collapsed rows take zero height — Avalonia `IsVisible=false` collapses, rule 5).
- Hoster row: add-mode (`account.Id == 0`) ComboBox with the icon+text ItemTemplate (:128-140, `HosterIconConverter`, rule 11) + `SelectionChanged → RefreshCredentialMode`; edit-mode locked Border (:141-156) with icon via the converter's `Convert` (Avalonia converter returns `IImage?` — mirror :115-116 with the Avalonia type).
- Credential modes: `RefreshCredentialMode` mirrors :168-204 using `HosterCredentialModes.GetMode(CurrentHoster())` — classic rows vs Sign-in row vs the OR-separator/ApiKey rows (hidden for session-cookie hosters); Sign-in enabled iff `_interactiveLogin is not null`, else the unavailable hint.
- **Masking (prep item 9): `PasswordBox` AND `ApiKeyBox` get `PasswordChar="●"`** — the recorded deviation from WPF's cleartext boxes; this is the ONLY masking lever (no VM bindings for the redactor to key on).
- Status/error plumbing: `ShowSignInStatus` (:211-218) via classes (rule 29 — `.success` = `SuccessBrush`, default = `TextSecondaryBrush`); `ShowSignInError` (:227-237) shows the height-capped error panel (`MaxHeight 48`, trim) + the Details **link** (rule 28: `TextBlock Classes="link"` + `PointerReleased`) → `new ErrorDetailsWindow(_lastSignInError).ShowDialog(this)` (:239-247).
- Sign-in click mirrors :249-313 exactly: re-entry guard, in-progress status, on success populate `ApiKeyBox` when a key came back, stash `_derivedUsername`/storage/cookie+expiry+pin (each only-overwrite-on-value), success text `EditAccount_SignIn_SuccessAs_Format`/`EditAccount_SignIn_Success`; failure → `ShowSignInError(result.Message ?? …FailedGeneric, result.Detail)`; catch → `ShowSignInError(ex.Message)`; finally re-enable.
- Save mirrors :315-414: webview branch requires key-or-cookie (validation message `EditAccount_Validation_RequireLoginOrApiKey`; focus Sign-in for cookie hosters, ApiKey box otherwise), classic branch requires both U/P (`EditAccount_Validation_RequireUsernameAndPassword`); both construct the DTO with the FULL carry set and `Close(dto)`. Validation boxes via `MessageBoxWindow.ShowErrorAsync(this, …)` (async — the Click handlers become `async void`, matching the WPF pattern).
- **The carry-field matrix (prep item 10 / the FileHosterLogin field checklist):** an edit-Save persists this DTO verbatim (no re-verify), so the window must carry, without a fresh sign-in: `Username` (via `_derivedUsername`), `StorageUsedBytes`, `StorageQuotaBytes`, `SessionCookie`, `SessionCookieExpiresUtc`, `PinnedProxyId`, `CreatedDateTime` — the 7 carried fields — plus `Id` and `AccountType` from `_original`. Dropping ANY of them silently blanks data on the next edit/refresh.
- Service members (replacing :202-206; mirror `DialogService.cs:128-143` and `:190-197` — open them):

```csharp
public async Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null)
{
    FileHosterLoginDto seed = new() { FileHosterName = hosterName, AccountType = AccountType.Free };
    EditAccountWindow dialog = new(seed, availableHosters, interactiveLogin)
    {
        // The true WPF mirror (DialogService.cs:138): the ADD member always overrides the
        // window title, defaulting to the add-flow title when the caller passed none.
        Title = title ?? Localizer.Instance["EditAccount_AddTitle"],
    };

    return await dialog.ShowDialog<FileHosterLoginDto?>(await GetOwnerOrRevealAsync());
}

public async Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null)
{
    EditAccountWindow dialog = new(account, hosters, interactiveLogin);
    if (title is not null)
    {
        dialog.Title = title; // null keeps the XAML default (the edit flow, IDialogService.cs:99-104)
    }

    return await dialog.ShowDialog<FileHosterLoginDto?>(await GetOwnerOrRevealAsync());
}
```

  Note: `ShowAddAccountDialogAsync` may be invoked from inside the modal wizard (UploadWizardViewModel:832, Phase 6) — the owner resolver's active-window rule already parents it correctly; nothing extra to do, just don't hardcode MainWindow.

- [ ] **Step 1: Port** (XAML + code-behind) + both service members. **Also rewrite the now-stale `AvaloniaDialogService` class summary** (its :11-24 doc still says the three editor members "stay `NotImplementedException` until Phase 5 builds their windows" — false after this task): state that every member is real as of Phase 5 (editors via `GetOwnerOrRevealAsync` + `ShowDialog<T>`), and drop the `<see cref="NotImplementedException"/>` reference (it would also pollute a non-anchored grep).
- [ ] **Step 2: Gallery.** Four shot buttons mirroring the Task 1 WPF factory table exactly (`DialogEditAccountClassicButton` = edit-mode Rapidgator w/ fake U/P; `…ApiKeyButton` = add-mode KatFile w/ fake key; `…CookieButton` = add-mode Isracloud; `…ErrorButton` = KatFile + poked error panel — internal x:Name fields, same assembly), all `interactiveLogin: null` (Sign-in disabled — matches the WPF reference cells) and direct `ShowDialog(this)`. Three harness buttons through the REAL `ShowEditAccountDialogAsync`: `AccountSignInSuccessButton` (fake callback returning `new AccountCheckResult(true, AccountType.Premium, ApiKey: "fake-api-key-0123456789abcdef", DerivedUsername: "fake_kat_user", StorageUsedBytes: 1L<<30, StorageQuotaBytes: 10L<<30)`), `AccountSignInFailureButton` (`new AccountCheckResult(false, AccountType.Free, Message: "Sign-in failed: invalid credentials", Detail: SynthErrorDetail)`), `AccountSignInThrowButton` (callback throwing `new InvalidOperationException("Synthesized WebView failure")`).
- [ ] **Step 3: Headless tests** (drive buttons via the Phase 3 TestSupport input helpers; per-test fallback = handler invocation + `Closed` assertion, record which — the Phase 4 §8 convention):
  - **Carry matrix** (the load-bearing one, table-driven): a DTO with ALL of Id/Username/Password/ApiKey/AccountType/StorageUsedBytes/StorageQuotaBytes/SessionCookie/SessionCookieExpiresUtc/PinnedProxyId/CreatedDateTime set → open as KatFile (api-key mode), click Save with no sign-in → returned DTO carries every carried field verbatim (and `Username` == the original's, via `_derivedUsername` seeding :122). Repeat for a classic hoster (Rapidgator) asserting the :394-412 branch (edited U/P + carried session/storage/created).
  - Mode switching: combo Rapidgator→KatFile→Isracloud flips row visibility per mode (classic rows / sign-in+key rows / sign-in-only) and RESETS the sign-in status each switch (:196-203).
  - Fake-callback outcomes: Success → ApiKeyBox text set, success status visible w/ `success` class, Save carries `DerivedUsername`+storage; Failure → error panel visible + status hidden, Details opens `ErrorDetailsWindow` containing the FULL `Detail` (snapshot windows, `finally`-close); Throws → error panel with the exception message.
  - Validation: KatFile + empty key + no cookie → Save keeps window open + MessageBoxWindow shown; Isracloud same (focus target = Sign-in button); classic + empty password same.
  - Nullable ctor: null callback → Sign-in disabled + unavailable hint.
- [ ] **Step 4: Bridge session.** The 4 matrix shots ×2 themes (`editaccount-classic/apikey/cookie/error`); drive `AccountSignInSuccessButton` → click Sign in through the bridge → status flips to "✓ Signed in as fake_kat_user" (`_editaccount-signedin.png`); **masking check**: `ava_props` on the open dialog shows PasswordBox/ApiKeyBox masked (record — prep item 9's SECURITY evidence); close everything via Cancel clicks. Contact sheet + Read all four pairs vs WPF (expected divergences: masked boxes vs WPF cleartext, Fluent combo/checkbox chrome; anything else gets fixed or an arbitration note).
- [ ] **Step 5:** Full suite gate; `grep -c "throw new NotImplementedException" src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` → **0**. **Commit** — `"feat(avalonia): EditAccountWindow (3 credential modes, sign-in harness, carry-fields) + both account dialog members — AvaloniaDialogService NotImplemented count is zero"`

---

### Task 10: Phase gate — review, tag, reconcile

- [ ] **Step 1: Whole-diff review**: `git diff phase4-dialogs-ready..HEAD` by a fresh adversarial reviewer (whole-diff panels catch cross-task issues — the Order-column precedent; per-task reviews already happened). Special attention: the carry-field matrix vs the WPF Save branches, the persistence twin's format vs the WPF original, and every recorded deviation.
- [ ] **Step 2: Gates**:
  - `grep -rn "System.Windows" src/CSUploader.Avalonia/` → zero; `src/CSUploader.Core/` → zero.
  - `grep -c "throw new NotImplementedException" src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs` → **0** — the phase KPI (throw-anchored: the class doc's `<see cref>` reference must not count; the doc itself is rewritten in Task 9).
  - Both suites green; final counts recorded (1178+HosterCredentialModes additions / 223+additions).
  - i18n gate green (`md-to-resx.py --check`); the phase diff shows **zero `Strings*.resx` changes**.
  - WPF-head safety: `git diff phase4-dialogs-ready..HEAD -- src/` outside `src/CSUploader.Avalonia/**` and `src/CSUploader.Core/**` touches ONLY `src/Services/ReferenceShotCapture.cs` (inside `#if DEBUG`) and `src/Views/EditAccountWindow.xaml.cs` (hoist consumption). Release WPF build succeeds.
  - Avalonia Release build succeeds; launched WITHOUT flags: no gallery, no probe leftovers (GroupingProbeWindow deleted in Task 5 — verify), Uploaded/Logs tabs live, editors reachable only through real flows.
  - Contact sheet complete: `editaccount-classic/apikey/cookie/error`, `editproxy`, `editproxy-tested`, `mainwindow-uploaded`, `mainwindow-logs` all have all four cells; every pair Read and arbitrated; the accepted-divergence list written out (at minimum: PasswordChar masking vs WPF cleartext; log-row content variance; any Fluent chrome rulings).
  - The two remaining empty MainWindow tabs are Uploads + Settings (Phase 6) — pin that in the gate notes the way Phase 4 pinned the 3 editor members.
- [ ] **Step 3:** `git tag phase5-medium-views-ready`.
- [ ] **Step 4: Reconcile the design doc** with Phase 5's outcomes — at minimum: the grouping verdicts (view built in code-behind, RowGroupTheme recipe, collapse-state-on-rebuild behavior, Ctrl+C/synthetic-KeyDown verdict, zebra alternation basis), the ElementStyle port-rule outcome (rule 20's final wording), the column-persistence decision (verbatim twin + shared menu helper; format-hoist REJECTED with rationale — revisit at cutover; ALSO correct the design's "column visibility/order/width persistence" wording — WPF never persisted width, and the twin deliberately mirrors visibility+order only), the reorder-event name, `ContextMenu.Opening` cancelability findings, the Core IVT grant, the PasswordChar deviation (maintainer-visible product change), and Phase 6 pointers (SelectRowOnRightClick's consumer; editors already reachable from the wizard path). Commit — `"docs: reconcile design with Phase 5 outcomes (grouping recipe, persistence twin, editor deviations)"`.
- [ ] **Step 5: Surface to the maintainer** (via the team lead): the contact-sheet path + driver usage (`--shots --dialogs` editor matrix / `--agent --gallery` buttons); **the PasswordChar deviation** (his editors now mask password/API-key boxes where WPF showed cleartext — deliberate, per the agent-security rule; flag for his sign-off); the grouping-probe verdict; the format-hoist judgment call; standing reminders (Phase 1 merge-back if still unmerged; Buzzheavier master-merge checklist incl. BitmapImageResources; the ProgressWindow keep-vs-delete question from Phase 4 if still open).

---

## Reality-check register

Things this plan cites against the installed bits. Items marked **CONFIRMED (plan review)** were verified by reflection over the installed assemblies during plan review — executors must NOT burn time re-deriving them; only the stated residual (runtime behavior, template internals) remains to check. Unmarked items are open and must be verified before/while coding:

1. **CONFIRMED (plan review, by reflection over the installed 11.3.13):** `DataGridCollectionView`, `DataGridPathGroupDescription`, and `DataGrid.CollapseRowGroup`/`ExpandRowGroup` exist as cited — do not re-derive. What remains for Task 2 is the RUNTIME grouping behavior (the checklist), not the API surface.
2. **`DataGridRowGroupHeader` template PART names** (expander toggle, item-count presenter) remain a probe item — extract via ILSpy/Avalonia source for 11.3.13 before re-templating (Task 2 Step 2). The PROPERTY surface is CONFIRMED (plan review): `DataGrid.RowGroupTheme` and `SublevelIndent` exist — wire per rule 27.
3. **Synthetic Ctrl+C**: whether `grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control, … })` reaches the DataGrid's internal clipboard path — Task 2 checklist 4 decides; fallback for the context-menu Copy: compose header+rows text in code-behind from the columns' `ClipboardContentBinding` values and set via `Window.Clipboard`.
4. **CONFIRMED (plan review):** the group-header right-click route exists — `DataGridRowGroupHeader` is reachable by ancestor walk and exposes its group's items (`DataGridCollectionViewGroup.Items`). Task 2 checklist 5 records the exact enumeration code Task 5 copies; no re-derivation.
5. **CONFIRMED (plan review):** `ContextMenu.Opening` is cancelable on 11.3.18 (the WPF `ContextMenuOpening` twin) — rule 18's tunnel-`ContextRequested` fallback is retired unless Task 3's `menu.Open(header)` + `e.Handled` interplay misbehaves at runtime (verify that interplay once, in Task 3 Step 2).
6. **`#ElementName` bindings inside ContextMenu popups** (name-scope) — avoided by design via code-behind `CommandParameter` assignment (rule 19); if a port finds the binding DOES resolve, still keep the code-behind form for consistency.
7. **Binding-valued style setters for `ToolTip.Tip` on cell descendants** (rule 20) — if setters can't carry per-item bindings on 11.3.18, fall back to template columns for the tooltip-carrying cells and record it in the rule-20 verdict.
8. **`DataGridRow.Index` exists — CONFIRMED (plan review).** What remains is its numbering on a GROUPED view (flat item index vs slot index) — Task 2 checklist 7; the zebra helper implements the recorded basis.
9. **CONFIRMED (plan review):** `MenuItem.StaysOpenOnClick` AND `MenuItemToggleType.CheckBox` are present on 11.3.18 — use both (rule 31); no degradation path needed.
10. **CONFIRMED (plan review):** `SortMemberPath` + `ClipboardContentBinding` live on the base `DataGridColumn` class, so template columns (Name/Hoster/URL) carry them. The RUNTIME built-in-copy semantics on a grouped view remain probe item #3.
11. **CONFIRMED (plan review):** `ColumnDisplayIndexChanged` exists on 11.3.13's DataGrid — wire it for reorder persistence (Tasks 5/6); no fallback needed.
12. **Headless `ShowDialog<T>` + input-sim interplay for the editors** (Phase 4 §8's finding carries): per-test fallback = `Show()` + handler invocation + `Closed`/`Outcome` assertion; record which tests fell back.
13. **`ava_props`/bridge masking of PasswordChar boxes** — prep item 9 asserts PasswordChar is the masking lever; verify once in Task 8's session and once in Task 9's (different box types) and record what the bridge actually returns for a masked box.
14. **`HosterIconConverter` (Avalonia) return type in the locked-hoster border** — mirror what the converter actually returns (`IImage`/`Bitmap`) at :115-116's port site.
15. **Log-grid `RowHeight=22` vs WPF `MaxHeight=22`** — visually compare in the Task 6 sheet pair; if Fluent's default row chrome fights 22px, adjust and record (the WPF grids read compact).
16. **`Logger.Current` reachability from `ava_eval`** (Task 6's log-burst drive) — if the eval sandbox can't touch statics, fall back to driving entries through `MainViewModel`'s registered `IAppLogger` via DI (`ava_eval` on the service provider) or accept startup-entries-only evidence; record.
