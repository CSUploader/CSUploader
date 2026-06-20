# Numeric Upload Order Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-package five-level priority enum with a flat per-file numeric upload order (dynamic 1..N, next is always #1), reorderable by typing the number or via Move Up/Down 1/10 actions.

**Architecture:** A per-file integer `QueueOrder` is the single source of upload order. The scheduler orders all files globally by it. A small pure helper (`UploadQueueOrder`) owns the renumber/move algorithm; the scheduler calls it on the single-consumer loop and raises `QueueOrderChanged`, which `PackageManager` persists. The old package-priority concept is removed last, once nothing references it.

**Tech Stack:** C# 14 / .NET 10, WPF (MVVM + CommunityToolkit.Mvvm), EF Core + SQLite, xUnit + Moq.

**Build/test:** the app may hold `bin`; build and test to a temp OutDir:
`dotnet test E:\Projects\CSUploader\CSUploader\tests\CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\`

**Sequencing note:** Tasks 1–7 are additive (the old `Priority` stays compiling). Task 8 deletes the dead priority code. Keep the build green after every task.

---

### Task 1: DAL — `QueueOrder` column + batched update

**Files:**
- Modify: `src/Dal/UploadPackageFileDbm.cs`
- Modify: `src/Dal/UploadPackageFileDto.cs`
- Modify: `src/Dal/UploadPackageFileRepository.cs`
- Modify: `src/FirstRun.cs`
- Test: `tests/Dal/UploadPackageFileRepositoryTests.cs` (create if absent; otherwise add to it)

- [ ] **Step 1: Add the column to the DBM + DTO.**

In `UploadPackageFileDbm.cs`, after the `SortOrder` property (line 61):
```csharp
    /// <summary>
    /// Global upload order across all packages (1-based; lower uploads sooner). 0 for legacy
    /// rows written before this column existed — the scheduler renumbers those on load.
    /// </summary>
    public int QueueOrder { get; set; }
```
In `UploadPackageFileDto.cs`, after `public int SortOrder { get; set; }` (line 44):
```csharp
    public int QueueOrder { get; set; }
```

- [ ] **Step 2: Map the column in all three mappers.**

In `UploadPackageFileRepository.cs`, add `QueueOrder = entity.QueueOrder,` to the `MapToDto(entity) => new()` initializer (after `SortOrder = entity.SortOrder,`, line 129); add `dto.QueueOrder = entity.QueueOrder;` to the `MapToDto(entity, dto)` overload (after line 152); add `QueueOrder = dto.QueueOrder,` to `MapToDbm` (after line 175).

- [ ] **Step 3: Add the batched update method.**

In `UploadPackageFileRepository.cs`, after `UpdateFinishedAsync` (line 110):
```csharp
    /// <summary>
    /// Rewrites <see cref="UploadPackageFileDbm.QueueOrder"/> for many files in one
    /// transaction. A single reorder renumbers the whole queue, so this is called with the
    /// full changed set rather than one row at a time.
    /// </summary>
    public async Task UpdateQueueOrderAsync(IReadOnlyDictionary<int, int> ordersByFileId, CancellationToken ct = default)
    {
        if (ordersByFileId.Count == 0)
        {
            return;
        }

        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        foreach ((int fileId, int order) in ordersByFileId)
        {
            await db.Set<UploadPackageFileDbm>()
                .Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.QueueOrder, order), ct);
        }
    }
```

- [ ] **Step 4: Migration.**

In `src/FirstRun.cs`, add to the additive-columns list (alongside line 97's `UploadPackage`/`Priority` entry):
```csharp
            ("UploadPackageFile", "QueueOrder", "ALTER TABLE UploadPackageFile ADD COLUMN QueueOrder INTEGER NOT NULL DEFAULT 0"),
