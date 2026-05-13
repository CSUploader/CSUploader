# Package Priority (5-level) — Implementation Plan

## Goal

Replace the visual-reorder behavior of the Uploads-tab ▲/▼ toolbar buttons with a real priority system. Each package carries a 5-level priority (Highest, High, Normal, Low, Lowest; default Normal). The `UploadScheduler` picks files from higher-priority packages first. The visual order of packages on the Uploads tab does **not** change when priority changes.

## Design constraints (confirmed with user)

- **Per-package**, not per-file. All files in a package inherit the package's priority.
- **Five bounded levels**: Highest / High / Normal / Low / Lowest.
- **Higher level uploads first**. Same-priority tiebreaker: insertion order (existing `Packages` collection order).
- **Visual order stays put** when priority changes — no list reshuffle.

## Architecture

- New enum `PackagePriority { Lowest=-2, Low=-1, Normal=0, High=1, Highest=2 }` in `src/Upload/PackagePriority.cs`. Integer backing makes ordering trivial and persistence a single int column.
- `Package` gains an `ObservableProperty`-style `Priority` field (raises `PropertyChanged`), default `PackagePriority.Normal`.
- `PackageFile.Priority` (and the DB column on `UploadPackageFile`) is dead code today — the DBM property is dropped and the SQL column is added to the existing `FirstRun.cs` `dropColumns` list (precedent: the 8 retired compression columns dropped the same way).
- DB: new column `UploadPackage.Priority INTEGER NOT NULL DEFAULT 0`, applied via the existing `FirstRun.cs` `(table, column, alter)` migration table.
- Scheduler picks the next file by ordering `Packages` descending by `Priority`, then by current list index (insertion order).
- Toolbar ▲/▼ become `IncreasePriority` / `DecreasePriority` commands on `UploadsViewModel`. Stepping is capped at Highest/Lowest. A right-click context-menu **Priority** submenu lets the user pick a level directly.
- Visual list (`VisibleRows`) is **not** rebuilt or reordered when priority changes — that's the whole point of the user's complaint.

## Open scope decisions resolved inline

- **Column display**: the existing "Priority" column rebinds to `Package.Priority` (and `PackageFile`'s parent's priority for file rows) via a new `PackagePriorityDisplayConverter` returning the localized level label.
- **Save behavior**: each priority change writes through to the DB (matches how other per-package settings persist).
- **Cross-package: same level**: tiebreaker is the order packages appear in `Packages` (i.e., insertion order, what the user currently sees).
- **Within a package**: files are picked in their existing order (no per-file priority).

## File map

- **New**
  - `src/Upload/PackagePriority.cs` — enum.
  - `src/Converters/PackagePriorityDisplayConverter.cs` — enum → localized label.
- **Modify**
  - `src/Upload/Package.cs` — `Priority` property + PropertyChanged + include in `NotifyDisplayPropertiesChanged`.
  - `src/Upload/PackageFile.cs` — drop the dead `Priority` property; the column-extractor and bindings shift to the package-level value.
  - `src/Upload/UploadScheduler.cs` — `FillSlots` orders `Packages` by `Priority` descending before iterating files.
  - `src/Upload/PackageManager.cs` — load/persist `Package.Priority`; drop the now-removed `PackageFile.Priority` mapping; on `Package.Priority` change, persist to DB.
  - `src/ViewModels/UploadsViewModel.cs` — replace `MoveUp`/`MoveDown` with `IncreasePriority`/`DecreasePriority`; new `SetPriority(PackagePriority)` command for the context-menu submenu. **Do not** mutate `Packages` order. Drop `RebuildVisibleRows()` calls from these commands.
  - `src/Views/UploadsView.xaml` — toolbar ▲/▼ rewire to new commands (keep glyphs and tooltips updated). Add **Priority** submenu under right-click. Rebind Priority column with `PackagePriorityDisplayConverter`.
  - `src/Dal/UploadPackageDbm.cs` + `UploadPackageDto.cs` + `UploadPackageRepository.cs` — add `Priority` column round-trip.
  - `src/Dal/UploadPackageFileDbm.cs` + `UploadPackageFileDto.cs` + `UploadPackageFileRepository.cs` — drop the `Priority` field from the DBM and mapping helpers.
  - `src/FirstRun.cs` — add `("UploadPackage", "Priority", "ALTER TABLE UploadPackage ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0")` to the patch list, and add `("UploadPackageFile", "Priority")` to the existing `dropColumns` list (matches the retired-compression-columns precedent on lines 30–46).
  - `src/ViewModels/ColumnValueExtractor.cs` — `Priority` for Uploads tab maps to `Priority` on either row type; converter formats it.
  - `docs/i18n-inventory*.md` + `src/Resources/Strings*.resx` (6 locales each) — add five level labels (`Uploads_Priority_Highest`, `Uploads_Priority_High`, `Uploads_Priority_Normal`, `Uploads_Priority_Low`, `Uploads_Priority_Lowest`), submenu header (`Uploads_Context_Priority`), tooltip strings.
