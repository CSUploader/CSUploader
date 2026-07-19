# Update-Check Failure Notification Implementation Plan

> **For agentic workers:** implement task-by-task; each task ends green + reviewer-gated. Steps use `- [ ]`.

**Goal:** Make update-check outcomes explicit so a *failed* check is surfaced to the user — the manual "Check for Updates" menu shows an error dialog naming the reason, and the automatic background/startup poll shows a debounced toast — instead of a failure silently reading as "you're on the latest version."

**Architecture:** `IUpdateService.CheckAsync` returns an explicit `UpdateCheckResult` (UpToDate / Available / Failed(reason) / NotInstalled) instead of a `UpdateAvailableInfo?` that collapses failure and "no update" into the same `null`. `MainViewModel.CheckForUpdatesAsync(userInitiated)` maps the result: a *background* failure fires a debounced toast (once per failure episode, re-armed on the next successful check); a *manual* check returns the result so the menu handler can render Available / AlreadyLatest / Error. New localized strings via the inventory→regen flow.

**Tech Stack:** .NET 10, CommunityToolkit.Mvvm, Velopack, xUnit + Moq, `IToastNotificationService` (Core abstraction), i18n via `scripts/md-to-resx.py`.

## Global Constraints

- **Core stays UI-agnostic:** `CSUploader.Core` must not reference Avalonia. Toast goes through the existing `IToastNotificationService` abstraction (Core).
- **i18n regen safety:** the `Strings*.resx` are GENERATED from `docs/i18n-inventory*.md` via `scripts/md-to-resx.py`. Edit the inventory `.md` (the source), then regen, then `--check` must be 6/6 and a `git diff` on the resx must show ONLY the 3 new keys added per locale (a careless regen silently deletes translations). Never hand-edit resx. All 7 locales (invariant/en + fil, ja, ko, vi, zh-Hans).
- **Identity untouched:** no change to `packId`/`AssemblyName`/`release.yml`.
- Reviewer-gated; TDD; small commits.

---

### Task 1: `UpdateCheckResult` type + `CheckAsync` signature + `UpdateService` impl

**Files:**
- Create: `src/CSUploader.Core/Lib/Update/UpdateCheckResult.cs`
- Modify: `src/CSUploader.Core/Lib/Update/IUpdateService.cs`
- Modify: `src/CSUploader.Core/Lib/Update/UpdateService.cs`
- Test: `tests/Lib/Update/UpdateServiceTests.cs` (create only if the folder/pattern exists; otherwise the `NotInstalled` branch is covered here and the rest at the VM level — see note)

**Interfaces:**
- Produces: `Task<UpdateCheckResult> IUpdateService.CheckAsync(CancellationToken)`; `UpdateCheckResult` with `UpdateCheckStatus Status`, `UpdateAvailableInfo? Info`, `string? FailureReason`, and static factories `UpToDate`/`NotInstalled`/`Available(info)`/`Failed(reason)`.

- [ ] **Step 1: Create the result type**

```csharp
// <copyright file="UpdateCheckResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>The kind of outcome an update check produced.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The check completed; no newer release is available.</summary>
    UpToDate,

    /// <summary>A newer release is available (<see cref="UpdateCheckResult.Info"/> is set).</summary>
    Available,

    /// <summary>The check could not complete (<see cref="UpdateCheckResult.FailureReason"/> is set).</summary>
    Failed,

    /// <summary>Not running from a Velopack-installed location (loose build / dotnet run); nothing to check.</summary>
    NotInstalled,
}

/// <summary>
/// The outcome of an update check. Distinguishes a FAILED check from "no update available"
/// so callers can surface the failure instead of showing "you're on the latest version".
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateAvailableInfo? Info = null,
    string? FailureReason = null)
{
    /// <summary>A completed check with no newer release.</summary>
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate);

    /// <summary>A loose/non-installed build with nothing to check.</summary>
    public static UpdateCheckResult NotInstalled { get; } = new(UpdateCheckStatus.NotInstalled);

    /// <summary>A newer release is available.</summary>
    public static UpdateCheckResult Available(UpdateAvailableInfo info) => new(UpdateCheckStatus.Available, Info: info);

    /// <summary>The check failed; <paramref name="reason"/> is a short human-readable message.</summary>
    public static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.Failed, FailureReason: reason);
}
```

- [ ] **Step 2: Change the interface**