```
Also add `QueueOrder INTEGER NOT NULL DEFAULT 0` to the `CREATE TABLE UploadPackageFile` block (near line 227's pattern, the file-table create).

- [ ] **Step 5: Write the failing test.**

In `tests/Dal/UploadPackageFileRepositoryTests.cs` (mirror the in-memory SQLite pattern in `tests/Dal/FileHosterLoginRepositoryTests.cs` — `new SqliteConnection("Data Source=:memory:")`, `EnsureCreated()`, a private `TestDbContextFactory`):
```csharp
[Fact]
public async Task UpdateQueueOrderAsync_RewritesOrdersForMultipleFiles()
{
    int p = await InsertPackageAsync("p");
    int a = await InsertFileAsync(p, "a", queueOrder: 1);
    int b = await InsertFileAsync(p, "b", queueOrder: 2);

    await _repo.UpdateQueueOrderAsync(new Dictionary<int, int> { [a] = 2, [b] = 1 });

    Assert.Equal(2, (await _repo.FindAsync(a))!.QueueOrder);
    Assert.Equal(1, (await _repo.FindAsync(b))!.QueueOrder);
}
```
Add a `queueOrder` parameter (default 0) to the test's `InsertFileAsync` helper that sets `QueueOrder = queueOrder` on the inserted DTO, and an `InsertPackageAsync` helper if not present.

- [ ] **Step 6: Run it — expect FAIL** (compile error / method missing), then it should PASS once Steps 1–4 are in.

Run: `dotnet test ...CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\ --filter "FullyQualifiedName~UploadPackageFileRepository"`
Expected: PASS.

- [ ] **Step 7: Commit.**
```bash
git add src/Dal/UploadPackageFileDbm.cs src/Dal/UploadPackageFileDto.cs src/Dal/UploadPackageFileRepository.cs src/FirstRun.cs tests/Dal/UploadPackageFileRepositoryTests.cs
git commit -m "Add UploadPackageFile.QueueOrder column + batched update"
```

---

### Task 2: `PackageFile.QueueOrder` property + persist/load

**Files:**
- Modify: `src/Upload/PackageFile.cs`
- Modify: `src/Upload/PackageManager.cs`

- [ ] **Step 1: Add the property** (with change notification, mirroring `FileHash`).

In `PackageFile.cs`, after the `Priority` property (~line 295):
```csharp
    /// <summary>
    /// Global upload position across all packages (1-based; lower uploads sooner). The
    /// scheduler orders every file by this value. Maintained dense (1..N) over non-terminal
    /// files; terminal files keep a stale value that is not displayed. Fires PropertyChanged
    /// so the Order cell refreshes immediately on a reorder.
    /// </summary>
    public int QueueOrder
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueOrder)));
        }
    }
```
Add `nameof(QueueOrder)` to the `NotifyDisplayPropertiesChanged()` invocations (alongside `nameof(Priority)`, line 38).

- [ ] **Step 2: Persist on insert + load.**

In `PackageManager.cs` `PersistNewPackageAsync` (the `UploadPackageFileDto` initializer, ~line 476), add:
```csharp
                    QueueOrder = file.QueueOrder,
```
In `LoadOnePersistedPackageAsync`, where the `PackageFile pf = new(...) { ... }` is built (~line 304), add `QueueOrder = fileDto.QueueOrder,` to the initializer.

- [ ] **Step 3: Build green.**

Run: `dotnet build src/CSUploader.csproj -p:OutDir=D:\temp2\cbuild\` → 0 errors. (No new test yet; covered by Task 4.)

- [ ] **Step 4: Commit.**
```bash
git add src/Upload/PackageFile.cs src/Upload/PackageManager.cs
git commit -m "Add PackageFile.QueueOrder, persisted and reloaded"
```

---

### Task 3: `UploadQueueOrder` helper (pure renumber/move algorithm)

**Files:**
- Create: `src/Upload/UploadQueueOrder.cs`
- Test: `tests/Upload/UploadQueueOrderTests.cs`

- [ ] **Step 1: Write the failing tests.**

`tests/Upload/UploadQueueOrderTests.cs`:
```csharp
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Upload;

public class UploadQueueOrderTests
{
    [Fact]
    public void Renumber_AssignsDenseOneToN()
    {
        var files = Make(3);
        UploadQueueOrder.Renumber(files);
        Assert.Equal([1, 2, 3], files.Select(f => f.QueueOrder));
    }

    [Fact]
    public void MoveTo_FirstToLast_ShiftsEverythingElseUp()
    {
        var files = Make(5); // positions 1..5
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveTo(files, files[0], 5);
        // old #1 is now #5; old #2..#5 became #1..#4
        Assert.Equal(5, files[0].QueueOrder);
        Assert.Equal([1, 2, 3, 4, 5], OrderedPositions(files));
        Assert.Equal(files[0], InOrder(files).Last());
    }

    [Fact]
    public void MoveTo_ClampsOutOfRange()
    {
        var files = Make(3);
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveTo(files, files[2], 99);
        Assert.Equal(3, files[2].QueueOrder); // clamped to N
    }

