# Avalonia Migration Phase 1: Core Split + ViewModel Purification — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract `CSUploader.Core` (all non-UI code + purified ViewModels) so the coming Avalonia head and the existing WPF head share one framework-free core, with the WPF app fully working and the whole test suite green after every task.

**Architecture:** Strangler step 1 from the design doc (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`). Pure-rename move commits first (git rename detection stays intact for master merges), then purification commits that remove `System.Windows.*` from ViewModels/services via head-implemented interfaces, then the purified ViewModels move.

**Tech Stack:** .NET 10, MSBuild SDK projects, CommunityToolkit.Mvvm 8.4.2, EF Core 10, xunit 2.9.3 + Moq.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`. Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree, has uncommitted work).
- Builds/tests use a temp OutDir to dodge file locks: `dotnet test -p:OutDir=D:\temp2\cbuild-mig\tests` / `dotnet build -p:OutDir=D:\temp2\cbuild-mig\wpf`. **The FULL suite must pass after every task** — that is the definition of done for each.
- `LangVersion` stays `preview` in every csproj (CommunityToolkit.Mvvm source-gen breaks on .NET 10 defaults otherwise). `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0` everywhere.
- **Move commits are PURE RENAMES**: `git mv` only, zero content edits in the same commit (except the csproj/xmlns edits explicitly listed in the same task — keep those in a separate commit within the task when noted). Run `git show --stat -M100%` to verify 100% similarity before finalizing a move commit.
- No behavior changes anywhere in this phase. The WPF app must look and act identically.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- C# namespaces NEVER change in this phase (`RootNamespace=CSUploader` in Core).
- When a task says "derive from call site", open the cited file:line and mirror the existing arguments/types exactly — the rule is: interface signatures use only Core-safe types (DTOs, primitives, Core types); no `System.Windows.*` in any Core file (final gate: `grep -r "System.Windows" src/CSUploader.Core/` returns nothing).

---

### Task 1: Create `CSUploader.Core` project + solution wiring

**Files:**
- Create: `src/CSUploader.Core/CSUploader.Core.csproj`
- Modify: `CSUploader.sln` (add project), `src/CSUploader.csproj` (add ProjectReference)

**Interfaces:**
- Produces: empty Core assembly (`AssemblyName=CSUploader.Core`, `RootNamespace=CSUploader`) with `InternalsVisibleTo` for `CSUploader.Tests` AND `CSUploader` (the head may touch internals during the split); head references Core.

- [ ] **Step 1: Write the Core csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <RootNamespace>CSUploader</RootNamespace>
    <AssemblyName>CSUploader.Core</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.9" />
    <!-- Same CVE-pinning rationale as the head csproj (see its comment). -->
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="3.0.2" />
    <PackageReference Include="SourceGear.sqlite3" Version="3.50.4.5" />
    <PackageReference Include="Velopack" Version="1.2.0" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="CSUploader.Tests" />
    <InternalsVisibleTo Include="CSUploader" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add to solution, reference from head**

Run: `dotnet sln add src/CSUploader.Core/CSUploader.Core.csproj`
In `src/CSUploader.csproj` add:
```xml
  <ItemGroup>
    <ProjectReference Include="..\CSUploader.Core\CSUploader.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Build + full suite**

Run: `dotnet test -p:OutDir=D:\temp2\cbuild-mig\tests` → all green (no code moved yet).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "build: add empty CSUploader.Core project (strangler step 1)"
```

---

### Task 2: Move wave A — all WPF-free code, as pure renames