In `IUpdateService.cs`, change `CheckAsync` to return the result and update its doc:

```csharp
    /// <summary>
    /// Polls the GitHub Releases endpoint. Returns an explicit outcome so callers can tell
    /// "up to date" from "check failed" (network, auth, 404) from "not installed".
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Update the implementation**

Replace `UpdateService.CheckAsync`:

```csharp
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            // Loose builds and `dotnet run` don't have a Velopack package layout to update.
            return UpdateCheckResult.NotInstalled;
        }

        try
        {
            UpdateInfo? info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return UpdateCheckResult.UpToDate;
            }

            string version = info.TargetFullRelease.Version.ToString();
            return UpdateCheckResult.Available(new UpdateAvailableInfo(version, info));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }
```

- [ ] **Step 4: Test the testable branch**

`UpdateManager` is concrete (not mockable), so only the `NotInstalled` branch is directly unit-testable (a test build is not Velopack-installed → `IsInstalled` is false). If a `tests/Lib/Update/` test project location exists, add:

```csharp
    [Fact]
    public async Task CheckAsync_WhenNotInstalled_ReturnsNotInstalled()
    {
        var svc = new UpdateService(Mock.Of<IAppLogger>());
        UpdateCheckResult result = await svc.CheckAsync();
        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
    }
```

The `UpToDate`/`Available`/`Failed` branches are exercised through the `MainViewModel` contract (Task 2) with a mocked `IUpdateService`. If there is no existing `UpdateService` test file, do not create a new test project — note it and rely on Task 2.

- [ ] **Step 5: Build** — `dotnet build src/CSUploader.Core/CSUploader.Core.csproj -c Release` compiles (callers still reference the old signature and will break until Task 2/3; that's expected — commit Task 1+2+3 together if compilation across the solution is required, or keep the solution red only between these tasks locally). Prefer to land Tasks 1–3 in one commit so the tree never compiles-red on a pushed commit.

---

### Task 2: `MainViewModel` — outcome mapping + debounced background toast

**Files:**
- Modify: `src/CSUploader.Core/ViewModels/MainViewModel.cs`
- Modify: `tests/ViewModels/MainViewModelUpdateTests.cs`

**Interfaces:**
- Consumes: `UpdateCheckResult` (Task 1), `IToastNotificationService.ShowInfo(string title, string body)`.
- Produces: `Task<UpdateCheckResult> CheckForUpdatesAsync(bool userInitiated = false)` (the two existing fire-and-forget call sites keep compiling — the return is discarded).

- [ ] **Step 1: Add the toast dependency + debounce field**

In the ctor (after `_uiDispatcher = ...`, ~line 67):

```csharp
        _toastService = services.GetRequiredService<Services.IToastNotificationService>();
```

With the other fields (~line 27):

```csharp
    private readonly Services.IToastNotificationService _toastService;
    private bool _backgroundCheckFailing;
```

- [ ] **Step 2: Rewrite `CheckForUpdatesAsync`**

Replace the method (currently ~lines 98-125):

```csharp
    /// <summary>
    /// Polls for a newer release. Safe to call from any thread; publishes onto the UI dispatcher.
    /// A background failure (<paramref name="userInitiated"/> == false) shows a debounced toast —
    /// once per failure episode, re-armed after the next successful check — so a chronically
    /// offline machine isn't nagged every poll. A user-initiated check shows nothing here; the
    /// caller renders the returned <see cref="UpdateCheckResult"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool userInitiated = false)
    {
        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: CheckAsync catches internally, but a poll tick must never fault.
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            result = UpdateCheckResult.Failed(ex.Message);
        }

        await _uiDispatcher.InvokeAsync(() => ApplyCheckResult(result, userInitiated));
        return result;
    }

    private void ApplyCheckResult(UpdateCheckResult result, bool userInitiated)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                _availableUpdate = result.Info;
                IsUpdateAvailable = true;
                AvailableVersion = result.Info!.NewVersion;
                _backgroundCheckFailing = false;
                _logger.Log(this, LogType.Status, $"Update available: v{result.Info.NewVersion} (current v{_updateService.CurrentVersion})");
                break;

            case UpdateCheckStatus.UpToDate:
            case UpdateCheckStatus.NotInstalled:
                _availableUpdate = null;
                IsUpdateAvailable = false;
                AvailableVersion = null;
                _backgroundCheckFailing = false;
                break;

            case UpdateCheckStatus.Failed:
                // A transient failure must NOT hide a previously-known available update, so leave
                // IsUpdateAvailable/_availableUpdate as they are. Surface a background failure once
                // per episode; a user-initiated failure is rendered by the caller from the result.
                if (!userInitiated && !_backgroundCheckFailing)
                {
                    _backgroundCheckFailing = true;
                    _toastService.ShowInfo(
                        Localizer.Instance["Update_CheckFailed_ToastTitle"],
                        Localizer.Instance["Update_CheckFailed_ToastBody"]);
                }

                break;
        }
    }