    [Fact]
    public void MoveBy_BlockDown_KeepsRelativeOrder()
    {
        var files = Make(5); // A B C D E
        UploadQueueOrder.Renumber(files);
        UploadQueueOrder.MoveBy(files, [files[1], files[2]], +1); // move B,C down 1
        // expected order: A D B C E
        Assert.Equal([files[0], files[3], files[1], files[2], files[4]], InOrder(files));
    }

    private static List<PackageFile> Make(int n)
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
        Package pkg = new(new PackageOptions { Title = "p", FileHosters = new() { { hoster, login } } });
        List<PackageFile> files = [];
        for (int i = 0; i < n; i++)
        {
            files.Add(new PackageFile(pkg, $@"C:\x\f{i}.bin", hoster, login) { QueueOrder = i + 1 });
        }
        return files;
    }

    private static List<PackageFile> InOrder(IEnumerable<PackageFile> files) => [.. files.OrderBy(f => f.QueueOrder)];
    private static int[] OrderedPositions(IEnumerable<PackageFile> files) => [.. InOrder(files).Select(f => f.QueueOrder)];
}
```

- [ ] **Step 2: Run — expect FAIL** (`UploadQueueOrder` not defined).

Run: `dotnet test ...CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\ --filter "FullyQualifiedName~UploadQueueOrder"`
Expected: FAIL (does not compile).

- [ ] **Step 3: Implement the helper.**

`src/Upload/UploadQueueOrder.cs`:
```csharp
// <copyright file="UploadQueueOrder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Pure ordering algebra for the flat upload queue. Operates on a mutable list of
/// non-terminal <see cref="PackageFile"/> held in current upload order; rewrites each
/// file's <see cref="PackageFile.QueueOrder"/> to a dense 1..N. No I/O, no scheduling —
/// the scheduler calls these on its loop and persists the result.
/// </summary>
internal static class UploadQueueOrder
{
    /// <summary>Assigns QueueOrder = 1..N over <paramref name="ordered"/> in its current order.</summary>
    public static void Renumber(IReadOnlyList<PackageFile> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].QueueOrder = i + 1;
        }
    }

    /// <summary>
    /// Moves <paramref name="file"/> to 1-based position <paramref name="target"/> (clamped to
    /// [1, N]); the items in between shift by one; the list is renumbered. <paramref name="ordered"/>
    /// must be the current queue sorted ascending by QueueOrder.
    /// </summary>
    public static void MoveTo(List<PackageFile> ordered, PackageFile file, int target)
    {
        int current = ordered.IndexOf(file);
        if (current < 0 || ordered.Count == 0)
        {
            return;
        }

        target = Math.Clamp(target, 1, ordered.Count);
        ordered.RemoveAt(current);
        ordered.Insert(target - 1, file);
        Renumber(ordered);
    }

    /// <summary>
    /// Moves the <paramref name="selected"/> files (those present in <paramref name="ordered"/>),
    /// kept as a contiguous block in their current relative order, by <paramref name="delta"/>
    /// positions (negative = sooner). Clamped so the block stays within bounds.
    /// </summary>
    public static void MoveBy(List<PackageFile> ordered, IReadOnlyCollection<PackageFile> selected, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        List<PackageFile> block = [.. ordered.Where(selected.Contains)];
        if (block.Count == 0)
        {
            return;
        }

        int firstIdx = ordered.IndexOf(block[0]);
        foreach (PackageFile f in block)
        {
            ordered.Remove(f);
        }

        int insertAt = Math.Clamp(firstIdx + delta, 0, ordered.Count);
        ordered.InsertRange(insertAt, block);
        Renumber(ordered);
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

Run: `dotnet test ...CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\ --filter "FullyQualifiedName~UploadQueueOrder"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit.**
```bash
git add src/Upload/UploadQueueOrder.cs tests/Upload/UploadQueueOrderTests.cs
git commit -m "Add UploadQueueOrder reorder/renumber helper"
```

---

### Task 4: Scheduler ordering + move API + persistence wiring

**Files:**
- Modify: `src/Upload/UploadScheduler.cs`
- Modify: `src/Upload/PackageManager.cs`
- Test: `tests/Upload/UploadSchedulerForceStartTests.cs` (reuse its `GatedPipeline`/`Build` harness in a new sibling test class) or create `tests/Upload/UploadSchedulerOrderTests.cs`

- [ ] **Step 1: Scheduler — order by QueueOrder.**

In `UploadScheduler.FillSlots()`, replace the sort line:
```csharp
// before:
allFiles = [.. _packages.OrderByDescending(p => p.Priority).SelectMany(p => p)];
// after:
allFiles = [.. _packages.SelectMany(p => p).OrderBy(f => f.QueueOrder)];
```

- [ ] **Step 2: Scheduler — append-on-schedule, the `QueueOrderChanged` event, move API, renumber-on-finish.**

Add the event (near the other events, ~line 58):
```csharp
    /// <summary>Raised after QueueOrder values change so the owner can persist them.</summary>
    public event EventHandler<IReadOnlyList<PackageFile>>? QueueOrderChanged;
```
Add a helper that snapshots the current non-terminal queue in order:
```csharp
    private List<PackageFile> OrderedNonTerminalFiles()
    {
        lock (_packagesLock)
        {
            return [.. _packages.SelectMany(p => p)
                .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
                .OrderBy(f => f.QueueOrder)];
        }
    }
```
Add a reusable "append any unordered file, then densify" method, and call it from BOTH `SchedulePackageFiles` and `FillSlots` (so new packages, reset files, and retried terminal files all land at the end). A non-terminal file with `QueueOrder == 0` is treated as "needs a slot" and appended:
```csharp
    /// <summary>
    /// Gives a global position to any non-terminal file that doesn't have one yet (QueueOrder 0):
    /// appends it after the current max, then renumbers the queue dense 1..N. New packages, reset
    /// files, and retried Failed/Cancelled files (which set QueueOrder back to 0) all append here.
    /// Returns true and fires QueueOrderChanged if anything changed.
    /// </summary>
    private bool EnsureQueueOrdered()
    {
        List<PackageFile> ordered = OrderedNonTerminalFiles();
        PackageFile[] unplaced = [.. ordered.Where(f => f.QueueOrder == 0)];
        if (unplaced.Length == 0)
        {
            return false;
        }

        int next = ordered.Count == 0 ? 0 : ordered.Where(f => f.QueueOrder > 0).Select(f => f.QueueOrder).DefaultIfEmpty(0).Max();
        foreach (PackageFile file in unplaced)
        {
            file.QueueOrder = ++next; // temporary; Renumber below makes it dense
        }

        List<PackageFile> reordered = [.. ordered.OrderBy(f => f.QueueOrder)];
        UploadQueueOrder.Renumber(reordered);
        QueueOrderChanged?.Invoke(this, reordered);
        return true;
    }
```
Call `EnsureQueueOrdered();` at the very top of `FillSlots()` (before the `IsPaused` check is fine — it only acts when there are unplaced files), and also in `SchedulePackageFiles` after the state-setting foreach. Because every re-queue path ends in `FillSlots()`/`FillAvailableSlots()`, this is the single choke point that appends late arrivals.

To make retried terminal files append (spec: "re-queue appends to the end"), set `QueueOrder = 0` in the re-queue paths:
- `PackageManager.ResetFile` (static) — add `file.QueueOrder = 0;` alongside the other resets.
- `UploadScheduler.ForceStartFile` — in the `if (file.State == FileState.Completed)` block (the force-start re-upload path added earlier this session), add `file.QueueOrder = 0;`.
A Paused file keeps its `QueueOrder` (it never left the non-terminal set), so resuming preserves its place — correct.
Add the public move API + a renumber method (place near `ForceStart`):
```csharp
    /// <summary>Moves a file to 1-based position <paramref name="target"/> in the global queue.</summary>
    public void MoveFileTo(PackageFile file, int target) => Post(() =>
    {
        List<PackageFile> ordered = OrderedNonTerminalFiles();
        UploadQueueOrder.MoveTo(ordered, file, target);
        QueueOrderChanged?.Invoke(this, ordered);
        FillSlots();
    });

    /// <summary>Moves the given files as a block by <paramref name="delta"/> positions (negative = sooner).</summary>
    public void MoveFilesBy(IReadOnlyList<PackageFile> files, int delta) => Post(() =>
    {
        List<PackageFile> ordered = OrderedNonTerminalFiles();
        UploadQueueOrder.MoveBy(ordered, files, delta);
        QueueOrderChanged?.Invoke(this, ordered);
        FillSlots();
    });

    private void RenumberQueue()
    {
        List<PackageFile> ordered = OrderedNonTerminalFiles();
        UploadQueueOrder.Renumber(ordered);
        QueueOrderChanged?.Invoke(this, ordered);
    }
```
In `OnUploadCompleted` and `OnHashCompleted`, after the terminal state is set (i.e. when the file became Completed/Failed/Cancelled), call `RenumberQueue();` before the existing `FillSlots();` so the remaining files re-densify to 1..N (next == #1). (Only needed on terminal transitions; safe to call on every completion.)

- [ ] **Step 3: PackageManager — persist QueueOrder changes.**

In the `PackageManager` constructor (after `_scheduler.FileStateChanged += OnFileStateChanged;`, line 59):
```csharp
        _scheduler.QueueOrderChanged += OnQueueOrderChanged;
```
Add the handler (mirror the fire-and-forget persistence in `OnFileStateChanged`):
```csharp
    private void OnQueueOrderChanged(object? sender, IReadOnlyList<PackageFile> files)
    {
        Dictionary<int, int> orders = files
            .Where(f => f.DbId is not null)
            .ToDictionary(f => f.DbId!.Value, f => f.QueueOrder);
        if (orders.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _persistLock.WaitAsync();
            try
            {
                await _fileRepo.UpdateQueueOrderAsync(orders);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to persist queue order: {ex.Message}");
            }
            finally
            {
                _persistLock.Release();
            }
        });
    }
