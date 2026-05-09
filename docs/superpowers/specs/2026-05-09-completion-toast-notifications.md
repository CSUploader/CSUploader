# Upload Completion Toast Notifications — Design

**Status:** Approved (design phase). Implementation plan to follow under `docs/superpowers/plans/`.

**Date:** 2026-05-09

## Goal

Show a JDownloader2-style toast popup in the bottom-right corner of the primary monitor when an upload finishes. Two flavours:

1. **Per-file toast** — fires the moment a single `PackageFile` transitions to `FileState.Completed`.
2. **Per-package summary toast** — fires when every file in a `Package` has reached a terminal state (`Completed` / `Failed` / `Cancelled`) and at least one of them is `Completed`. The body summarises the run (`"3 of 4 files uploaded"`).

Multiple toasts stack vertically — the first toast occupies the bottom slot, each subsequent toast appears directly above the existing stack — each auto-dismisses after 5 s, and clicking one activates the main window and selects the **Uploaded** tab.

The whole feature is gated by a new persisted setting `AppSettings.ShowCompletionToasts` (default `true`), exposed as a checkbox on the Settings → General page so users can disable it like any other UI option.

## Non-goals

- Per-monitor placement — toasts always appear on the primary monitor's working area.
- Failure-only toasts (success-only per-file is the spec; package-summary covers partial failures via its count).
- Drag-to-move, custom action buttons inside the toast, audio cues.
- Suppressing toasts based on window focus state — they always fire when the setting is on.
- A "clear all toasts" affordance.

## Architecture

```
UploadScheduler.FileStateChanged (existing event)
        │
        ▼
UploadNotificationListener   ──────▶  decides per-file vs. per-package
        │
        ▼
ToastNotificationService     ──────▶  setting gate, dispatcher marshal,
        │                              stack management, position math
        ▼
ToastWindow + ToastViewModel ──────▶  rendered WPF toast (one per call)
        │
        ▼ (click)
MainViewModel.ActivateAndShowUploadedTab()
   (restores window via TrayIconManager.ShowMainWindow + sets SelectedTabIndex)
```

The listener and service are pure C#; the toast itself is a small WPF window.

## File structure

### New files