- **Tests (new)**
  - `tests/Upload/UploadSchedulerPriorityTests.cs` — scheduler picks a Highest-priority package's file before a Normal-priority package's file, even if Normal was added first.
  - `tests/ViewModels/UploadsViewModelPriorityTests.cs` — IncreasePriority steps Normal → High → Highest and caps. DecreasePriority steps Normal → Low → Lowest and caps. Visual `Packages` order is unchanged across multiple priority changes.
  - `tests/Dal/UploadPackageRepositoryPriorityTests.cs` — Priority round-trips through SQLite.
  - `tests/Upload/PackagePriorityTests.cs` — defaults to Normal; setter raises PropertyChanged on the package AND on each child PackageFile (cascade); a Package with zero files still exposes Normal (replaces the old nullable-int rollup that returned null).

## Task breakdown (TDD per task — failing test, implement, run, commit)

### Task 1: Add `PackagePriority` enum

**Files**
- Create: `src/Upload/PackagePriority.cs`

**Step 1: Test (none yet — enum is just data).**

**Step 2: Implement**

```csharp
namespace CSUploader.Upload;

/// <summary>
/// Five-level package upload priority. Integer-backed so ordering is a single
/// comparison and persistence is a plain int column.
/// </summary>
public enum PackagePriority
{
    Lowest = -2,
    Low = -1,
    Normal = 0,
    High = 1,
    Highest = 2,
}
```

**Step 3: Commit**
`git add src/Upload/PackagePriority.cs && git commit -m "Add PackagePriority enum"`

### Task 2: `Package.Priority` observable property

**Files**
- Modify: `src/Upload/Package.cs`

**Step 1: Failing test** (in new `tests/Upload/PackagePriorityTests.cs`)

```csharp
[Fact]
public void Package_PriorityDefault_IsNormal()
{
    Package package = new(new PackageOptions { Title = "p", FileHosters = new() });
    Assert.Equal(PackagePriority.Normal, package.Priority);
}

[Fact]
public void Package_SettingPriority_RaisesPropertyChanged()
{
    Package package = new(new PackageOptions { Title = "p", FileHosters = new() });
    List<string?> changes = [];
    package.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
    package.Priority = PackagePriority.High;
    Assert.Contains(nameof(Package.Priority), changes);
}
```

**Step 2: Implement** — replace the existing nullable-int `Priority` rollup (currently `public int? Priority => files.Max(f => f.Priority)`) with a backed property using the `field` keyword pattern that the codebase already uses for `IsExpanded`:

```csharp
public PackagePriority Priority
{
    get;
    set
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));

        // Cascade so file rows refresh their pass-through Priority cell immediately,
        // not after the 200ms UI tick.
        PackageFile[] snapshot;
        lock (_filesLock) { snapshot = [.. PackageFiles]; }
        foreach (PackageFile f in snapshot)
        {
            f.RaisePropertyChanged(nameof(PackageFile.Priority));
        }
    }
} = PackagePriority.Normal;
```

Add a small `internal void RaisePropertyChanged(string)` on `PackageFile` (or `PropertyChanged?.Invoke(...)` directly if accessible) so the cascade works.

**Step 3: Run** `dotnet test --filter PackagePriorityTests`. **Step 4: Commit.**

### Task 3: Drop `PackageFile.Priority` and add pass-through + cascade hook