```
Add pass-throughs used by the ViewModel:
```csharp
    public void MoveFileTo(PackageFile file, int target) => _scheduler.MoveFileTo(file, target);

    public void MoveFilesBy(IReadOnlyList<PackageFile> files, int delta) => _scheduler.MoveFilesBy(files, delta);
```

- [ ] **Step 4: Write the failing tests** (new class in `tests/Upload/UploadSchedulerForceStartTests.cs`-style harness; copy the `GatedPipeline`, `Build`, `WaitFor`, `MakeFile` helpers, or factor them into a shared base). Use a hash-free `GatedPipeline` and `maxUploads: 1` so order is observable.
```csharp
[Fact]
public async Task FillSlots_PicksLowestQueueOrderFirst_AcrossPackages()
{
    // Two single-file packages; the second added gets QueueOrder 2. Move it to 1 → it runs first.
    // (Build two packages on the same scheduler; assert the Uploading file is the moved one.)
}

[Fact]
public async Task MoveFileTo_RenumbersQueueDense()
{
    // 5 queued files (limit 0 so none start); MoveFileTo(file#1, 5); assert orders are 1..5 and
    // the moved file is last.
}

[Fact]
public async Task OnComplete_RenumbersRemainingToStartAtOne()
{
    // 3 files, limit 1; complete the running #1; assert the remaining two are QueueOrder 1 and 2.
}
```
Fill these in following the existing `UploadSchedulerForceStartTests` patterns (gated pipeline, `WaitFor`, `scheduler.Start()`). For "two packages", call `Build` once then add a second package built with the same hoster/login to the same scheduler via `scheduler.AddPackage`.

- [ ] **Step 5: Run — expect PASS** after Steps 1–3.

Run: `dotnet test ...CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\ --filter "FullyQualifiedName~UploadScheduler"`
Expected: PASS (existing force-start tests still green — none asserted package priority ordering).

- [ ] **Step 6: Commit.**
```bash
git add src/Upload/UploadScheduler.cs src/Upload/PackageManager.cs tests/Upload/UploadSchedulerForceStartTests.cs
git commit -m "Order uploads by per-file QueueOrder; add move API + renumber"
```

---

### Task 5: ViewModel — move commands, cell-edit, column extractor

**Files:**
- Modify: `src/ViewModels/UploadsViewModel.cs`
- Modify: `src/ViewModels/ColumnValueExtractor.cs`

- [ ] **Step 1: Repurpose the toolbar commands + add move commands.**

In `UploadsViewModel.cs`, replace `IncreasePriority`/`DecreasePriority` (lines 242–262) with:
```csharp
    /// <summary>Toolbar ▲ — move the focused file one position sooner (toward #1).</summary>
    [RelayCommand]
    private void MoveUp(object? item)
    {
        if (item is PackageFile file)
        {
            _packageManager.MoveFilesBy([file], -1);
        }
    }

    /// <summary>Toolbar ▼ — move the focused file one position later.</summary>
    [RelayCommand]
    private void MoveDown(object? item)
    {
        if (item is PackageFile file)
        {
            _packageManager.MoveFilesBy([file], +1);
        }
    }
