# Avalonia Migration Phase 2: Avalonia Shell + WebView2 GO/NO-GO Spike — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `CSUploader.Avalonia` head — project, DI composition on `AddCoreServices`, stub-or-real implementations of every head-supplied interface, empty tabbed MainWindow, AvaDevBridge wiring, `--agent` safety guard — prove the MCP dev loop end-to-end with a live screenshot, then run the **WebView2 GO/NO-GO spike** that gates the login architecture for Phase 8. Also lands the test-harness prep deferred from the Phase 1 gate (Task 0).

**Architecture:** Strangler step 2 from the design doc (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, §The Avalonia head, §MCP dev loop, §Phases "Phase 2 Task 0" + "Phase 2"). The WPF head stays the authoritative, fully-working app; the Avalonia head is a second executable sharing Core. Nothing in this phase ports any real view — Phase 2 delivers composition, tooling, and the spike verdict.

**Tech Stack:** .NET 10, Avalonia **11.3.18** + Avalonia.Controls.DataGrid **11.3.13** + Avalonia.Themes.Fluent, CommunityToolkit.Mvvm 8.4.2 (Core), Microsoft.Web.WebView2 1.0.4022.49 (spike), Avalonia.Headless.XUnit 11.3.18, xunit 2.9.3 + Moq, AvaDevBridge/AvaDevMcp from `E:\Projects\avalonia-agent-mcp` (protocol 2, prebuilt exe already referenced by the committed `.mcp.json`).

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase1-core-split-ready`. Never touch `E:\Projects\CSUploader\CSUploader` (the maintainer's tree, has uncommitted Buzzheavier work).
- **Suite gate after every task** (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — 1162 green at phase start; Task 0 raises the count (record the new baseline and carry it forward; the count only goes up, never down).
  - From Task 3 on, ALSO `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests`.
  - The two test projects use **separate OutDirs** — a shared OutDir would mix WPF and Avalonia assemblies in one folder and break discovery. Do not run bare solution-level `dotnet test -p:OutDir=…` once the second test project exists.
- Every new csproj: `LangVersion=preview` (CommunityToolkit.Mvvm source-gen breaks on .NET 10 defaults), `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`.
- **Version pins are hard**: Avalonia 11.3.18 (matches AvaDevBridge's `AvaloniaVersion` pin in `E:/Projects/avalonia-agent-mcp/Directory.Build.props`), DataGrid 11.3.13 (end of its 11.x line; its dependency range accepts the newer core). Do not "helpfully" bump either.
- The WPF head is **untouched** this phase except the one `DefaultItemExcludes` addition in Task 1 (and the mirror edit in the tests csproj in Task 3). The WPF app must keep building and behaving identically; the full existing suite is the regression net.
- No feature work, no view ports, no new i18n keys (skeleton tab headers are temporary hardcoded English with tracking comments — real headers arrive with the Avalonia LocExtension in Phase 3). Never hand-edit `Strings*.resx`.
- **Agent-safety operating rules** (design §The Avalonia head): the agent never drives the wizard through a final start action; dev DBs are per-bin scratch by construction (DB lives beside the exe — every fresh OutDir is a fresh DB); never copy the real `CSUploader.db` into a bridge-enabled build; the bridge and `ava-drive` exclude each other (single-driver lock) — close one before using the other.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited file:line and copy the semantics exactly — where this plan could not verify an Avalonia API name against the installed package, the step says so explicitly and names the file to check; resolve it at implementation time, don't guess.

---

### Task 0: Test-harness prep (deferred from the Phase 1 gate)

**Files:**
- Create: `tests/ViewModels/InlineUiDispatcher.cs`, `tests/ViewModels/UploadsViewModelPackageEventTests.cs`, `src/CSUploader.Core/Services/FileDialogFilterParser.cs`, `tests/Services/FileDialogFilterParserTests.cs`
- Modify: `tests/ViewModels/ConnectionManagerViewModelTests.cs` (:61, :80, :268, :295, :311, :415, :435, :455), `tests/ViewModels/UploadedViewModelTests.cs` (:255), `tests/ViewModels/UploadsViewModelFilterTests.cs` (:129), `tests/ViewModels/UploadsViewModelRemoveTests.cs` (:102), `tests/ViewModels/MainViewModelUpdateTests.cs` (:62), `tests/CLAUDE.md` (§ViewModel tests, the `WpfUiDispatcher` paragraph at :48)

**Interfaces:**
- Produces (tests-side, full code):

```csharp
namespace CSUploader.Tests.ViewModels;

/// <summary>
/// Deterministic IUiDispatcher for ViewModel tests: Post and InvokeAsync run INLINE
/// (unlike WpfUiDispatcher, whose Post is a no-op without an Application), and timers
/// are manually tickable. This makes the VMs' Post-routed event handlers — the exact
/// path the Avalonia head will drive through a real dispatcher — testable.
/// </summary>
public sealed class InlineUiDispatcher : CSUploader.Services.IUiDispatcher
{
    public List<TestTimer> Timers { get; } = [];

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public CSUploader.Services.IUiTimer CreateTimer(TimeSpan interval, Action onTick)
    {
        TestTimer timer = new(onTick);
        Timers.Add(timer);
        return timer;
    }

    public sealed class TestTimer(Action onTick) : CSUploader.Services.IUiTimer
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        /// <summary>Fires the tick callback if the timer is running.</summary>
        public void Tick()
        {
            if (IsRunning)
            {
                onTick();
            }
        }

        public void Dispose() => IsRunning = false;
    }
}
```

- Produces (Core — the Win32 filter-string parser the Avalonia pickers consume in Phase 4; `IDialogService.BrowseFilesAsync`'s doc contract at `src/CSUploader.Core/Services/IDialogService.cs:39-43` says "implementations on non-Win32 dialog stacks must parse it"):

```csharp
namespace CSUploader.Services;

/// <summary>
/// Parses Win32 file-dialog filter strings ("Name (*.ext)|*.ext|All files (*.*)|*.*")
/// into name/pattern groups, for dialog stacks (Avalonia StorageProvider) that don't
/// speak the Win32 syntax natively. Lenient: null/empty input yields an empty list and
/// a malformed trailing name-without-patterns segment is dropped — a bad localized
/// filter string must degrade to "no filter", never crash a file dialog.
/// </summary>
public static class FileDialogFilterParser
{
    public readonly record struct FilterEntry(string Name, string[] Patterns);