**Files**
- Modify: `src/Upload/PackageFile.cs` — remove `public int Priority { get; set; }`. Add `public PackagePriority Priority => Package.Priority;` (pass-through, same pattern as `ScheduledStartTime` on line 119). Add `internal void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));` so Task 2's `Package` setter can cascade into the child rows. Update `NotifyDisplayPropertiesChanged` if needed (it already raises `Priority`).
- Modify: `src/Dal/UploadPackageFileDbm.cs`, `UploadPackageFileDto.cs`, `UploadPackageFileRepository.cs` — drop the `Priority` field and mapping helpers. SQL column is retired via `dropColumns` in Task 4.
- Modify: `src/Upload/PackageManager.cs` — drop `Priority = fileDto.Priority` (line 308) and `Priority = file.Priority` (line 455).
- Modify: `src/ViewModels/ColumnValueExtractor.cs` — Uploads-tab `"Priority"` resolves to `Priority` on either Package or PackageFile (pass-through covers files).

**Step 1: Failing test** — existing tests should keep compiling; add one asserting `file.Priority` reflects `package.Priority`.

**Step 2: Implement** as above.

**Step 3: Run all tests.** **Step 4: Commit.**

### Task 4: DB round-trip for `UploadPackage.Priority`

**Files**
- Modify: `src/Dal/UploadPackageDbm.cs` — add `public int Priority { get; set; }`.
- Modify: `src/Dal/UploadPackageDto.cs` — same.
- Modify: `src/Dal/UploadPackageRepository.cs` — read/write in MapToDbm / MapToDto.
- Modify: `src/FirstRun.cs` — add `("UploadPackage", "Priority", "ALTER TABLE UploadPackage ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0")`.

**Step 1: Failing test** (new `tests/Dal/UploadPackageRepositoryPriorityTests.cs`)

```csharp
[Fact]
public async Task InsertAndGet_RoundTripsPriority()
{
    UploadPackageDto pkg = new() { Name = "p", CreatedDateTime = DateTime.Now, Priority = (int)PackagePriority.High };
    await _repo.InsertAsync(pkg);
    UploadPackageDto[] all = await _repo.GetAllAsync();
    Assert.Equal((int)PackagePriority.High, all.Single(p => p.Id == pkg.Id).Priority);
}
```

**Step 2: Implement.** **Step 3: Run.** **Step 4: Commit.**

### Task 5: Load + persist `Package.Priority` in `PackageManager`

**Files**
- Modify: `src/Upload/PackageManager.cs` — load `Package.Priority` from `pkgDto.Priority` in `LoadOnePersistedPackageAsync`; write through to DB when `Package.Priority` changes (subscribe in `WirePackageEvents` or similar).

**Step 1: Failing test** — extend the existing reload tests in `PackageManagerSoftRemoveTests.cs`:

```csharp
[Fact]
public async Task LoadPersistedPackagesAsync_RestoresPriority()
{
    // ... persist a package with Priority=High, reload, assert package.Priority == High.
}
```

**Step 2: Implement** — load path reads `pkgDto.Priority` (cast to enum). On `Package.PropertyChanged(Priority)`, persist via the existing generic `UploadPackageRepository.UpdateAsync(dto)` round-trip (no new repo method needed) — matches how other per-package settings already persist.

**Step 3: Run.** **Step 4: Commit.**

### Task 6: Scheduler orders by priority

**Files**
- Modify: `src/Upload/UploadScheduler.cs` — `FillSlots` iterates packages ordered by `Priority` descending, then by their current index in `Packages`.

**Step 1: Failing test** (new `tests/Upload/UploadSchedulerPriorityTests.cs`)

```csharp
[Fact]
public void NextFileToHash_PicksHigherPriorityPackageFirst()
{
    // Two packages: Pkg_Normal added first, Pkg_Highest added second.
    // Scheduler with concurrency=1 must pick a file from Pkg_Highest before Pkg_Normal.
}

[Fact]
public void SamePriority_FollowsInsertionOrder()
{
    // Both packages at Normal: first-added wins.
}
```

**Step 2: Implement** — change `_packages.SelectMany(p => p)` to `_packages.OrderByDescending(p => p.Priority).SelectMany(p => p)`. `OrderByDescending` is stable in LINQ-to-objects, so same-priority packages keep their `_packages` insertion order automatically — no explicit `ThenBy` needed.

**Step 3: Run.** **Step 4: Commit.**

### Task 7: VM commands `IncreasePriority` / `DecreasePriority` / `SetPriority`

**Files**
- Modify: `src/ViewModels/UploadsViewModel.cs` — replace `MoveUp`/`MoveDown` body, drop the `Packages.Move(...)` call and `RebuildVisibleRows()`. New `SetPriority(PackagePriority)` command.

**Step 1: Failing test** (new `tests/ViewModels/UploadsViewModelPriorityTests.cs`)