```
Replace `SetPriority` (lines 270–277) with a delta-based move over the multi-selection (the right-click submenu):
```csharp
    /// <summary>Right-click → Move submenu. Negative delta = sooner. Operates on all selected file rows.</summary>
    [RelayCommand]
    private void MoveSelected(string? deltaText)
    {
        if (!int.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
        {
            return;
        }

        PackageFile[] files = [.. SelectedRows.OfType<PackageFile>()];
        if (files.Length == 0 && SelectedRow is PackageFile single)
        {
            files = [single];
        }

        if (files.Length > 0)
        {
            _packageManager.MoveFilesBy(files, delta);
        }
    }

    /// <summary>Commit of the editable Order cell — move the file to the typed 1-based position.</summary>
    [RelayCommand]
    private void SetOrder((PackageFile File, int Target) arg)
        => _packageManager.MoveFileTo(arg.File, arg.Target);
```
Keep the private `ResolveOwningPackage` only if still referenced; otherwise delete it in Task 8. (`SelectedRows` already exists — it's the multi-selection snapshot used by Copy.)

- [ ] **Step 2: ColumnValueExtractor — map "Order".**

In `ColumnValueExtractor.cs`, the Uploads-tab switch (line 47): the key `"Order"` maps to the property `QueueOrder` directly, so no special case is needed (the `_ => columnKey` default already returns `"Order"` → there is no `Order` property; add a mapping):
```csharp
                "Order" => "QueueOrder",
```
And delete the `PackagePriority p => ...` arm of `Format` (lines 84–92) in Task 8 (it stops being reachable once the column is gone; leaving it compiles until then).

- [ ] **Step 3: Build green.**

Run: `dotnet build src/CSUploader.csproj -p:OutDir=D:\temp2\cbuild\` → 0 errors.

- [ ] **Step 4: Commit.**
```bash
git add src/ViewModels/UploadsViewModel.cs src/ViewModels/ColumnValueExtractor.cs
git commit -m "Uploads VM: move-up/down + move-by + set-order commands"
```

---

### Task 6: XAML — Order column, Move submenu, toolbar

**Files:**
- Modify: `src/Views/UploadsView.xaml`
- Modify: `src/Views/UploadsView.xaml.cs` (cell-commit → SetOrder)

- [ ] **Step 1: Toolbar — point arrows at the new commands.**

In `UploadsView.xaml`, line 143 `Command="{Binding IncreasePriorityCommand}"` → `Command="{Binding MoveUpCommand}"`; line 150 `DecreasePriorityCommand` → `MoveDownCommand`. Update the two tooltips to the new i18n keys (Task 7): `Uploads_Tooltip_MoveUp` / `Uploads_Tooltip_MoveDown`.

- [ ] **Step 2: Right-click submenu — Move Up/Down 1/10.**

Replace the Priority submenu (lines 251–259) with:
```xml
                    <MenuItem Header="{loc:Loc Uploads_Context_Move}">
                        <MenuItem Header="{loc:Loc Uploads_Move_Up10}"   Command="{Binding MoveSelectedCommand}" CommandParameter="-10" />
                        <MenuItem Header="{loc:Loc Uploads_Move_Up1}"    Command="{Binding MoveSelectedCommand}" CommandParameter="-1" />
                        <MenuItem Header="{loc:Loc Uploads_Move_Down1}"  Command="{Binding MoveSelectedCommand}" CommandParameter="1" />
                        <MenuItem Header="{loc:Loc Uploads_Move_Down10}" Command="{Binding MoveSelectedCommand}" CommandParameter="10" />
                    </MenuItem>
```

- [ ] **Step 3: Order column — editable template column, replaces the Priority column.**

A `DataGridTextColumn` with a display converter can't round-trip (two-way binding calls `ConvertBack`). Use a `DataGridTemplateColumn` with separate display/edit templates instead. Replace the Priority `DataGridTextColumn` (lines 558–560) with:
```xml
                <DataGridTemplateColumn Header="{loc:Loc Uploads_Col_Order}" Width="60"
                                        Visibility="Collapsed" x:Name="OrderColumn">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding Converter={StaticResource QueueOrderDisplayConverter}}"
                                       VerticalAlignment="Center" Margin="4,0,0,0" />
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                    <DataGridTemplateColumn.CellEditingTemplate>
                        <DataTemplate>
                            <!-- Edit raw QueueOrder; package/terminal rows are ignored on commit. -->
                            <TextBox Text="{Binding QueueOrder, Mode=OneWay}" />
                        </DataTemplate>
                    </DataGridTemplateColumn.CellEditingTemplate>
                </DataGridTemplateColumn>