    public static IReadOnlyList<FilterEntry> Parse(string? filter) { /* segments split on '|', pairwise (name, patterns); patterns split on ';', trimmed, empties dropped */ }
}
```

- [ ] **Step 1: Add `InlineUiDispatcher` and swap the VM tests' `WpfUiDispatcher` for it** at exactly the 12 listed sites — 11 are `new WpfUiDispatcher()` construction swaps (ConnectionManagerViewModelTests ×8, UploadedViewModelTests ×1, UploadsViewModelFilterTests ×1, UploadsViewModelRemoveTests ×1) and 1 is a DI registration swap (`MainViewModelUpdateTests.cs:62` becomes `sc.AddSingleton<IUiDispatcher, InlineUiDispatcher>()`). Do **NOT** touch `tests/StartupDISmokeTests.cs:143` — the Core-graph smoke deliberately keeps the real (inert) `WpfUiDispatcher`; its comment explains why. CAUTION, audited behavior change: `WpfUiDispatcher.Post` is a **no-op** in headless tests and `UploadsViewModelRemoveTests` relies on that (it mirrors grid state manually). With inline Post, the VMs' `PackageManager_*` handlers now actually run — remove manual mirroring that the handlers now perform for real, and strengthen assertions where the previously-dead path now executes (`Packages.Contains` guard at `UploadsViewModel.cs:783` already prevents double-adds). Run the affected test classes after each file's swap, then the full suite.
- [ ] **Step 2: New Post-routed-handler tests** in `tests/ViewModels/UploadsViewModelPackageEventTests.cs`, using a real `PackageManager` + real `UploadScheduler` + in-memory repos per tests/CLAUDE.md conventions (mirror the harness in `UploadsViewModelRemoveTests.CreateVmShowing:97` — now at the swapped line):
  - `PackageAdded_BuildsVisibleRow` — drive `PackageManager` so it raises `PackageAdded` (`PackageManager.cs:380` or via scheduler at `:67`); assert the package row lands in `vm.VisibleRows` synchronously (inline Post).
  - `FileCompleted_ImmediatelyMode_PrunesRowAndEmptyPackage` — `settings.RemoveFinishedUploads = Immediately`; drive a file to `Completed` through the real completion path and assert `UploadsViewModel.PackageManager_FileCompleted` (`UploadsViewModel.cs:733`) pruned it. **Reality check first**: find the narrowest existing trigger for a Completed transition with `grep -rn "FileCompleted" tests/` and mirror how `tests/Upload/PackageManager*` tests drive completions; only if no test-drivable path exists, add an `internal` drain/raise hook on `PackageManager` following the `DrainPendingPersistenceAsync` precedent (tests/CLAUDE.md §Structure).
  - `PackageCompleted_WhenPackageIsReadyMode_RemovesPackage` — same approach against `UploadsViewModel.cs:747`.
  - `FilterTextChange_RaisesFilterInvalidated` — set `vm.FilterText`; assert `FilterInvalidated` fired (the `OnFilterTextChanged` path, `UploadsViewModel.cs:160`).
  - `IsExpandedToggle_AddsAndRemovesFileRows` — toggle `package.IsExpanded` both ways; the real route is `Package_PropertyChanged` (`UploadsViewModel.cs:794-810`) → Post → `InsertPackageFiles` (`:802`) / `RemovePackageFiles` (`:806`), which raises NO `FilterInvalidated`; assert file rows appear/disappear in `VisibleRows` (inline Post makes it synchronous). NOTE: `RebuildVisibleRows` (`:280-297`) is NOT this route — it is dead code (Step 2b).
- [ ] **Step 2b (optional cleanup): delete dead `RebuildVisibleRows`.** `UploadsViewModel.RebuildVisibleRows` (`:280-297`, including its `FilterInvalidated` raise at `:296`) has ZERO callers. Re-verify with `grep -rn "RebuildVisibleRows" src/` (expect only the definition), then delete the method. It was equally dead pre-migration — carried over from master's `FilteredRows.Refresh` version — so removing it is cleanup, not a behavior change.
- [ ] **Step 3: `FileDialogFilterParser` + unit tests.** Test cases pinned to the repo's real filter strings: `"JSON files (*.json)|*.json|All files (*.*)|*.*"` (`UploadedViewModel.cs:281`), `"Proxy lists (*.txt)|*.txt|All files (*.*)|*.*"` (the `Settings_Conn_ImportProxies_FileFilter` value, `docs/i18n-inventory.md:406`), `null` → empty, multi-pattern `"Images|*.png;*.jpg"`, trailing odd segment dropped, whitespace trimmed.
- [ ] **Step 4: Update `tests/CLAUDE.md`** §ViewModel tests: VM tests construct `new InlineUiDispatcher()` (deterministic inline marshal + tickable timers); `WpfUiDispatcher` remains only in the head-graph smoke test. Keep the "not `Mock.Of<IUiDispatcher>()`" NRE warning.
- [ ] **Step 5: Full suite** — all green; **record the new test count** (this becomes the baseline every later task carries).
- [ ] **Step 6: Commit** — `"test: inline UI dispatcher for VM tests + Post-routed handler coverage + Win32 filter parser"`

---

### Task 1: `CSUploader.Avalonia` project + shell skeleton

**Files:**
- Create: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`, `src/CSUploader.Avalonia/Program.cs`, `src/CSUploader.Avalonia/App.axaml`, `src/CSUploader.Avalonia/App.axaml.cs`, `src/CSUploader.Avalonia/Views/MainWindow.axaml`, `src/CSUploader.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `src/CSUploader.csproj` (DefaultItemExcludes), `CSUploader.sln`

**Interfaces:**
- Produces: a second executable head (`AssemblyName=CSUploader.Avalonia` until the Phase 9 cutover takes `CSUploader`), launching to an empty 4-tab window. No DI yet (Task 2).

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <RootNamespace>CSUploader</RootNamespace>
    <AssemblyName>CSUploader.Avalonia</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <LangVersion>preview</LangVersion>
    <!-- WebView2's managed API marshals COM; required for the Phase 2 spike + Phase 8 login host. -->
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <!-- Parity-first migration: the WPF XAML being ported uses reflection bindings throughout.
         Compiled bindings (x:DataType everywhere) are a deliberate opt-in AFTER the port. -->
    <AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>
    <ApplicationIcon>..\Properties\Images\Logo\icon.ico</ApplicationIcon>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.18" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.18" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.18" />
    <!-- DataGrid's 11.x line ends at 11.3.13; its dependency range accepts the 11.3.18 core.
         Pinned NOW so the resolution graph is locked before any grid work (design §Hard constraints). -->
    <PackageReference Include="Avalonia.Controls.DataGrid" Version="11.3.13" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.18" Condition="'$(Configuration)' == 'Debug'" />
    <!-- Reserved for head-local MVVM helpers as views arrive; if still unused at the Phase 9
         cutover, drop it (same rule that removed it from the WPF head in c4e9b82). -->
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\CSUploader.Core\CSUploader.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Reuse the WPF head's icon; no duplication. If the Link'd avares path does not resolve
         at runtime (verify with AssetLoader in Step 5), copy the .ico to Assets\ instead. -->
    <AvaloniaResource Include="..\Properties\Images\Logo\icon.ico" Link="Assets\icon.ico" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="CSUploader.Avalonia.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Exclude the nested project from the WPF head's globs.** `src/CSUploader.Avalonia/` sits under the WPF project's directory, exactly like Core — extend `src/CSUploader.csproj:17`:

```xml
<DefaultItemExcludes>$(DefaultItemExcludes);CSUploader.Core\**;CSUploader.Avalonia\**</DefaultItemExcludes>
```

- [ ] **Step 3: Program.cs** — Velopack first line, before AppBuilder (parity with `src/App.xaml.cs:26`; `VelopackApp` flows transitively from Core's Velopack 1.2.0 reference):

```csharp
using Avalonia;
using Velopack;