| Path | Responsibility |
|---|---|
| `src/Services/IToastNotificationService.cs` | DI seam: `ShowFileCompleted(PackageFile)` and `ShowPackageCompleted(Package, succeeded, total)`. |
| `src/Services/ToastNotificationService.cs` | Concrete impl. Reads `AppSettings.ShowCompletionToasts`, marshals to dispatcher, owns the stack list, computes positions from `SystemParameters.WorkArea`. |
| `src/Services/IToastWindowFactory.cs` | Test seam: `IToastHost Create(ToastViewModel)`. Production impl returns a wrapped `ToastWindow`; test fakes return an in-memory stub. |
| `src/Services/DefaultToastWindowFactory.cs` | Production impl — `new ToastWindow(viewModel)`. |
| `src/Services/UploadNotificationListener.cs` | Subscribes to `UploadScheduler.FileStateChanged`. Translates state changes into service calls. Owns the per-package `_summaryShown` flag. |
| `src/ViewModels/ToastViewModel.cs` | `ObservableObject` with `Title`, `Message`, `IconKey`, `ActivateCommand`, `CloseCommand`. |
| `src/Views/ToastWindow.xaml` | Borderless, transparent, top-most window. Contains the visual layout. |
| `src/Views/ToastWindow.xaml.cs` | Owns auto-dismiss `DispatcherTimer`. Pauses on mouse-enter, resumes on mouse-leave. Closes on `CloseCommand`. |
| `tests/Services/ToastNotificationServiceTests.cs` | Setting-gate test, position math test, stack add/remove test (against an injected `IToastWindowFactory` fake so we don't open real windows in xUnit). |
| `tests/Services/UploadNotificationListenerTests.cs` | Feeds synthetic `FileStateChanged` events; asserts service is called once per `Completed` and exactly once per package summary. |

### Modified files

| Path | Change |
|---|---|
| `src/Upload/Settings.cs` | Add `DefaultShowCompletionToasts = true` and `ShowCompletionToasts` property. |
| `src/Dal/SettingRepository.cs` | Persist + hydrate the new flag (matches the `MinimizeToTray` shape). |
| `src/ViewModels/SettingsViewModel.cs` | Hydrate in `LoadAsync`, save in `SaveAsync`, snapshot in dirty tracking, expose `ShowCompletionToasts` as `[ObservableProperty]`. |
| `src/Views/SettingsView.xaml` | Add a checkbox in the General panel: `Settings_ShowCompletionToasts`. |
| `src/App.xaml.cs` | Register `IToastNotificationService` (singleton), `UploadNotificationListener` (singleton, eager-instantiated so it subscribes at startup). |
| `src/ViewModels/MainViewModel.cs` | Expose `ActivateAndShowUploadedTab()` — calls `TrayIconManager.ShowMainWindow()` and sets `SelectedTabIndex` to the Uploaded tab. The toast service depends on `MainViewModel`, not `TrayIconManager`, keeping window-state concerns in the VM. |
| `src/Resources/Strings.*.resx` (×6) | Add `Toast_FileCompleted_Title`, `Toast_FileCompleted_Body`, `Toast_PackageCompleted_Title`, `Toast_PackageCompleted_Body`, `Settings_ShowCompletionToasts`. |

## Component details

### `IToastNotificationService` / `ToastNotificationService`

```csharp
public interface IToastNotificationService
{
    void ShowFileCompleted(PackageFile file);
    void ShowPackageCompleted(Package package, int succeeded, int total);
}
```

Constructor takes `AppSettings`, `IDialogService`-style dispatcher access, and an `IToastWindowFactory` (test seam — production impl `new ToastWindow(viewModel)`). Maintains `private readonly List<ToastWindow> _activeToasts`.

`Show*` flow:

1. Return immediately if `_settings.ShowCompletionToasts == false`.
2. `Application.Current.Dispatcher.BeginInvoke(...)` to ensure UI-thread.
3. Build a `ToastViewModel` (Title/Message/IconKey/`ActivateCommand`/`CloseCommand`).
4. Create the toast via the factory; subscribe to its `Closed` event so we can re-flow positions.
5. Compute the stack position (see below) and place the window with `Left` / `Top`.
6. Show the window non-modally.

**Position math.** Use `SystemParameters.WorkArea` (excludes the taskbar). Right edge: `Left = WorkArea.Right - ToastWidth - 12px margin`. New toasts go *above* the existing stack: `Top = WorkArea.Bottom - 12 - sumOfHeights(activeToasts) - thisHeight`. So the first toast sits at the bottom; each subsequent one appears directly above the previous. When a toast closes, the service re-flows `Top` for every remaining toast so they slide down to fill the gap (no animation in v1 — a direct `Top` reassignment).

**Activate command.** Invokes `MainViewModel.ActivateAndShowUploadedTab()` (which internally calls `TrayIconManager.ShowMainWindow()` and switches the tab), then closes the toast.

### `UploadNotificationListener`

Singleton, eager-instantiated in `App.xaml.cs:ConfigureServices()` (must subscribe before any uploads start). Constructor injects `UploadScheduler` and `IToastNotificationService`.

```csharp
private readonly Dictionary<Package, bool> _summaryShown = new();

private void OnFileStateChanged(object? sender, FileStateChangedEventArgs e)
{
    if (e.New == FileState.Completed)
    {
        _toasts.ShowFileCompleted(e.File);
    }

    Package pkg = e.File.Package;
    if (IsTerminal(e.New) && AllFilesTerminal(pkg) && !_summaryShown.GetValueOrDefault(pkg))
    {
        int succeeded = pkg.Count(f => f.State == FileState.Completed);
        int total = pkg.Count();
        if (succeeded > 0)
        {
            _summaryShown[pkg] = true;
            _toasts.ShowPackageCompleted(pkg, succeeded, total);
        }
    }
}
```

`IsTerminal` covers `Completed` / `Failed` / `Cancelled`. The flag prevents a re-fired summary when the user retries a failed file later.

### `ToastViewModel`

```csharp
public partial class ToastViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _iconKey = string.Empty;  // resource key e.g. "ToastSuccessIcon"

    public IRelayCommand ActivateCommand { get; }
    public IRelayCommand CloseCommand { get; }
}
```

Activate / Close are wired by the service when it builds the VM.

### `ToastWindow`

XAML highlights:

- `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`
- `ShowInTaskbar="False"`, `Topmost="True"`, `ResizeMode="NoResize"`
- `WindowStartupLocation="Manual"` (the service sets `Left`/`Top`)
- Width 360, Height ≈ 80
- Outer `Border` with `CornerRadius="6"`, `Background="{DynamicResource SurfaceBrush}"`, `BorderBrush="{DynamicResource AccentBrush}"`, `BorderThickness="1"`, drop shadow
- Layout: 4 px accent stripe on the left, an `Image` (icon resource) bound to `IconKey`, vertical stack with `Title` (bold) and `Message`, a small "×" close button in the top-right that triggers `CloseCommand`
- Mouse-down anywhere on the body → `ActivateCommand`; the close × stops propagation

Code-behind:
- `DispatcherTimer` for 5 s auto-dismiss, started in `Loaded`, stopped on `MouseEnter`, restarted on `MouseLeave`.
- `CloseCommand` calls `Close()`.

## Localization

Add the following resource keys to `Strings.resx` and the 5 translated copies (`fil`, `ja`, `ko`, `vi`, `zh-Hans`):

| Key | English value |
|---|---|
| `Toast_FileCompleted_Title` | `Upload finished` |
| `Toast_FileCompleted_Body` | `{0}` (filename) |
| `Toast_PackageCompleted_Title` | `Package finished` |
| `Toast_PackageCompleted_Body` | `{0} of {1} files uploaded — {2}` (succeeded, total, package name) |
| `Settings_ShowCompletionToasts` | `Show a popup notification when an upload finishes` |

The 5 non-English values can ship as the English string initially with a `# TODO localize` marker — the existing i18n inventory docs already track outstanding translations.

## Persistence

`AppSettings.ShowCompletionToasts` joins the existing `MinimizeToTray` / `AutoDisableFailingProxies` / etc. flags. The persistence pattern (string key in `SettingRepository`, hydration in `SettingsViewModel.LoadAsync`, save in `SaveAsync`, snapshot field for dirty tracking) is mechanical — follow the `MinimizeToTray` example exactly.

## Testing

### Unit tests (must pass)

- `ToastNotificationServiceTests`
  - When `ShowCompletionToasts = false`, no factory call is made.
  - When `ShowCompletionToasts = true`, factory is invoked with a populated VM.
  - Stacking: three sequential `ShowFileCompleted` calls leave three windows in `_activeToasts` at distinct `Top` values, ordered by call sequence.
  - Closing the middle toast re-flows the others.
- `UploadNotificationListenerTests`
  - `Completed` state → `ShowFileCompleted` called once.
  - Non-terminal state → no service call.
  - Package with 4 files, all `Completed` → `ShowPackageCompleted` called exactly once with `(succeeded=4, total=4)`.
  - Package with 3 `Completed` + 1 `Failed` → `ShowPackageCompleted` called once with `(3, 4)`.
  - Package with 0 `Completed` + 4 `Failed` → no summary fired.
  - After a summary fires, retrying a failed file and seeing it succeed must NOT fire a second summary.

### Manual

- Smoke-test on a real upload run with the setting on, then off.
- Verify clicking activates the window and selects the Uploaded tab.
- Verify the toast disappears after 5 s and the timer pauses on hover.
- Verify the toast appears above the Windows taskbar regardless of taskbar position (uses `WorkArea`, not `PrimaryScreenHeight`).

## Open risks

- **Multi-monitor users.** v1 pins toasts to the primary monitor's `WorkArea`. If users complain, we can later read the main window's `Screen` and place toasts on that monitor. Out of scope for now.
- **Heavy concurrent completions.** With many small files completing within a second, the stack could grow large. The 5 s auto-dismiss caps it; we accept the temporary clutter rather than introducing aggregation logic.
- **Window focus-stealing.** Setting `Topmost=true` and `ShowActivated=false` (we will set this on the toast window) avoids stealing focus from the active app while the toast is shown.