```csharp
[Fact]
public void IncreasePriority_StepsLevelAndCapsAtHighest()
{
    // Start Normal → call once → High → again → Highest → again → still Highest.
}

[Fact]
public void DecreasePriority_StepsLevelAndCapsAtLowest()
{
    // Mirror of above.
}

[Fact]
public void IncreasePriority_DoesNotChangePackagesOrder()
{
    // Two packages; bump the second's priority; assert Packages[0] and Packages[1] still
    // refer to the original objects in the original order.
}
```

**Step 2: Implement.**

```csharp
[RelayCommand]
private void IncreasePriority(object? item)
{
    if (ResolveOwningPackage(item) is Package p && p.Priority < PackagePriority.Highest)
    {
        p.Priority = (PackagePriority)((int)p.Priority + 1);
    }
}

[RelayCommand]
private void DecreasePriority(object? item)
{
    if (ResolveOwningPackage(item) is Package p && p.Priority > PackagePriority.Lowest)
    {
        p.Priority = (PackagePriority)((int)p.Priority - 1);
    }
}

[RelayCommand]
private void SetPriority(PackagePriority level)
{
    if (ResolveOwningPackage(SelectedRow) is Package p)
    {
        p.Priority = level;
    }
}
```

**Step 3: Run.** **Step 4: Commit.**

### Task 8: Localization

**Files**
- Modify: `docs/i18n-inventory.md` and 5 locale variants (`.zh-Hans`, `.ja`, `.ko`, `.vi`, `.fil`) — add:
  - `Uploads_Priority_Highest = Highest`
  - `Uploads_Priority_High = High`
  - `Uploads_Priority_Normal = Normal`
  - `Uploads_Priority_Low = Low`
  - `Uploads_Priority_Lowest = Lowest`
  - `Uploads_Context_Priority = Priority`
  - `Uploads_Tooltip_IncreasePriority = Increase priority`
  - `Uploads_Tooltip_DecreasePriority = Decrease priority`
- Regen `src/Resources/Strings*.resx` via `scripts/md-to-resx.py`.

**Step 1: Implement.** **Step 2: Build.** **Step 3: Commit.**

### Task 9: Converter

**Files**
- Create: `src/Converters/PackagePriorityDisplayConverter.cs` — `PackagePriority → localized string` via `Localizer.Instance[…]` lookup of the keys from Task 8.

**Step 1: Implement.** **Step 2: Build.** **Step 3: Commit.**

### Task 10: XAML wiring — toolbar buttons, context menu, column display

(Run after Task 8 + Task 9 so the converter and resx keys both exist.)

**Files**
- Modify: `src/Views/UploadsView.xaml`
  - Register the new converter as a static resource at the top of the file.
  - Toolbar ▲: `Command="{Binding IncreasePriorityCommand}" CommandParameter="{Binding ElementName=uploadsGrid, Path=SelectedItem}"`. Same for ▼.
  - Tooltips: localized strings (`Uploads_Tooltip_IncreasePriority`, `Uploads_Tooltip_DecreasePriority`).
  - Right-click context menu: new MenuItem `Priority` with 5 subitems, each bound to `SetPriorityCommand` with the matching `PackagePriority` enum value via `{x:Static}` parameter.
  - Priority column: change `Binding="{Binding Priority, TargetNullValue=''}"` (around line 523) to `Binding="{Binding Priority, Converter={StaticResource PackagePriorityConverter}}"`. **Drop `TargetNullValue`** — the new enum is non-nullable so the fallback is dead code.
  - Also update `ColumnValueExtractor.Extract` so the per-column "Copy → Priority" entry returns the localized level label rather than the raw int.

**Step 1: Implement** (UI; no test).

**Step 2: Build + run app to manually verify** (per CLAUDE.md UI guidance).

**Step 3: Commit.**

## Test plan

- `dotnet test` — all 321 existing tests still green plus the new ones from Tasks 2, 4, 5, 6, 7.
- Manual: launch app, add a few packages, ▲/▼ toolbar buttons should change the Priority column label, the row stays put. Right-click → Priority → select a level should also work. Restart the app and verify priorities persist. With several packages queued, the higher-priority ones should start hashing/uploading first.

## Migration notes

- Existing DBs get the new column via the `FirstRun.cs` patch; existing rows default to Priority=0 (Normal).
- The dead `UploadPackageFile.Priority` SQL column is left in place — no destructive ALTER, no risk to legacy data. EF just ignores it.