namespace CSUploader;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack first-frame hook: handles --veloapp-install / --veloapp-uninstall
        // command-line flags that the installer fires. Must run before anything else.
        VelopackApp.Build().Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
```

- [ ] **Step 4: App.axaml / App.axaml.cs / MainWindow.** FluentTheme base ONLY (token/theme port is Phase 3); `ShutdownMode.OnMainWindowClose`; the DataGrid Fluent style include lands now to prove the 11.3.13 pin resolves:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="CSUploader.App"
             RequestedThemeVariant="Light">
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />
  </Application.Styles>
</Application>
```

```csharp
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

MainWindow.axaml: `Title="CSUploader"`, `Width="1024" Height="800"`, `WindowStartupLocation="CenterScreen"`, `Icon="avares://CSUploader.Avalonia/Assets/icon.ico"` (mirrors `src/Views/MainWindow.xaml:13-16`), containing a bare `TabControl` with four `TabItem`s headered `Uploads` / `Uploaded` / `Settings` / `Logs` in that order (mirrors `MainWindow.xaml:48-61`), each with an empty content `Border` and a `<!-- TODO(phase3): loc:Loc headers; TODO(phase5/6): real views -->` comment.

- [ ] **Step 5: Wire + verify.** `dotnet sln add src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`. Build and launch: `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava` then run `D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe` — a 1024×800 window with 4 tabs and the app icon appears; close it. Full suite gate (proves the WPF head still builds cleanly with the new exclude).
- [ ] **Step 6: Commit** — `"build: add CSUploader.Avalonia head skeleton (Fluent, 4-tab shell, Velopack hook)"`

---

### Task 2: Avalonia implementations of the head-supplied interfaces + DI composition

**Files:**
- Create under `src/CSUploader.Avalonia/Services/`: `AvaloniaUiDispatcher.cs`, `AvaloniaClipboardService.cs`, `AvaloniaFontEnumerationService.cs`, `AvaloniaThemeApplier.cs`, `AvaloniaTrayIconService.cs`, `AvaloniaDialogService.cs`, `AvaloniaUpdateProgressSink.cs`, `StubInteractiveAuthService.cs`, `NoOpToastNotificationService.cs`
- Modify: `src/CSUploader.Avalonia/App.axaml.cs`, `src/CSUploader.Avalonia/Views/MainWindow.axaml(.cs)`

**Interfaces:**
- Consumes: the seven head-supplied interfaces named in `src/CSUploader.Core/ServiceRegistration.cs:180-183` — `IDialogService`, `IUiDispatcher`, `IClipboardService`, `IThemeApplier`, `ITrayIconService`, `IFontEnumerationService`, `IUpdateProgressSink` — plus the two additional head registrations the Core graph needs: `IInteractiveAuthService` (feeds the captcha-gated pipelines) and `IToastNotificationService` (feeds `UploadNotificationListener`). The definitive list is what `tests/StartupDISmokeTests.cs:138-148` mocks.
- Produces: `internal static App.ConfigureServices(IServiceCollection, string baseDirectory)` mirroring `src/App.xaml.cs:58-91`:

```csharp
internal static void ConfigureServices(IServiceCollection services, string baseDirectory)
{
    services.AddCoreServices(baseDirectory);

    // UI services (Avalonia implementations of Core interfaces)
    services.AddSingleton<IDialogService, AvaloniaDialogService>();            // throws per member until Phase 4
    services.AddSingleton<IUpdateProgressSink, AvaloniaUpdateProgressSink>();  // no-op until Phase 4
    services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
    services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
    services.AddSingleton<IFontEnumerationService, AvaloniaFontEnumerationService>();
    services.AddSingleton<IThemeApplier, AvaloniaThemeApplier>();              // no-op until Phase 3
    services.AddSingleton<IInteractiveAuthService, StubInteractiveAuthService>(); // throws until Phase 8
    services.AddSingleton<AvaloniaTrayIconService>();
    services.AddSingleton<ITrayIconService>(sp => sp.GetRequiredService<AvaloniaTrayIconService>());
    services.AddSingleton<IToastNotificationService, NoOpToastNotificationService>(); // real toasts in Phase 7
}
```

**Implementation levels (per the design + Phase 2 scope):**

1. **`AvaloniaUiDispatcher` — REAL, contract-critical.** Must honor the asymmetric exception routing documented on `src/CSUploader.Core/Services/IUiDispatcher.cs:22-27` (design §Phase 1 purification item 3): inline execution throws to the awaiter; **marshaled execution routes exceptions to the framework's unhandled path and the returned Task must NOT fault**. Avalonia's own `Dispatcher.UIThread.InvokeAsync` faults the task — do not use it for the marshaled path. Avalonia's "framework unhandled path" is the **`Dispatcher.UIThread.UnhandledException` event** (there is NO WPF-style `Application.DispatcherUnhandledException` in Avalonia) — the App wires it in Step 2; without that wiring, a rethrown marshaled exception would crash the process instead of routing.

```csharp
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    // Test seam: routes marshaled exceptions somewhere observable instead of rethrowing.
    internal Action<Exception>? MarshaledExceptionSink { get; init; }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action(); // inline path throws to the awaiter (contract)
            return Task.CompletedTask;
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (MarshaledExceptionSink is not null)
            {
                MarshaledExceptionSink(ex);
            }
            // Deliberately NO general catch: a marshaled exception propagates into the
            // dispatcher loop and surfaces on Dispatcher.UIThread.UnhandledException,
            // which App wires to log + mark Handled (the contract's "framework unhandled
            // path"). The finally still completes the Task — it must NOT fault (contract).
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick) { /* Avalonia DispatcherTimer, created STOPPED; Start/Stop/Dispose wrapper mirroring WpfUiDispatcher.WpfUiTimer */ }
}
```

   Notes for the implementer: `WpfUiDispatcher`'s "no Application → Post no-op / inert timer" tolerance exists for headless WPF unit tests; Avalonia-head tests always run under a headless session with a live dispatcher, so no equivalent guard is needed — but verify Avalonia's `DispatcherTimer` can be constructed off the UI thread (VM ctors call `CreateTimer`); if it can't, marshal the construction.

2. **`AvaloniaClipboardService` — REAL.** Owner contract from `IDialogService`'s doc (design §Phase 1 item 5) applies to clipboard access too: resolve the clipboard from the currently-active window **at call time** (`ApplicationLifetime as IClassicDesktopStyleApplicationLifetime` → `Windows.FirstOrDefault(w => w.IsActive) ?? MainWindow` → `.Clipboard`). `SetTextAsync`/`ClearAsync` forward; return `Task.CompletedTask` when no window is available.
3. **`AvaloniaFontEnumerationService` — REAL.** Project Avalonia's `FontManager.Current.SystemFonts` to names, de-duplicated and sorted (contract on `IFontEnumerationService.cs:15-16`; verify the exact member name against Avalonia 11.3.18 when implementing).
4. **`AvaloniaTrayIconService` — REAL (Avalonia built-in `TrayIcon`).** Mirror `src/Services/TrayIconManager.cs` member-for-member — read it first: `UpdateVisibility()` reads `AppSettings` (`MinimizeToTray` / `CloseAction`) and creates/destroys the icon to match; `ShowMainWindow()` restores + activates `desktop.MainWindow` (mirror TrayIconManager's exact restore sequence); icon from `avares://CSUploader.Avalonia/Assets/icon.ico`; clicked event → `ShowMainWindow`. `NotifyHidden()` is a **no-op with a tracking comment** — Avalonia has no balloon API; Phase 7 routes it through the app's own toast system (design §The Avalonia head). Implements `IDisposable` (dispose the `TrayIcon`).
5. **`AvaloniaThemeApplier` — no-op both members**, tracking comments pointing at Phase 3 (theme dictionaries) — it is the designated sole writer of the new-window dark-chrome preference per the design's Phase 7 note, which lands then.
6. **`AvaloniaUpdateProgressSink` — no-op all four members** (`Open/SetStatus/Report/Close`), tracking comment pointing at Phase 4 (UpdateProgress window).
7. **`AvaloniaDialogService` — throws.** Implements `IDialogService` directly (NOT `DialogServiceBase` yet — Phase 4 rewrites it on the base); every member `throw new NotImplementedException("Avalonia dialogs arrive in Phase 4 — <member> tracked there.");`.
8. **`StubInteractiveAuthService` — throws.** Implement every `IInteractiveAuthService` member (read `src/CSUploader.Core/Services/IInteractiveAuthService.cs` for the exact surface) with `NotSupportedException("Interactive sign-in arrives with the Phase 8 WebView2 port.")`.
9. **`NoOpToastNotificationService` — no-op.** Implement every `IToastNotificationService` member (read `src/CSUploader.Core/Services/IToastNotificationService.cs`) as no-ops, tracking comment pointing at Phase 7. This replaces the WPF head's `IToastWindowFactory` + factory-lambda registration (`App.xaml.cs:77-87`) for now — do not port that lambda yet.