```
Declare the converter in the view resources: `<conv:QueueOrderDisplayConverter x:Key="QueueOrderDisplayConverter" />`. Remove the `PackagePriorityConverter` resource declaration (line 27).
Also update the Copy submenu item (line 288): `CommandParameter="Priority"` → `CommandParameter="Order"`, header `Uploads_Col_Priority` → `Uploads_Col_Order`.

- [ ] **Step 4: Code-behind — commit the edited cell to a move.**

In `UploadsView.xaml.cs`, subscribe to the grid's `CellEditEnding` (wire in XAML `CellEditEnding="UploadsGrid_CellEditEnding"` on the DataGrid, or attach in the constructor). Handler:
```csharp
private void UploadsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
{
    if (e.EditAction != DataGridEditAction.Commit) return;
    if (e.Column != OrderColumn) return;            // x:Name from the XAML column
    if (e.Row.Item is not PackageFile file) return; // ignore package rows
    TextBox? tb = FindDescendant<TextBox>(e.EditingElement);
    if (tb is null || !int.TryParse(tb.Text, out int target)) return;
    if (DataContext is UploadsViewModel vm)
    {
        vm.SetOrderCommand.Execute((file, target));
    }
}

private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
{
    if (root is null) return null;
    if (root is T hit) return hit;
    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
    for (int i = 0; i < n; i++)
    {
        T? found = FindDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        if (found is not null) return found;
    }
    return null;
}
```
The `e.Row.Item is not PackageFile` check makes package/terminal rows effectively read-only (their committed edits are ignored), so no per-row `IsReadOnly` plumbing is needed.

- [ ] **Step 5: Package-row Order display (min of children) + blank for done/unordered.**

Create `src/Converters/QueueOrderDisplayConverter.cs` (IValueConverter) that takes the **row object** (bind `Binding="{Binding}"`) and returns the display string:
```csharp
public object Convert(object value, Type t, object p, CultureInfo c) => value switch
{
    PackageFile f => f.State is FileState.Completed or FileState.Failed or FileState.Cancelled || f.QueueOrder <= 0
        ? string.Empty : f.QueueOrder.ToString(CultureInfo.CurrentCulture),
    Package pkg => pkg.Where(x => x.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
        .Select(x => (int?)x.QueueOrder).Where(o => o > 0).Min() is int m ? m.ToString(CultureInfo.CurrentCulture) : string.Empty,
    _ => string.Empty,
};
public object ConvertBack(...) => throw new NotSupportedException();
```
Register it in the view's resources and set the Order column `Binding="{Binding Converter={StaticResource QueueOrderDisplayConverter}}"`. File rows show their number; package rows show the min child order; done/unordered rows show blank. The `CellEditEnding` handler (Step 4) already ignores commits on non-`PackageFile` rows, so package/terminal rows are effectively read-only.

- [ ] **Step 6: Build + manual smoke.**

Run: `dotnet build src/CSUploader.csproj -p:OutDir=D:\temp2\cbuild\` → 0 errors. (Manual UI verification happens at the end.)

- [ ] **Step 7: Commit.**
```bash
git add src/Views/UploadsView.xaml src/Views/UploadsView.xaml.cs
git commit -m "Uploads grid: editable Order column + Move submenu + toolbar"
```

---

### Task 7: i18n — swap Priority strings for Order/Move

**Files:**
- Modify: `docs/i18n-inventory*.md` (6)
- Modify: `src/Resources/Strings*.resx` (6, via regen)

- [ ] **Step 1: Edit all six inventory `.md` files.** Remove `Uploads_Priority_Highest/High/Normal/Low/Lowest` and `Uploads_Context_Priority`. Rename `Uploads_Col_Priority` value to "Order" (and keep the key, or add `Uploads_Col_Order` and remove `Uploads_Col_Priority` — prefer a new key `Uploads_Col_Order`). Add: `Uploads_Context_Move`, `Uploads_Move_Up1/Up10/Down1/Down10`, `Uploads_Tooltip_MoveUp/MoveDown`. English values:
```
Uploads_Col_Order                    = Order
Uploads_Context_Move                 = Move
Uploads_Move_Up10                    = Up 10
Uploads_Move_Up1                     = Up 1
Uploads_Move_Down1                   = Down 1
Uploads_Move_Down10                  = Down 10
Uploads_Tooltip_MoveUp               = Move up (uploads sooner)
Uploads_Tooltip_MoveDown             = Move down (uploads later)
```
Provide translations in the five satellite inventories (ja/ko/fil/vi/zh-Hans) matching the existing style — translate "Order", "Move", "Up N"/"Down N", and the tooltips.

- [ ] **Step 2: Regen safely (temp + diff), per the i18n-regen-safety rule.**
```bash
PY=$(command -v python || command -v python3)
for pair in "docs/i18n-inventory.md:Strings.resx" "docs/i18n-inventory.ja.md:Strings.ja.resx" "docs/i18n-inventory.ko.md:Strings.ko.resx" "docs/i18n-inventory.fil.md:Strings.fil.resx" "docs/i18n-inventory.vi.md:Strings.vi.resx" "docs/i18n-inventory.zh-Hans.md:Strings.zh-Hans.resx"; do
  md="${pair%%:*}"; out="${pair##*:}"
  "$PY" scripts/md-to-resx.py "$md" "/tmp/resxcheck/$out" >/dev/null
  echo "== $out =="; diff "src/Resources/$out" "/tmp/resxcheck/$out" || true
done
```
Confirm the diff shows ONLY the intended add/remove/rename (no unrelated deletions). Then copy `/tmp/resxcheck/*` over `src/Resources/*`.

- [ ] **Step 3: Build green** (XAML keys resolve). `dotnet build src/CSUploader.csproj -p:OutDir=D:\temp2\cbuild\`.

- [ ] **Step 4: Commit.**
```bash
git add docs/i18n-inventory*.md src/Resources/Strings*.resx
git commit -m "i18n: replace Priority strings with Order/Move (six languages)"
```

---

### Task 8: Remove the dead priority code

**Files:**
- Delete: `src/Upload/PackagePriority.cs`, `src/Converters/PackagePriorityDisplayConverter.cs`
- Modify: `src/Upload/Package.cs`, `src/Upload/PackageFile.cs`, `src/Upload/PackageManager.cs`, `src/Dal/UploadPackageDto.cs`, `src/Dal/UploadPackageRepository.cs`, `src/ViewModels/ColumnValueExtractor.cs`, `tests/**` (any priority test)

- [ ] **Step 1: Remove usages.**
  - `Package.cs`: delete the `Priority` property (lines ~436–458) and its cascade.
  - `PackageFile.cs`: delete `public PackagePriority Priority => Package.Priority;` and the `nameof(Priority)` line in `NotifyDisplayPropertiesChanged`.
  - `PackageManager.cs`: delete the `Priority` branch in `WirePackagePersistence` (the `if (e.PropertyName == nameof(Package.Priority))` block) and any `UpdatePriorityAsync` call.
  - `UploadPackageDto.cs`: remove `public PackagePriority Priority { ... }`. `UploadPackageRepository.cs`: remove the three `Priority` mappings and delete `UpdatePriorityAsync`. (Leave the DB column — unread.)
  - `ColumnValueExtractor.cs`: delete the `PackagePriority p => ...` arm of `Format`.
  - Remove `PackagePriorityDisplayConverter` declaration if any other XAML references it (grep first).
- [ ] **Step 2: Delete the two files.**
```bash
git rm src/Upload/PackagePriority.cs src/Converters/PackagePriorityDisplayConverter.cs
```
- [ ] **Step 3: Fix fallout.** `grep -rn "PackagePriority\|\.Priority\b\|SetPriorityCommand\|IncreasePriorityCommand\|DecreasePriorityCommand\|PackagePriorityConverter" src tests` and resolve every hit (delete obsolete tests, update references). The `ProxySetting.Priority` and `ProxySettingDto.Priority` are UNRELATED (proxy ordering) — do NOT touch them.
- [ ] **Step 4: Full build + test.**
```bash
dotnet test E:\Projects\CSUploader\CSUploader\tests\CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild\
```
Expected: build clean (0 warnings), all tests PASS.
- [ ] **Step 5: Commit.**
```bash
git add -A
git commit -m "Remove per-package priority (enum, converter, cascade, persistence)"
```

---

## Final verification

- [ ] Run the full suite to a temp OutDir — all green, 0 warnings.
- [ ] Launch the app (build-lock permitting): add two packages of several files; confirm the **Order** column shows 1..N across both packages; type a number to move a file and watch the rest renumber; use right-click **Move → Up/Down 1/10** and the toolbar arrows; start uploads with the concurrency limit low and confirm files start in Order sequence and that the queue renumbers so the next is always #1; confirm **Force start** still overrides.
- [ ] Restart the app and confirm the order persisted.
```