```

(The timer + startup call sites `_ = CheckForUpdatesAsync()` are unchanged and still compile.)

- [ ] **Step 3: Update the tests**

In `MainViewModelUpdateTests.cs`:
- Register the toast service in BOTH providers so `GetRequiredService` resolves. In the ctor's `ServiceCollection` (~line 64) add `sc.AddSingleton(Mock.Of<IToastNotificationService>());`, and in `BuildScopedProvider` add a `IToastNotificationService` parameter defaulting to a fresh `Mock<IToastNotificationService>` so a test can pass one to assert on. Simplest: give `CreateVm` an optional `Mock<IToastNotificationService>? toast = null` and register `(toast ?? new Mock<IToastNotificationService>()).Object`.
- Migrate the existing setups to the new return type:
  - `.ReturnsAsync((UpdateAvailableInfo?)null)` → `.ReturnsAsync(UpdateCheckResult.UpToDate)`
  - `.ReturnsAsync(info)` → `.ReturnsAsync(UpdateCheckResult.Available(info))`
  - Keep `CheckForUpdatesAsync_WhenServiceThrows_DoesNotCrash` (VM still catches) and assert `IsUpdateAvailable` stays false.
- Add:

```csharp
    [Fact]
    public async Task CheckForUpdatesAsync_BackgroundFailure_ShowsToastOnce()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("network down"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        await vm.CheckForUpdatesAsync(); // background
        await vm.CheckForUpdatesAsync(); // still failing → debounced, no second toast

        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_BackgroundFailure_ThenSuccess_ReArmsToast()
    {
        Mock<IUpdateService> updater = new();
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("down"));
        await vm.CheckForUpdatesAsync();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.UpToDate);
        await vm.CheckForUpdatesAsync(); // success re-arms
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("down again"));
        await vm.CheckForUpdatesAsync();

        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_UserInitiatedFailure_NoToast_ReturnsFailed()
    {
        Mock<IUpdateService> updater = new();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("boom"));
        Mock<IToastNotificationService> toast = new();
        MainViewModel vm = CreateVm(updater.Object, toast: toast);

        UpdateCheckResult result = await vm.CheckForUpdatesAsync(userInitiated: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("boom", result.FailureReason);
        toast.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FailureAfterAvailable_KeepsUpdateAvailable()
    {
        UpdateAvailableInfo info = new("2.3.4", new object());
        Mock<IUpdateService> updater = new();
        MainViewModel vm = CreateVm(updater.Object);

        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Available(info));
        await vm.CheckForUpdatesAsync();
        updater.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UpdateCheckResult.Failed("blip"));
        await vm.CheckForUpdatesAsync();

        Assert.True(vm.IsUpdateAvailable); // a transient failure must not hide a known update
        Assert.Equal("2.3.4", vm.AvailableVersion);
    }
```

- [ ] **Step 4: Run the Core suite** — `dotnet test tests/CSUploader.Core.Tests.csproj -c Release` all green.

---

### Task 3: Manual menu handler (head) renders the three outcomes

**Files:**
- Modify: `src/CSUploader/Views/MainWindow.axaml.cs`
- Modify: `tests/CSUploader.Tests/Views/MainWindowMenuTests.cs` (read first; adapt/extend to cover the Failed path if it asserts the handler)

- [ ] **Step 1: Rewrite `MenuCheckForUpdates_Click`**

```csharp
    private async void MenuCheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            UpdateCheckResult result = await vm.CheckForUpdatesAsync(userInitiated: true);
            string title = Localizer.Instance["Main_CheckForUpdates_DialogTitle"];
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    await MessageBoxWindow.ShowInformationAsync(
                        this,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_CheckForUpdates_Available_Format"], result.Info!.NewVersion),
                        title);
                    break;
                case UpdateCheckStatus.Failed:
                    await MessageBoxWindow.ShowErrorAsync(
                        this,
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Main_CheckForUpdates_Failed_Format"], result.FailureReason),
                        title);
                    break;
                default: // UpToDate / NotInstalled
                    await MessageBoxWindow.ShowInformationAsync(this, Localizer.Instance["Main_CheckForUpdates_AlreadyLatest"], title);
                    break;
            }
        }
    }