- [ ] **Step 1:** Implement the nine services at the levels above.
- [ ] **Step 2:** App composition — extend `App.OnFrameworkInitializationCompleted` to mirror `src/App.xaml.cs:22-47`: build `ServiceCollection` → `ConfigureServices(services, AppDomain.CurrentDomain.BaseDirectory)` → `BuildServiceProvider()` → `ServiceRegistration.WireRuntime(provider)` → `desktop.MainWindow = new MainWindow { DataContext = provider.GetRequiredService<MainViewModel>() }` → dispose the provider on `desktop.Exit` (parity with `App.xaml.cs:49-53`). Bind MainWindow's `Title="{Binding WindowTitle}"` and the TabControl's `SelectedIndex="{Binding SelectedTabIndex}"` (first live bindings; both resolve on `MainViewModel` — see `src/Views/MainWindow.xaml:13,48`).
  **REQUIRED in the same step — global UI exception handler**: right after the provider is built, wire

```csharp
Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    _serviceProvider!.GetRequiredService<IAppLogger>().Log(this, LogType.Error,
        $"Unhandled exception on the UI thread: {e.Exception}");
    e.Handled = true; // keep a marshaled VM exception from killing the process
};
```

  (args type `DispatcherUnhandledExceptionEventArgs`; match `IAppLogger.Log`'s real signature at `src/CSUploader.Core/Lib/IAppLogger.cs:15` and `LogType`'s real member names). This is what makes `AvaloniaUiDispatcher`'s marshaled-path propagation "route to the framework's unhandled path" instead of crashing, and it fulfills the design's global unhandled-exception-logging item (§The Avalonia head: "parity: none today — keep parity, just log").