**Files (git mv, exhaustive list):**
- `src/Dal/**` → `src/CSUploader.Core/Dal/`
- `src/Upload/**` → `src/CSUploader.Core/Upload/`
- `src/Lib/**` → `src/CSUploader.Core/Lib/` **EXCEPT** `src/Lib/UI/` (both files stay) and `src/Lib/Localization/LocExtension.cs` (stays; `Localizer.cs` + `LocalizedOption.cs` move)
- `src/FirstRun.cs` → `src/CSUploader.Core/FirstRun.cs`
- `src/GlobalUsings.cs` → `src/CSUploader.Core/GlobalUsings.cs` (then recreate an identical copy at `src/GlobalUsings.cs` — the head still needs `global using System.IO; global using System.Net.Http;` — this is the ONE allowed content addition, in the follow-up commit)
- `src/Services/{ConfirmationKeys,IDialogService,IInteractiveAuthService,IToastHost,IToastNotificationService,IToastWindowFactory,UploadNotificationListener}.cs` → `src/CSUploader.Core/Services/`
- `src/Resources/Strings.resx` + `Strings.{zh-Hans,ko,ja,vi,fil}.resx` → `src/CSUploader.Core/Resources/` (SDK glob embeds them; manifest stays `CSUploader.Resources.Strings` because RootNamespace=CSUploader — verify with `ildasm`/`dotnet-ildasm` or by running the app and switching language)
- ViewModels (WPF-free ones ONLY — verify each with `grep -L "System.Windows" src/ViewModels/*.cs` before moving; expected movers include `UploadWizardViewModel.cs`, `FileHosterSelectionViewModel.cs`, `LogsViewModel.cs`, `ToastViewModel.cs` + helper/value types; the five dirty ones — Main, Uploads, Uploaded, ConnectionManager, Settings — STAY until Tasks 5-10)
- STAY in head: `src/Services/{DialogService,DefaultToastWindowFactory,ToastWindowHost,TrayIconManager,WebViewInteractiveAuthService,ToastNotificationService}.cs` (ToastNotificationService has System.Windows.Rect — moves in Task 9), `src/Lib/UI/*`, `src/Lib/Localization/LocExtension.cs`, `src/Behaviors`, `src/Converters`, `src/Views`, `src/App.xaml*`, `src/Properties/**`, `src/Resources/{Tokens,Theme.Light,Theme.Dark,ImageResources}.xaml`, `src/stylecop.json` (copy it into Core too if the analyzers are wired via it — check how stylecop is configured; if it's an AdditionalFile in the csproj, mirror that in Core).

**Interfaces:**
- Produces: Core compiles with everything above; head compiles referencing Core; zero namespace changes.

- [ ] **Step 1: Pure-rename commit**

`git mv` everything listed. NO other edits. Commit:
```bash
git commit -m "refactor: move framework-free code to CSUploader.Core (pure renames)"
git show --stat -M100% HEAD | tail -20   # verify: every line 'rename ... (100%)'
```
(The tree does NOT build at this commit — acceptable; the follow-up commit lands within minutes and the pair is atomic for merges. Do not push between them.)

- [ ] **Step 2: Compile-fix commit (mechanical, no logic)**

1. Recreate `src/GlobalUsings.cs` (identical content + file header).
2. Head XAML xmlns edits — ONLY the views whose vm: types move NOW (reviewer-verified: no view references both a moved and a staying VM type, so no dual prefix is ever needed): append `;assembly=CSUploader.Core` to `xmlns:vm` in **LogsView.xaml** (LogsViewModel), **UploadWizardWindow.xaml** (UploadWizardViewModel), **ToastWindow.xaml** (ToastViewModel), **LogDetailsWindow.xaml** (LogEntryViewModel), and to `xmlns:upload` in **LogsView.xaml:12** (SettingKey lives in src/Upload/SettingKey.cs → Core; used at :104/:137/:176/:209). **Do NOT touch** the vm: prefix in MainWindow/UploadedView/UploadsView (d:DesignInstance-only refs to staying VMs) or SettingsView (SettingsView.xaml:648 has `{x:Static vm:ConnectionManagerViewModel.ProxyTypeOptions}` — compile-resolved; editing it before ConnectionManagerViewModel moves BREAKS the build) — those four get their edit in Task 10. **Do NOT touch `xmlns:loc`** — LocExtension stays head-local.
3. Remove now-redundant PackageReferences from the head csproj that Core supplies transitively (EF, Hosting, SQLite pins, Velopack, CommunityToolkit.Mvvm) — keep WebView2, Ookii, SharpVectors.
3b. Update `tests/Localization/I18nRegenGateTests.cs` resx paths to `src/CSUploader.Core/Resources/Strings*.resx` (the md paths are unchanged).
4. Build head + run FULL suite: `dotnet test -p:OutDir=D:\temp2\cbuild-mig\tests` → green.
5. Launch the WPF app once from the build output; switch language in Settings to verify satellite resources resolve from Core (spot-check one localized string).

```bash
git add -A && git commit -m "refactor: fix compilation after Core move (xmlns assembly qualifiers, package refs)"
```

---

### Task 3: Split DI registration + shared runtime bootstrap

**Files:**
- Create: `src/CSUploader.Core/ServiceRegistration.cs`
- Modify: `src/App.xaml.cs` (ConfigureServices shrinks; OnStartup calls WireRuntime), `tests/StartupDISmokeTests.cs`

**Interfaces:**
- Produces:
  - `public static class ServiceRegistration` (namespace `CSUploader`) with
    `public static IServiceCollection AddCoreServices(this IServiceCollection services, string baseDirectory)` — everything currently in `App.ConfigureServices` EXCEPT: IDialogService/DialogService, IInteractiveAuthService/WebViewInteractiveAuthService, TrayIconManager, IToastWindowFactory/DefaultToastWindowFactory, IToastNotificationService factory lambda, and the six ViewModel registrations (all still head types or head-dependent — they migrate in Task 10);
  - `public static void WireRuntime(IServiceProvider provider)` — the AttemptRunner.AttemptCompleted → ProxyManager.ReportResult bridge + eager `provider.GetRequiredService<UploadNotificationListener>()` resolve, moved verbatim from App.OnStartup:41-54.
- Consumes: nothing new.

- [ ] **Step 1: Extract**

`App.ConfigureServices` becomes: `services.AddCoreServices(baseDirectory);` + the head-only registrations listed above (kept verbatim). `App.OnStartup` replaces its inline bridge/eager-resolve block with `ServiceRegistration.WireRuntime(_serviceProvider);`. Delete the vestigial `AppDomain.CurrentDomain.SetData("DataDirectory", baseDirectory);` line (design-approved: nothing reads it; the Sqlite connection string is absolute). Keep `Lib.UI.ImmersiveDarkMode.RegisterGlobalHandler()` in OnStartup (head-only).

- [ ] **Step 2: Add the Core-only DI smoke test**

In `tests/StartupDISmokeTests.cs`, alongside the existing test (which keeps exercising `App.ConfigureServices` for the full head graph), add:

```csharp
[Fact]
public void AddCoreServices_ResolvesCoreGraphWithoutUiRegistrations()
{
    ServiceCollection services = new();
    services.AddCoreServices(Path.GetTempPath());
    // Head-implemented interfaces get test doubles so core singletons that depend on them resolve.
    services.AddSingleton(Mock.Of<IDialogService>());
    services.AddSingleton(Mock.Of<IInteractiveAuthService>());
    using ServiceProvider provider = services.BuildServiceProvider();

    Assert.NotNull(provider.GetRequiredService<PackageManager>());
    Assert.NotNull(provider.GetRequiredService<UploadScheduler>());
    Assert.NotNull(provider.GetRequiredService<Upload.Pipeline.AttemptRunner>());
    Assert.NotEmpty(provider.GetServices<Upload.Pipeline.IFileHosterPipeline>());
    ServiceRegistration.WireRuntime(provider); // must not throw
}
```

(Adjust the mocked interface list to whatever AddCoreServices actually requires — the DI graph tells you: run the test, add doubles until it resolves. IToastNotificationService is expected too, via UploadNotificationListener.)

- [ ] **Step 3: Full suite + app smoke** — `dotnet test -p:OutDir=D:\temp2\cbuild-mig\tests` green; launch app, upload tab renders, proxy health logging still works (check Logs tab shows startup entries).

- [ ] **Step 4: Commit** — `git add -A && git commit -m "refactor: extract AddCoreServices + WireRuntime shared bootstrap to Core"`

---

### Task 4: IDialogService goes async

**Files:**
- Modify: `src/CSUploader.Core/Services/IDialogService.cs`, `src/Services/DialogService.cs`, every call site (derive: `grep -rn "_dialogService\.\|DialogService\.\|dialogService\." src/ tests/ --include="*.cs"` — expect ~25 sites across MainViewModel, UploadsViewModel, UploadedViewModel, ConnectionManagerViewModel, SettingsViewModel, UploadWizardViewModel, WebViewInteractiveAuthService + test mocks)

**Interfaces:**
- Produces (complete new interface — same XML docs carried over):

```csharp
public interface IDialogService
{
    Task ShowErrorAsync(string message, string? title = null);
    Task<bool> ShowConfirmationAsync(string message, string? title = null);
    Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null);
    Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null);
    Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null);
    Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, string? title = null);
    Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null);
}
```

- [ ] **Step 1: Rename + wrap.** WPF `DialogService` implements each member by wrapping the existing sync body: `public Task ShowErrorAsync(...) { <existing body>; return Task.CompletedTask; }` / `Task.FromResult(<existing return>)`. No `Task.Run` — dialogs must stay on the UI thread.
- [ ] **Step 2: Convert call sites.** `[RelayCommand]` methods calling them become `async Task` (CommunityToolkit generates AsyncRelayCommand — command property NAMES stay identical, XAML bindings unaffected; verify one generated name before assuming). Plain methods become async and await; event handlers become `async void` only where already event handlers. In WebViewInteractiveAuthService:88 the ShowError call sits inside a Dispatcher.Invoke lambda — make the lambda async or hop out; preserve ordering (error shown before method returns).
- [ ] **Step 3: Fix test mocks and command invocations.** `Mock<IDialogService>` setups: `.Returns(true)` → `.ReturnsAsync(true)`. CAUTION: tests/CLAUDE.md's "invoke via ExecuteAsync" convention is NOT uniformly followed — there are ~40 sync `.Execute()` sites. Commands whose methods become async here turn those into fire-and-forget: audit and convert to `await ...ExecuteAsync(...)` at least **RemoveFailedCommand (ConnectionManagerViewModelTests ×3), RemoveSelectedCommand (UploadsViewModelRemoveTests ×2), BrowseFilesCommand (UploadWizardViewModelTests ×9)** — plus any other `.Execute(` site whose command gained an await (find them: `grep -n "Command.Execute(" tests/ -r`).
- [ ] **Step 4: Full suite green; app smoke: trigger one confirmation dialog (e.g. remove an upload) and one error path.**
- [ ] **Step 5: Commit** — `"refactor: IDialogService becomes fully async (Avalonia has no sync dialogs)"`

---

### Task 5: Grow IDialogService + IUpdateProgressSink; evict direct window construction from ViewModels

**Files:**
- Modify: `src/CSUploader.Core/Services/IDialogService.cs`, `src/Services/DialogService.cs`
- Create: `src/CSUploader.Core/Services/IUpdateProgressSink.cs`, `src/Services/WpfUpdateProgressSink.cs`
- Modify: `src/ViewModels/ConnectionManagerViewModel.cs` (:381 HttpDetailsWindow; :643, :787 ProxyTextDialog), `src/ViewModels/SettingsViewModel.cs` (:755, :1223 EditAccountWindow), `src/ViewModels/UploadsViewModel.cs` (:424 SpeedLimitDialog), `src/ViewModels/MainViewModel.cs` (:143 UpdateProgressWindow), `src/App.xaml.cs` (register the sink)

**Interfaces:**
- Produces: new IDialogService members — derive each signature from the existing window ctor + result usage at the cited line, using ONLY Core-safe types. Expected shape (adjust parameter lists to the real call sites):

```csharp
    // Reviewer-verified against the actual window ctors/results:
    Task ShowHttpDetailsAsync(HttpTransaction transaction);                       // HttpDetailsWindow.xaml.cs:22; HttpTransaction is Lib/Net/Http → Core
    Task<string?> ShowProxyTextDialogAsync(string title, string description,
        string initialText, bool readOnly);                                       // ProxyTextDialog.xaml.cs:17 + ResultText
    Task<int?> ShowSpeedLimitDialogAsync(int? currentLimit);                      // SpeedLimitDialog.xaml.cs:14 + int? Result
    Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account,
        string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin,
        string? title = null);                                                    // EditAccountWindow.xaml.cs:95; AccountCheckResult is src/Upload → Core
```

```csharp
// src/CSUploader.Core/Services/IUpdateProgressSink.cs
namespace CSUploader.Services;

/// <summary>Non-modal update-download progress surface. WPF: UpdateProgressWindow;
/// Avalonia head supplies its own. Open/Report/Close are UI-thread-safe.</summary>
public interface IUpdateProgressSink
{
    void Open();
    void Report(int percent);
    void Close();
}
```

THE RULE: VM keeps identical observable behavior; only construction moves.
- Consumes: Task 4's async interface style.
- Test churn IN THIS TASK: `MainViewModelUpdateTests.BuildScopedProvider` (CreateVm:156) must register the new `IUpdateProgressSink` (a Mock) or `new MainViewModel(scoped)` throws — and that test exercises exactly the update-progress flow this task reroutes; assert against the mock sink instead of the window.

- [ ] **Step 1:** Implement the WPF sides in DialogService/WpfUpdateProgressSink by MOVING the existing construction code out of the VMs verbatim (owner resolution via `Application.Current.MainWindow` lives in the service now).
- [ ] **Step 2:** Replace the 7 VM sites with interface calls; delete the now-unused `using CSUploader.Views;`/`System.Windows` imports where that was the last use.
- [ ] **Step 3:** Full suite + app smoke of each affected dialog (HTTP details from Logs, proxy import/export text, speed limit, edit account, check-for-updates progress).
- [ ] **Step 4: Commit** — `"refactor: route all VM-launched dialogs through IDialogService/IUpdateProgressSink"`

---

### Task 6: IUiDispatcher + timer abstraction

**Files:**
- Create: `src/CSUploader.Core/Services/IUiDispatcher.cs`, `src/Services/WpfUiDispatcher.cs`
- Modify: `src/ViewModels/{MainViewModel,UploadsViewModel,UploadedViewModel,ConnectionManagerViewModel}.cs`, `src/App.xaml.cs` (register), `src/Services/ToastNotificationService.cs` consumers unaffected (its dispatch callback is already injected)

**Interfaces:**
- Produces:

```csharp
namespace CSUploader.Services;

public interface IUiDispatcher
{
    /// <summary>Fire-and-forget marshal to the UI thread (WPF Dispatcher.BeginInvoke).</summary>
    void Post(Action action);
    Task InvokeAsync(Action action);
    /// <summary>Creates a STOPPED UI-thread timer; caller starts it.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action onTick);
}

public interface IUiTimer : IDisposable
{
    void Start();
    void Stop();
}
```

WPF impl wraps `Application.Current.Dispatcher` and `DispatcherTimer`. **Null-tolerant for tests, BEHAVIOR-PRESERVING**: when `Application.Current` is null, `Post` is a **no-op** (today's `Application.Current?.Dispatcher.BeginInvoke` is a no-op in headless tests — UploadsViewModelRemoveTests relies on exactly that and mirrors grid state manually; running inline instead would change test behavior), `CreateTimer` returns a no-op timer (fixes the UploadsViewModel unconditional-DispatcherTimer hazard), and `InvokeAsync` runs inline (no current VM site uses a null-guarded synchronous Invoke — verify while converting). This replaces the VMs' scattered `Application.Current?` guards. Register as singleton.

- [ ] **Step 1:** Implement + register; convert the 4 VMs' `Dispatcher.BeginInvoke`/`DispatcherTimer` uses (UploadsViewModel ×5 + ctor timer, MainViewModel guarded timer + BeginInvoke, UploadedViewModel, ConnectionManagerViewModel :214 …). Preserve each site's exact semantics (BeginInvoke → Post; awaited Invoke → InvokeAsync).
- [ ] **Step 2:** Also swap the toast factory lambda's `dispatchToUi` in App.xaml.cs to `sp.GetRequiredService<IUiDispatcher>().Post` (same behavior).
- [ ] **Step 3: Test churn IN THIS TASK**: VM ctors gain IUiDispatcher — update direct construction sites: `ConnectionManagerViewModelTests.CreateVm:60`, `UploadsViewModelRemoveTests.CreateVmShowing:97`, `UploadedViewModelTests.CreateVm:254`, and `MainViewModelUpdateTests.BuildScopedProvider` registers IUiDispatcher (pass/register the real WpfUiDispatcher — it is inert without Application.Current — or a trivial fake).
- [ ] **Step 4:** Full suite; run UploadsViewModelRemoveTests 3× (timer hazard gone, Post still no-op under test — no new flakes, no newly-inline continuations).
- [ ] **Step 5: Commit** — `"refactor: abstract UI-thread marshaling behind IUiDispatcher (fixes VM timer test hazard)"`

---

### Task 7: IClipboardService, IUiShell, ITrayIconService, IFontEnumerationService, IThemeApplier

**Files:**
- Create: `src/CSUploader.Core/Services/{IClipboardService,IUiShell,ITrayIconService,IFontEnumerationService,IThemeApplier}.cs`, `src/Services/{WpfClipboardService,WpfUiShell,WpfFontEnumerationService,WpfThemeApplier}.cs`
- Modify: `src/Services/TrayIconManager.cs` (implements ITrayIconService), the 5 VMs, `src/App.xaml.cs`

**Interfaces:**
- Produces:

```csharp
// Reviewer-verified against the actual call sites:
public interface IClipboardService
{
    Task SetTextAsync(string text);
    Task ClearAsync();                 // UploadedViewModel:118 calls Clipboard.Clear()
}

public interface IUiShell
{
    void ActivateMainWindow();
    void Shutdown();
}

public interface ITrayIconService
{
    void UpdateVisibility();           // PARAMETERLESS — TrayIconManager.cs:33; sites SettingsViewModel:551/:568
    void NotifyHidden();
    void ShowMainWindow();
}

public interface IFontEnumerationService { IReadOnlyList<string> GetSystemFontFamilyNames(); }

public interface IThemeApplier
{
    /// <summary>Mirrors SettingsViewModel.ApplyGridFontResources (:420-437): writes BOTH
    /// Resources["GridFontFamily"] and Resources["GridFontSize"]. There is NO runtime
    /// light/dark swap in Phase 1 (App.xaml merges Theme.Light at startup) — no ApplyTheme member.</summary>
    void ApplyGridFont(string family, double size);
}
```

GOTCHA (reviewer-verified): `SettingsViewModel.GridFontFamilyOptions` is a STATIC property initialized inline from `Fonts.SystemFontFamilies` (:85) — a static initializer can't consume DI. Convert it to an INSTANCE property populated from IFontEnumerationService in the ctor; `SettingsView.xaml:212` binds `{Binding GridFontFamilyOptions}`, which resolves instance properties, so no XAML change. SettingsViewModel's `TrayIconManager?` ctor param becomes `ITrayIconService?` (:28/:38).
- Consumes: IUiDispatcher (Task 6) where marshaling wraps these calls today.

- [ ] **Step 1:** Implement + register + convert the VMs. Clipboard call sites (`UploadsViewModel:674`, UploadedViewModel, dialogs' code-behind can keep using WPF Clipboard directly — ONLY VM sites convert) become `await` (commands go async as in Task 4).
- [ ] **Step 2:** After conversion run `grep -n "System.Windows" src/ViewModels/*.cs` — expected remaining hits ONLY in UploadsViewModel (ICollectionView, Task 8) and none in Main/Uploaded/ConnectionManager/Settings. If Settings still hits on `System.Windows.Media` fonts, the IFontEnumerationService shape is wrong — fix it, don't leave a leak.
- [ ] **Step 2b: Test churn IN THIS TASK**: ctor updates at `SettingsViewModelTests.CreateVm:61` (ITrayIconService? mock), `UploadsViewModelRemoveTests.CreateVmShowing:97` (IClipboardService), `UploadedViewModelTests.CreateVm:254`, plus IUiShell/IClipboardService registrations in `MainViewModelUpdateTests.BuildScopedProvider` if MainViewModel gained them.
- [ ] **Step 3:** Full suite + app smoke: copy-link from Uploads grid, tray hide/show, theme switch light↔dark, grid font change, exit via menu.
- [ ] **Step 4: Commit** — `"refactor: purify VMs — clipboard/shell/tray/fonts/theme behind Core interfaces"`

---

### Task 8: UploadsViewModel sheds ICollectionView

**Files:**
- Modify: `src/ViewModels/UploadsViewModel.cs` (:11 using System.Windows.Data; :135-147 FilteredRows; Refresh at :166/:302), `src/Views/UploadsView.xaml` (ItemsSource binding), `src/Views/UploadsView.xaml.cs` (view construction), tests touching FilteredRows

**Interfaces:**
- Produces (VM surface):

```csharp
public ObservableCollection<UploadRowViewModel> VisibleRows { get; }   // existing collection, name per current code
public bool MatchesFilter(object item);                                 // predicate formerly assigned to ICollectionView.Filter
public event EventHandler? FilterInvalidated;                           // raised where .Refresh() is called today (:166, :302) and on FilterText change
```

- Consumes: nothing new. The WPF head re-creates the view UI-side:

```csharp
// UploadsView.xaml.cs ctor, after InitializeComponent + DataContext resolution:
ICollectionView view = CollectionViewSource.GetDefaultView(_viewModel.VisibleRows);
view.Filter = _viewModel.MatchesFilter;
_viewModel.FilterInvalidated += (_, _) => view.Refresh();
UploadsGrid.ItemsSource = view;   // XAML ItemsSource binding to FilteredRows is removed
```

- [ ] **Step 1:** Refactor VM (delete FilteredRows + using System.Windows.Data), wire the view as above, adjust tests (assert against VisibleRows + MatchesFilter instead of FilteredRows contents; a filter test becomes: set FilterText, assert MatchesFilter(row) outcomes + FilterInvalidated raised).
- [ ] **Step 2:** Full suite + app smoke: type in the Uploads filter box, rows filter live; clear → all rows return; sorting still works.
- [ ] **Step 3: Commit** — `"refactor: UploadsViewModel exposes filter predicate; heads own their collection views"`

---

### Task 9: ToastNotificationService sheds System.Windows.Rect (DIP contract) and moves to Core

**Files:**
- Create: `src/CSUploader.Core/Services/DipRect.cs`
- Modify: `src/Services/ToastNotificationService.cs` (then `git mv` to `src/CSUploader.Core/Services/` in a separate pure-rename commit), `src/Services/IToastHost.cs` if it exposes Rect (already in Core — grep said clean, verify), `src/App.xaml.cs` (workAreaProvider lambda), `tests/Services/ToastNotificationServiceTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace CSUploader.Services;

/// <summary>Device-independent-pixel rectangle. ALL toast geometry (work area, host
/// Top/Left/Height) is in DIPs — the WPF head passes SystemParameters.WorkArea verbatim;
/// the Avalonia head must convert Screens' physical pixels via Screen.Scaling.</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
```

- [ ] **Step 1:** Swap `System.Windows.Rect` → `DipRect` inside ToastNotificationService (+ the App.xaml.cs lambda: `() => { var wa = SystemParameters.WorkArea; return new DipRect(wa.X, wa.Y, wa.Width, wa.Height); }`), update the test file's rects. Commit: `"refactor: toast geometry is framework-free DipRect (DIP contract)"`.
- [ ] **Step 2:** `git mv src/Services/ToastNotificationService.cs src/CSUploader.Core/Services/` — pure rename commit: `"refactor: move ToastNotificationService to Core (pure rename)"`. Full suite between and after.

---

### Task 10: Move the five purified ViewModels to Core; migrate VM registrations

**Files:**
- `git mv src/ViewModels/{MainViewModel,UploadsViewModel,UploadedViewModel,ConnectionManagerViewModel,SettingsViewModel}.cs src/CSUploader.Core/ViewModels/` (+ any helper types that stayed with them)
- Modify: `src/App.xaml.cs` (the six VM registrations move into `ServiceRegistration.AddCoreServices`), head XAML — the four DEFERRED xmlns edits from Task 2: append `;assembly=CSUploader.Core` to `xmlns:vm` in **MainWindow.xaml, SettingsView.xaml, UploadedView.xaml, UploadsView.xaml** (SettingsView's `x:Static vm:ConnectionManagerViewModel.ProxyTypeOptions` at :648 now resolves in Core — both its VM types move together here)

**Interfaces:**
- Produces: `grep -r "System.Windows" src/CSUploader.Core/` → ZERO hits (the phase gate). All ViewModels resolve from Core registrations.

- [ ] **Step 1:** Pre-move gate: `grep -n "System.Windows\|CSUploader.Views" src/ViewModels/*.cs` → must be empty. If not, a prior task leaked — fix there first.
- [ ] **Step 2:** Pure-rename commit, then a compile-fix commit (registration move + xmlns collapse), full suite green between/after as in Task 2's pattern.
- [ ] **Step 3:** App smoke: full manual pass — every tab renders, upload wizard opens, settings panels switch, logs populate, toasts fire (use the fake-data seed if present; otherwise a paused local-file package).
- [ ] **Step 4: Commit(s)** — `"refactor: move purified ViewModels to Core (pure renames)"` + `"refactor: VM registrations live in AddCoreServices"`

---

### Task 11: Phase gate — review + merge-back artifact

- [ ] **Step 1:** Run the whole-diff review: full `git diff master...avalonia-migration` reviewed by a fresh adversarial reviewer (per-task reviews already happened; this catches cross-task issues — the repo's history shows final whole-diff reviews catch what per-task reviews miss).
- [ ] **Step 2:** `grep -r "System.Windows" src/CSUploader.Core/` → zero. Full suite green. i18n gate green.
- [ ] **Step 3:** Tag: `git tag phase1-core-split-ready`. NOTE: we cannot merge into `master` from this worktree (master is checked out in the maintainer's main tree, which holds uncommitted Buzzheavier work). The design's early merge-back therefore becomes: **surface to the maintainer** — "Phase 1 is merge-ready; merging it to master early keeps future hoster work on the shared layout; your uncommitted Buzzheavier edits will follow the renames automatically on rebase/merge (content-edit vs pure-rename)". the maintainer merges when convenient; the migration continues on the branch regardless.
- [ ] **Step 4:** Update the design doc's §Merge protocol if anything learned here changed it; commit.