```

Add `using CSUploader.Lib.Update;` if not already imported.

- [ ] **Step 2: Adapt the head menu test** — read `MainWindowMenuTests.cs`; if it drives the handler through a mocked VM, update the setup to the new `CheckForUpdatesAsync` return and add a Failed→ShowError assertion. Keep it green.

- [ ] **Step 3: Run the head suite** — `dotnet test tests/CSUploader.Tests/CSUploader.Tests.csproj -c Release` all green.

---

### Task 4: Localized strings (3 keys × 7 locales) via inventory→regen

**Files:**
- Modify: `docs/i18n-inventory.md` and `docs/i18n-inventory.{fil,ja,ko,vi,zh-Hans}.md`
- Regen (generated, do not hand-edit): `src/CSUploader.Core/Resources/Strings*.resx`

- [ ] **Step 1: Add the three keys to each inventory `.md`**, placed next to the existing `Main_CheckForUpdates_*` entries (match the surrounding format exactly). Keys + text:

| Key | en (invariant) |
|---|---|
| `Main_CheckForUpdates_Failed_Format` | `Couldn't check for updates: {0}` |
| `Update_CheckFailed_ToastTitle` | `Update check failed` |
| `Update_CheckFailed_ToastBody` | `CSUploader couldn't check for updates. It'll try again later.` |

Translations (match existing tone; keep the `{0}` placeholder):

- **ja:** `更新を確認できませんでした: {0}` / `更新の確認に失敗しました` / `CSUploader は更新を確認できませんでした。後でもう一度試します。`
- **ko:** `업데이트를 확인할 수 없습니다: {0}` / `업데이트 확인 실패` / `CSUploader가 업데이트를 확인하지 못했습니다. 나중에 다시 시도합니다.`
- **vi:** `Không thể kiểm tra bản cập nhật: {0}` / `Kiểm tra cập nhật thất bại` / `CSUploader không thể kiểm tra bản cập nhật. Sẽ thử lại sau.`
- **zh-Hans:** `无法检查更新：{0}` / `更新检查失败` / `CSUploader 无法检查更新，稍后将重试。`
- **fil:** `Hindi ma-check ang mga update: {0}` / `Nabigo ang pag-check ng update` / `Hindi ma-check ng CSUploader ang mga update. Susubukan ulit mamaya.`

- [ ] **Step 2: Regenerate** — run the project's md→resx generator (e.g. `python scripts/md-to-resx.py`).

- [ ] **Step 3: Verify regen safety** — `python scripts/md-to-resx.py --check` → **6/6 OK, 0 drift**, AND `git -C . diff --stat -- "src/CSUploader.Core/Resources/Strings*.resx"` shows ONLY additions (the 3 new keys per locale), never a deletion. If any locale lost a key, STOP — the inventory drifted; reconcile before proceeding.

---

### Task 5: Whole-feature review + gate

- [ ] **Step 1: Full solution** — `dotnet build CSUploader.sln -c Release` = 0 warning / 0 error.
- [ ] **Step 2: Both suites** — `CSUploader.Core.Tests` (1143 + the new update tests) green; `CSUploader.Tests` (453 + any menu test) green.
- [ ] **Step 3: i18n** — `--check` 6/6; resx diff additions-only.
- [ ] **Step 4: Fresh reviewer** over the whole feature diff: outcome mapping correctness, debounce (once-per-episode, re-arm on success, never hides a known update), no Core→Avalonia leak, i18n regen safety, tests non-vacuous, manual handler renders all three outcomes.
- [ ] **Step 5: Commit** — `feat: surface update-check failures (manual error dialog + debounced background toast)`.

## Self-Review notes
- Signature ripple: `CheckAsync` return type changes → every mock setup in `MainViewModelUpdateTests` + any other `IUpdateService` consumer must migrate (grep `CheckAsync(` and `IUpdateService`). The two fire-and-forget `_ = CheckForUpdatesAsync()` call sites are unaffected.
- `NotInstalled` on a manual check maps to "AlreadyLatest" (loose/dev builds only) — acceptable; no dedicated string (YAGNI).