- [ ] **Step 3:** **Startup init**: read how the WPF head triggers `MainViewModel.InitializeAsync` (find the call in `src/Views/MainWindow.xaml.cs`) and mirror it on the Avalonia `Window.Opened` event with the same awaited/fire-and-forget shape. This runs DB init, settings/proxies/packages/uploaded hydration (`MainViewModel.cs:163-260`) — required for Task 5's guard to mean anything.
- [ ] **Step 4:** Launch from `D:\temp2\cbuild-mig\ava` (fresh scratch DB beside the exe by construction). Window title switches to the VM-provided `WindowTitle`; tabs switch; no crash from stub services during idle startup (the update check's failure path must log, not throw — it is try/caught per `MainViewModel.cs:83-85`).
- [ ] **Step 5:** Full suite gate.
- [ ] **Step 6: Commit** — `"feat(avalonia): head DI composition — real dispatcher/clipboard/fonts/tray, staged stubs for dialogs/auth/toasts"`

---

### Task 3: Avalonia head smoke tests (`tests/CSUploader.Avalonia.Tests`)

**Files:**
- Create: `tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj`, `tests/CSUploader.Avalonia.Tests/TestAppBuilder.cs`, `tests/CSUploader.Avalonia.Tests/AvaloniaStartupDISmokeTests.cs`, `tests/CSUploader.Avalonia.Tests/AvaloniaUiDispatcherTests.cs`
- Modify: `tests/CSUploader.Tests.csproj` (DefaultItemExcludes), `CSUploader.sln`

- [ ] **Step 1: csproj** — TFM/LangVersion per Global Constraints; packages: `Avalonia.Headless.XUnit` **11.3.18**, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5 (PrivateAssets pattern as in `tests/CSUploader.Tests.csproj:23-26`), `Microsoft.NET.Test.Sdk` 18.7.0, `Moq` 4.20.72, `coverlet.collector` 10.0.1; `<Using Include="Xunit" />`; ProjectReference `..\..\src\CSUploader.Avalonia\CSUploader.Avalonia.csproj`. **No `UseWPF`.**
  **FIRST ACTION after writing the csproj — restore-verify**: `dotnet restore tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj` must resolve cleanly. The tooling repo's SPIKE-FINDINGS §2 documents a Headless-vs-DataGrid dependency clash — in that repo it was driven by Central Package Management + `CentralPackageTransitivePinningEnabled`. CSUploader likely dodges it: this repo does NOT use CPM, and DataGrid 11.3.13 (arriving transitively through the head ProjectReference) declares an open `>= 11.3.13` Avalonia range that 11.3.18 satisfies — but verify, don't assume. If restore DOES clash: fall back to the out-of-process render-to-bitmap harness described in SPIKE-FINDINGS (headless test project drops the DataGrid-bearing reference; DataGrid coverage moves to bridge-driven checks).
- [ ] **Step 2: Prevent glob bleed** — the new project nests under `tests/`, whose existing csproj globs `**/*.cs`. Add to `tests/CSUploader.Tests.csproj` (same fix as `src/CSUploader.csproj:17`):

```xml
<DefaultItemExcludes>$(DefaultItemExcludes);CSUploader.Avalonia.Tests\**</DefaultItemExcludes>
```

- [ ] **Step 3: Headless bootstrap** — `TestAppBuilder.cs`:

```csharp
[assembly: AvaloniaTestApplication(typeof(CSUploader.Tests.Avalonia.TestAppBuilder))]

namespace CSUploader.Tests.Avalonia;

/// <summary>Headless Avalonia session for the test assembly. Uses a bare Application —
/// the DI smoke composes the real App.ConfigureServices graph directly, so the real App's
/// desktop-lifetime startup never runs under test.</summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class TestApp : Application
{
}
```

- [ ] **Step 4: DI smoke** (`[AvaloniaFact]` — VM ctors create dispatcher timers, so the headless dispatcher must exist), mirroring BOTH WPF smokes in `tests/StartupDISmokeTests.cs`: Velopack locator static-init (copy the `_velopackInit` pattern, `:46-51`); temp base directory; `App.ConfigureServices(services, tempDir)` (internal → `InternalsVisibleTo` from Task 1); build provider **and resolve everything INLINE on the test's UI thread** — `[AvaloniaFact]` runs the test body on the headless UI thread, which is exactly where `CreateTimer` works; do NOT copy the WPF smoke's `Task.Run` + timeout watchdog wrapper (off-thread resolution would break the timer construction the smoke exists to exercise); assert:
  - each of the seven head-supplied interfaces AND `IInteractiveAuthService` AND `IToastNotificationService` resolves (`GetRequiredService` per interface — this is the "all head registrations present" gate);
  - `PackageManager`, `UploadScheduler`, `AttemptRunner` resolve and `GetServices<IFileHosterPipeline>()` is non-empty;
  - all six VMs resolve (`MainViewModel`, `UploadsViewModel`, `UploadedViewModel`, `SettingsViewModel`, `ConnectionManagerViewModel`, `LogsViewModel`);
  - `ServiceRegistration.WireRuntime(provider)` does not throw.
- [ ] **Step 5: Dispatcher contract tests** (`[AvaloniaFact]`), pinning the `IUiDispatcher.cs:22-27` contract: (a) inline path (already on UI thread) — a throwing action throws to the caller synchronously; (b) marshaled path — from `Task.Run`, `InvokeAsync` with a throwing action + injected `MarshaledExceptionSink`: returned Task completes **non-faulted** and the sink observed the exception (pump with `Dispatcher.UIThread.RunJobs()` if awaiting cross-thread needs it — resolve mechanics at implementation time); (c) `CreateTimer` returns a stopped timer that only ticks after `Start()`.
- [ ] **Step 6:** `dotnet sln add tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj`. Run BOTH suite commands (Global Constraints) — all green; record the Avalonia-suite count.
- [ ] **Step 7: Commit** — `"test(avalonia): headless DI smoke (all head interfaces + six VMs) and IUiDispatcher contract tests"`

---

### Task 4: AvaDevBridge wiring (CI-safe) + redactor

**Files:**
- Create: `Directory.Build.local.props` (repo root, **untracked**), `Directory.Build.local.props.sample` (committed), `src/CSUploader.Avalonia/Diagnostics/BridgeRedactor.cs`
- Modify: `.gitignore`, `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`, `src/CSUploader.Avalonia/App.axaml.cs`

- [ ] **Step 1: Local props + sample + ignore.** MSBuild auto-imports only `Directory.Build.props`, so the csproj imports the local file explicitly (Step 2). Content of BOTH files (sample carries a comment header explaining copy-to-activate):

```xml
<Project>
  <PropertyGroup>
    <AvaDevBridgeCsproj>E:\Projects\avalonia-agent-mcp\AvaDevBridge\AvaDevBridge.csproj</AvaDevBridgeCsproj>
  </PropertyGroup>
</Project>
```

`.gitignore` gains (next to the existing Claude Code entry at `.gitignore:400`):

```
# Dev-only AvaDevBridge location — machine-specific; copy Directory.Build.local.props.sample to enable
/Directory.Build.local.props
```

- [ ] **Step 2: Guarded reference + define** in `CSUploader.Avalonia.csproj` (import at the top of the file, before the property groups):

```xml
<Import Project="$(MSBuildThisFileDirectory)..\..\Directory.Build.local.props"
        Condition="Exists('$(MSBuildThisFileDirectory)..\..\Directory.Build.local.props')" />

<PropertyGroup Condition="'$(Configuration)' == 'Debug' and '$(AvaDevBridgeCsproj)' != '' and Exists('$(AvaDevBridgeCsproj)')">
  <AvaBridgeEnabled>true</AvaBridgeEnabled>
  <DefineConstants>$(DefineConstants);AVA_BRIDGE</DefineConstants>
</PropertyGroup>

<ItemGroup Condition="'$(AvaBridgeEnabled)' == 'true'">
  <ProjectReference Include="$(AvaDevBridgeCsproj)" />
</ItemGroup>
```

(AvaDevBridge targets `net10.0` with Avalonia pinned at 11.3.18 in its own `Directory.Build.props` — TFM- and version-compatible with this head by construction.)

- [ ] **Step 3: Attach + redactor.** In `App.OnFrameworkInitializationCompleted`, after `desktop.MainWindow` is assigned:

```csharp
#if AVA_BRIDGE
        this.AttachAgentBridge(o =>
        {
            o.EnableMutations = true;
            o.Redactor = new Diagnostics.BridgeRedactor();
        });
#endif
```

`BridgeRedactor.cs` is wrapped entirely in `#if AVA_BRIDGE` (it references a bridge type). `ISensitiveDataRedactor` has TWO members: `bool ShouldHide(string ownerType, string name)` and `string Mask(string value)`. The bridge's built-in secret regex already hides `password|token|secret|apikey`-shaped names on its own, so `ShouldHide` only needs to ADD the CSUploader-specific shapes: return true when `name` contains `cookie` or `userhash` (case-insensitive); `Mask` returns `"«redacted»"`. This is the design's belt-and-braces agent-safety item.
- [ ] **Step 4: CI-safety proof** (design gate: bare restore succeeds without the tooling repo; mirrors `release.yml:43-44`):
  1. `Rename-Item Directory.Build.local.props Directory.Build.local.props.off`
  2. `dotnet restore` → succeeds. `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Release -p:OutDir=D:\temp2\cbuild-mig\ava-rel` → succeeds; `Get-ChildItem D:\temp2\cbuild-mig\ava-rel -Filter AvaDev*` → empty.
  3. `Rename-Item Directory.Build.local.props.off Directory.Build.local.props`
  4. Debug build with the props present → `AvaDevBridge.dll` in `D:\temp2\cbuild-mig\ava`; launch the exe → a handshake file appears under `%LOCALAPPDATA%\ava-agent-bridge\<pid>.json` within ~2 s; close the app.
  (If the tooling repo's `TreatWarningsAsErrors` breaks the Debug build on a bridge warning, fix forward in the tooling repo — that repo is ours — and note it; the Release/CI path is immune by construction.)
- [ ] **Step 5:** Full suite gate (both projects). **Commit** — `"build(avalonia): Debug-only AvaDevBridge wiring behind Directory.Build.local.props (CI-safe) + sensitive-data redactor"`

---

### Task 5: `--agent` startup guard

**Files:**
- Modify: `src/CSUploader.Core/Upload/Settings.cs`, `src/CSUploader.Avalonia/App.axaml.cs`
- Create/extend tests: the existing autostart tests (locate via `grep -rn "RegisteredPackageCount" tests/` — `UploadScheduler.cs:82-85` says they exist) + an `AppSettings` latch test (add to the existing AppSettings test file if `Glob tests/**/*Settings*Tests.cs` finds one, else create `tests/Upload/AppSettingsTests.cs`)

**Why a latch, not an assignment (the seam, verified):** `MainViewModel.InitializeAsync` runs `SettingsViewModel.LoadAsync()` (`MainViewModel.cs:250`), which writes the persisted value back into `AppSettings.AutostartUploads` **before** `PackageManager.LoadPersistedPackagesAsync()` (`MainViewModel.cs:255`) reads it (`PackageManager.cs:394-400`). The write-back is NOT the `OnAutostartUploadsChanged` partial (`SettingsViewModel.cs:494-499`) — during load that path is suppressed by `_suppressAutoSave` (`:213`/`:496`) — it is the **unconditional VM→settings copy block at the end of the load** (`SettingsViewModel.cs:369-388`, specifically `:377 _settings.AutostartUploads = AutostartUploads;`). Either way, a plain startup-time `settings.AutostartUploads = Never` would be silently overwritten; the latch wins over any later write while leaving the setter (and therefore DB persistence) untouched. The latch is also invisible in the Settings UI: the page binds `SettingsViewModel`'s own `[ObservableProperty] AutostartUploads` (backing field `SettingsViewModel.cs:95`; `SettingsView.xaml:458`), which is hydrated from the DB and never reads `AppSettings` back — so an `--agent` session still displays the user's persisted choice, not "Never".

- [ ] **Step 1: Core latch.** In `Settings.cs`, convert the auto-property at `:112` to the file's existing backing-field pattern (`uploadsTabPageRefreshTimer` at `:55-64`):

```csharp
private AutostartUploadsMode? autostartUploads;
private bool forceAutostartNever;

public AutostartUploadsMode AutostartUploads
{
    get => forceAutostartNever ? AutostartUploadsMode.Never : autostartUploads ?? DefaultAutostartUploads;
    set => autostartUploads = value;
}

/// <summary>
/// One-way latch for agent-driven dev sessions (the Avalonia head's --agent switch):
/// after this call the getter reports Never regardless of later writes, so the
/// settings-load during MainViewModel.InitializeAsync cannot re-enable autostart before
/// LoadPersistedPackagesAsync honours it. The setter still records the user's value —
/// the Settings UI and DB persistence are unaffected.
/// </summary>
public void ForceAutostartUploadsNever() => forceAutostartNever = true;
```

- [ ] **Step 2: Head switch.** In `App.OnFrameworkInitializationCompleted`, immediately after `WireRuntime` and before `MainWindow` is constructed:

```csharp
if (desktop.Args?.Contains("--agent", StringComparer.Ordinal) == true)
{
    AppSettings settings = _serviceProvider.GetRequiredService<AppSettings>();
    settings.ForceAutostartUploadsNever();
    _serviceProvider.GetRequiredService<UploadScheduler>().PauseAll();
    _serviceProvider.GetRequiredService<IAppLogger>().Log(this, LogType.Info,
        "--agent: AutostartUploads forced to Never; scheduler started paused.");
}
```

`PauseAll()` (`UploadScheduler.cs:223-230`) is the existing clean pause API — no new scheduler surface needed. It posts `IsPaused = true` onto the scheduler loop, and channel FIFO ordering puts it ahead of any later scheduling posts. **Verify one assumption while implementing**: `UploadScheduler.Post` must enqueue even before `Start()` has run (the loop is started by `PackageManager`'s ctor at `PackageManager.cs:70`, which DI may resolve after this call) — read `Post`/`ProcessLoopAsync` in `UploadScheduler.cs`; if pre-start posts were dropped (unexpected), resolve `PackageManager` first and then call `PauseAll()`. Match `LogType`'s real member name.
- [ ] **Step 3: Tests.** (a) Latch: set `AutostartUploads = Always`, call `ForceAutostartUploadsNever()`, set `Always` again → getter reports `Never`. (b) End-to-end policy: extend the existing autostart-mode tests — persisted package with `wasRunningAtShutdown` shape + `Always` mode + latched settings → `LoadPersistedPackagesAsync` does NOT register it with the scheduler (`RegisteredPackageCount` stays 0). Belt-and-braces rationale in a test comment: latching alone stops queuing; `PauseAll` alone would leave files queued and one `FillAvailableSlots`/`StartAll` (`UploadScheduler.cs:136-164`) away from really uploading.
- [ ] **Step 4:** Full suite gate (both projects). **Commit** — `"feat(avalonia): --agent guard — AutostartUploads latched to Never + scheduler starts paused"`

---

### Task 6: Dev-loop proof (build → launch → drive → screenshot)

No production files change; this task proves and documents the loop the rest of the migration lives on. The committed `.mcp.json` already points at the prebuilt `E:/Projects/avalonia-agent-mcp/AvaDevMcp/bin/Release/net10.0/AvaDevMcp.exe`; this session drives via `scripts/ava-drive.cs` instead (MCP registration needs a session restart). Single-driver lock: never run `ava-drive` while an MCP session is attached, and vice versa.

- [ ] **Step 1: Build + launch** (Debug = bridge on, `--agent` = uploads can't auto-start; scratch DB beside the exe):

```powershell
dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava
# launch detached (Bash tool: run_in_background)
D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe --agent
```

- [ ] **Step 2: Drive** (each command exits after one round-trip; the printed envelope is the evidence):

```powershell
dotnet run scripts/ava-drive.cs -- ava_windows
dotnet run scripts/ava-drive.cs -- ava_tree
dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out D:\temp2\cbuild-mig\shots\phase2-shell.png
```

Pass criteria: `ava_windows` lists the main window (`title: CSUploader…`, `isMain: true`); `ava_tree` shows the `TabControl` with four `TabItem` nodes carrying the computed `text` headers Uploads/Uploaded/Settings/Logs; the PNG (open with Read) shows the Fluent-themed 4-tab shell. Bonus probe that the VM wiring is live: `dotnet run scripts/ava-drive.cs -- ava_vm '{"ref":"<main-window-ref>"}'` returns `MainViewModel` properties (redactor active — no credential-shaped values).
- [ ] **Step 3:** Check `ava_logs` for binding errors (`area:"Binding"` is the highest-value signal): the two live bindings (`WindowTitle`, `SelectedTabIndex`) must produce none. Close the app (window X — `OnMainWindowClose` ends the process; verify it exits).
- [ ] **Step 4:** Fix anything the loop surfaced; suite gate; **Commit** (only if fixes were needed) — `"fix(avalonia): shell issues surfaced by first bridge-driven session"`. Otherwise record the evidence (envelope outputs + screenshot path) in the task notes; no commit.

---

### Task 7: WebView2 GO/NO-GO spike (the Phase 8 gate)

**Files:**
- Create: `src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs`, `src/CSUploader.Avalonia/Spike/WebView2SpikeWindow.axaml(.cs)`, `docs/superpowers/specs/2026-07-11-webview2-spike-verdict.md`
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` (add `Microsoft.Web.WebView2` **1.0.4022.49** — same pin as the WPF head, `src/CSUploader.csproj:37`), `src/CSUploader.Avalonia/App.axaml.cs` (Debug-only trigger)

**THROWAWAY code**: everything under `Spike/` is Debug-gated scaffolding, kept in-tree as the Phase 8 reference and deleted when Phase 8 lands the real host. Mark each file's header `// THROWAWAY — Phase 2 WebView2 spike; superseded by the Phase 8 login host.`

- [ ] **Step 1: Host + window.**
  - `WebView2HwndHost : NativeControlHost` — `CreateNativeControlCore(IPlatformHandle parent)` creates a bare child HWND (`CreateWindowEx`, `"static"` class, `WS_CHILD | WS_VISIBLE`, parented to `parent.Handle`) and returns it; `DestroyNativeControlCore` destroys it.
  - `WebView2SpikeWindow`: URL `TextBox` (default `https://hitfile.net/login`) + Go; the host filling the center; a diagnostics panel of **bridge-readable Avalonia controls** (the native HWND is invisible to bridge screenshots/tree — design §MCP dev loop — so every observation must surface on the Avalonia side): a status `TextBlock` fed by `NavigationStarting`/`NavigationCompleted` + current `Source`; buttons **Probe** (`ExecuteScriptAsync` returning `{tag,id,value}` of `document.activeElement` → output `TextBox` named `ProbeOutput`), **Cookies** (`CookieManager.GetCookiesAsync(currentUrl)` → names + `IsHttpOnly` flags → `ProbeOutput`), **Capture** (`CoreWebView2.CapturePreviewAsync` PNG → `D:\temp2\cbuild-mig\shots\webview-capture.png` → path in `ProbeOutput` — this is the agent's eye inside the WebView).
  - Controller plumbing — what transplants vs what is NEW: the WPF reference (`src/Views/WebViewLoginWindow.xaml.cs:150-206` — read it first) supplies the environment creation (`CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: …, options)` at `:172-175`), the `CoreWebView2EnvironmentOptions` handling, and the cookie/probe logic. But WPF then hands that environment to its WebView **control** via `EnsureCoreWebView2Async(env)` (`:177`) — there is NO controller creation to copy. `env.CreateCoreWebView2ControllerAsync(hwnd)` is NEW code written for this spike (the API is confirmed present on the pinned 1.0.4022.49). **User-data folder for the spike is scratch**: `D:\temp2\cbuild-mig\webview2-udf` (NOT the app's real `%LOCALAPPDATA%\CSUploader\WebView2\…` tree). Bounds sync: on host `BoundsChanged`/layout + window move, set `controller.Bounds` to the host rect converted DIPs→physical via the `TopLevel.RenderScaling`; call `controller.NotifyParentWindowPositionChanged()` on window moves. `OnClosed` → `controller.Close()` (the WPF side's `WebView.Dispose()` lock-release equivalent — design §The Avalonia head adaptation list).
  - Trigger: `#if DEBUG` — when args contain `--webview-spike`, open the spike window via `ShowDialog(mainWindow)` from the **`MainWindow.Opened` hook** (the same hook Task 2 Step 3 uses for `InitializeAsync`), NOT synchronously in `OnFrameworkInitializationCompleted`: `ShowDialog` requires an already-SHOWN owner. Modal-from-birth serves verify point (c). Forwardable through `ava_launch args` and plain command line.
- [ ] **Step 2: Run the five verify points.** Launch: `D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe --agent --webview-spike`. Live sites: `https://hitfile.net/login` (Turnstile-gated; `HitFilePipeline.cs:73`), alternates `https://keep2share.cc/auth/login` (`Keep2SharePipeline.cs:52`), `https://ufile.io/login` (reCAPTCHA; `UfileIoPipeline.cs:56`). Buzzheavier's login is NOT on this branch (uncommitted in the maintainer's tree) — don't reference it. **No real sign-in by the agent, ever; no credentials in any probe output.**

  | # | Verify | PASS means | Agent-verifiable | maintainer-verifiable |
  |---|--------|-----------|------------------|-----------------|
  | a | Keyboard/typing into a real hoster login incl. Turnstile | Clicked field gets focus; typed characters land in the page (Probe shows them in `activeElement.value`; Capture shows them rendered); Tab moves within the page while the WebView has focus, not out to Avalonia controls; Turnstile widget is interactive and solvable | Focus + typed-text half: drive native input with a small PowerShell SendInput helper at screen coordinates (from a bridge screenshot of the window; the WebView region is the "hole"), then Probe + Capture | Turnstile interaction end-to-end ("needs eyeballs" — the challenge is visual and variable), ideally one full real sign-in |
  | b | Bounds sync at 125%/150% DPI + resize | After every resize, `window.innerWidth/innerHeight` (Probe) ≈ host DIP bounds × `RenderScaling` within a couple px; Capture shows content filling the host with no dead zones; corner links hit-test correctly | Resize sweep at current DPI: resize via `ava_set_prop` on the window's Width/Height (or PowerShell), Probe + Capture after each; verify the arithmetic | The 125% and 150% passes (display-scale change or a differently-scaled monitor) + corner-click hit-testing |
  | c | ShowDialog modal ownership | While the spike dialog is open, MainWindow input is blocked (`ava_props` on MainWindow shows `IsEnabled=false`; an `ava_action` invoke on a main-window tab does not take effect); WebView stays interactive inside the modal; closing restores the owner | Fully | Spot-check feel (optional) |
  | d | `Controller.Close()` releases the user-data-folder lock | With the app still running after the spike window closes, `Rename-Item D:\temp2\cbuild-mig\webview2-udf …` (or delete) succeeds within a few seconds | Fully (Bash/PowerShell) | — |
  | e | CookieManager reads | Cookies button returns a non-empty list for the login URL **including at least one `IsHttpOnly` cookie** (Cloudflare/session cookies appear on first load; HttpOnly visibility is what the whole login capture rests on — `WebViewLoginWindow.xaml.cs:369-374,423-428`) | Fully (read `ProbeOutput` via bridge) | — |

- [ ] **Step 3: Record the verdict** in `docs/superpowers/specs/2026-07-11-webview2-spike-verdict.md`: per-point result + evidence (probe outputs, capture/screenshot paths), the GO/NO-GO call, and Phase 8 implications. **Abort criterion (verbatim from the design)**: if any of a–e fails unfixably → fall back to a tiny WPF-hosted login helper process (separate exe, same `IInteractiveAuthService` contract) and re-scope Phase 8 accordingly; the migration continues either way — the gate decides the login architecture, not the project. A NO-GO also updates the design doc's §Phases/§Risks in this commit.
- [ ] **Step 4:** Full suite gate (both projects). **Commit** — `"spike(avalonia): WebView2-in-NativeControlHost GO/NO-GO — verdict recorded"` (code + verdict doc together; maintainer-verifiable rows may land as PENDING with the agent-side evidence complete — flag them in the verdict doc and to the team lead).

---

### Task 8: Phase gate — review, tag, surface to the maintainer

- [ ] **Step 1:** Whole-diff review: `git diff phase1-core-split-ready..HEAD` reviewed by a fresh adversarial reviewer (per-task reviews already happened; the repo's history shows whole-diff reviews catch cross-task issues — e.g. the Order-column DataGrid bug).
- [ ] **Step 2:** Gates: `grep -rn "System.Windows" src/CSUploader.Avalonia/` → zero; `grep -rn "System.Windows" src/CSUploader.Core/` → still zero; both suites green (record final counts: Task 0 baseline + Task 3/5 additions); i18n gate green (runs inside the main suite via `I18nRegenGateTests`); CI-safety re-check — rename `Directory.Build.local.props` away, `dotnet restore` + Release build succeed, rename back.
- [ ] **Step 3:** WPF head unchanged in behavior: launch it once from `D:\temp2\cbuild-mig\wpf`, spot-check a tab + one dialog.
- [ ] **Step 4:** `git tag phase2-shell-and-spike-ready`. Surface to the maintainer: (1) the spike verdict + the two maintainer-verifiable items (Turnstile typing, DPI passes) if still pending — the Phase 8 GO/NO-GO is only final once those pass; (2) the Phase 1 merge-back reminder still stands (design §Merge protocol — merging early keeps hoster work on the shared layout).
- [ ] **Step 5:** Reconcile the design doc with anything Phase 2 taught (spike outcome, bridge wiring reality, dispatcher contract nuances); commit — `"docs: reconcile design with Phase 2 outcomes (spike verdict, bridge wiring)"`.

---

**Plan-wide reality-check register** (things this plan cites but the implementer must read before coding, because the plan could not pin them from the installed bits): Avalonia `FontManager.Current.SystemFonts` member shape (Task 2); Avalonia `DispatcherTimer` off-UI-thread construction (Task 2); the WPF `MainViewModel.InitializeAsync` trigger site in `src/Views/MainWindow.xaml.cs` (Task 2); `TrayIconManager.cs` visibility/restore logic (Task 2); `IInteractiveAuthService`/`IToastNotificationService` full member lists (Task 2); the Headless-vs-DataGrid restore-verify against the tooling repo's SPIKE-FINDINGS §2 (Task 3); `UploadScheduler.Post` pre-`Start` queue semantics (Task 5); the existing autostart-mode test file and the Completed-transition test harness (Tasks 0, 5); linked-`AvaloniaResource` avares path resolution (Task 1).
