# Numeric upload order — design

**Date:** 2026-06-19
**Status:** Approved (brainstorm), ready for implementation plan

## Goal

Replace the per-package five-level text priority (`Highest…Lowest`) with a
**flat, per-file numeric upload order**, so the user can see and control exactly
which file uploads next. The Uploads queue becomes a single numbered list of
files (1..N); the number on a file is its place in the global upload order.

This reverses an earlier decision (a per-file priority was retired in favour of
per-package — see `FirstRun.cs` retired-columns list). We are deliberately going
back to per-file, but as an explicit *order*, not a level.

## Decisions (settled during brainstorm)

- **Model:** explicit reorderable rank. Each non-terminal file has a unique,
  contiguous position. Moving a file renumbers the rest (move #1 → #10 shifts
  #2…#10 each up by one).
- **Scope:** flat, **all files across all packages** — one global 1..N. Package
  grouping is no longer an ordering boundary.
- **Numbering:** **dynamic 1..N over non-terminal files.** When a file finishes,
  the rest renumber so the next file is always **#1**. Completed / Failed /
  Cancelled files leave the numbered set and show "—".
- **Grid:** **keep the existing package-grouped tree.** Each *file* row shows its
  global order number in an "Order" column; package rows show "—" (or the min of
  their files — see Open question O1). Sorting the grid by the Order column gives
  the real upload sequence on demand. (We are NOT flattening the grid.)
- **Reorder mechanisms:**
  - **Type the number** in the Order cell → file jumps to that slot, others shift.
  - **Right-click → "Priority" submenu** → *Up 1 / Up 10 / Down 1 / Down 10*
    (Up = uploads sooner = toward #1; clamped to [1, N]).
  - **Toolbar up/down arrows** (today's `IncreasePriority`/`DecreasePriority`)
    → move the selected file ±1.
- **Force start** is unchanged — it still launches a file past the concurrency
  limit regardless of its order number.

## Data model & persistence

- **`PackageFile.QueueOrder` (int)** — the global upload position. Replaces
  `PackageFile.Priority` (which was a pass-through to `Package.Priority`).
  - Maintained as a dense, contiguous 1..N over the **non-terminal** files
    (Idle / HashQueued / Hashing / UploadQueued / Uploading / Paused), in upload
    order. Terminal files (Completed / Failed / Cancelled) are excluded from the
    numbered set; their `QueueOrder` is not displayed.
- **DB:** new `UploadPackageFile.QueueOrder` INTEGER column via an additive
  `FirstRun` migration (same pattern as the `CreatedDateTime` / per-host columns).
  - `UploadPackageFileRepository`: map the column in all three mappers; add
    `UpdateQueueOrderAsync(IReadOnlyDictionary<int fileId, int order>)` (batched —
    one transaction — because a single reorder rewrites many rows).
- **Retired** (kept in DB but unused, per the existing retire convention):
  - `PackagePriority` enum (delete the type).
  - `Package.Priority` property, its cascade to child files, and the priority
    branch of `WirePackagePersistence`.
  - `UploadPackageRepository.UpdatePriorityAsync` and the `Priority` mapping.
  - The `UploadPackage.Priority` column stays (NOT NULL DEFAULT 0), unread.
  - `PackagePriorityDisplayConverter` (delete).
  - The existing per-package within-file `UploadPackageFile.SortOrder` stays as
    the file's original add-order, used to seed `QueueOrder` for newly added
    packages (see below).

## Scheduler ordering

`UploadScheduler.FillSlots`:

```csharp
// before: OrderByDescending(p => p.Priority).SelectMany(p => p)
allFiles = [.. _packages.SelectMany(p => p).OrderBy(f => f.QueueOrder)];
```

Lowest `QueueOrder` is picked first, across package boundaries, for **both**
hash and upload admission. `QueueOrder` is unique among non-terminal files, so no
tiebreak is needed; the per-host and concurrency gates are unchanged.

## Queue operations

All queue mutations run on the scheduler's single-consumer loop (so renumber +
persist are serialized) and then persist the changed `QueueOrder`s in one batch.
Conceptually there is one ordered list of non-terminal files; `QueueOrder` is the
1-based index into it.

- **Append (new files):** when a package's files are scheduled, assign
  `QueueOrder = currentMax + 1, +2, …` in (package add order, then `SortOrder`
  within the package). New files go to the **end**.
- **Move to position K** (cell edit): clamp K to [1, N]; remove the file from the
  list, insert at index K-1; reassign 1..N.
- **Move by Δ** (Up1 = −1, Up10 = −10, Down1 = +1, Down10 = +10): compute
  `K = clamp(currentK + Δ, 1, N)`, then Move to K.
- **Multi-select move:** operate on the selected files as a block, preserving
  their relative order (sort selected by current `QueueOrder`, move the block).
- **On a file becoming terminal** (OnUploadCompleted / OnHashCompleted →
  Completed/Failed/Cancelled): it leaves the numbered set; renumber the remaining
  non-terminal files 1..N (this is what keeps "next" at #1).
- **On retry / re-queue of a terminal file** (manual Retry, Reset, or a
  Force-start re-upload): the file re-enters the non-terminal set and is
  **appended to the end** of the queue.

## UI

- **Order column** (replaces the Priority column) on the Uploads grid:
  - File rows: editable text cell showing the 1..N number; committing an integer
    calls Move-to-position. Out-of-range/non-integer input is clamped/ignored.
  - Package rows: the **minimum** Order of the package's non-terminal files
    (read-only) so you can see where the package sits; "—" if none.
  - Terminal file rows: "—".
- **Right-click → "Move" submenu** (renamed from "Priority"): replace the five
  Highest…Lowest items with **Up 1 / Up 10 / Down 1 / Down 10**. These bind to a
  `MoveSelectedCommand` with the delta as parameter and operate on all selected
  file rows.
- **Toolbar up/down arrows:** repurpose `IncreasePriorityCommand` /
  `DecreasePriorityCommand` to move the selected file by −1 / +1.
- **Remove:** `SetPriorityCommand`, the priority submenu items, and the
  `PackagePriorityDisplayConverter` reference.
- `ColumnValueExtractor` (per-column copy): change the "Priority" key to "Order"
  yielding the file's number.

## i18n

- **Remove:** `Uploads_Priority_Highest/High/Normal/Low/Lowest`.
- **Add:** `Uploads_Context_MoveUp1`, `_MoveUp10`, `_MoveDown1`, `_MoveDown10`
  (or a single parametrised string), and rename the column header
  `Uploads_Col_Priority` → `Uploads_Col_Order` ("Order").
- All six languages, regenerated from `docs/i18n-inventory*.md` via the
  temp-diff-verify procedure.

## Testing

- Scheduler picks lowest `QueueOrder` first, interleaving files from different
  packages (drop the old package-priority ordering test, add this).
- Move-to-position: typing K shifts the in-between files; renumber stays 1..N.
- Move-by: ±1 and ±10 with clamping at both ends; multi-select block move.
- Append on add; renumber-on-finish keeps the set contiguous with #1 = next.
- Retry/re-queue of a terminal file appends to the end.
- Persistence: `QueueOrder` round-trips; `FirstRun` migration adds the column;
  reload restores order.
- Force start still ignores `QueueOrder` (over-the-limit launch unaffected).

## Touchpoints (from current code)

`PackageFile.cs`, `Package.cs`, `PackagePriority.cs` (delete),
`UploadScheduler.cs` (FillSlots + queue ops), `PackageManager.cs` (move API +
persistence + append), `Dal/UploadPackageFileDbm.cs`, `Dal/UploadPackageFileDto.cs`,
`Dal/UploadPackageFileRepository.cs`, `Dal/UploadPackageDto.cs` /
`UploadPackageRepository.cs` (remove priority), `FirstRun.cs` (add QueueOrder
column; note retired Priority), `Converters/PackagePriorityDisplayConverter.cs`
(delete), `ViewModels/UploadsViewModel.cs` (commands), `ViewModels/ColumnValueExtractor.cs`,
`Views/UploadsView.xaml` (column + submenu + toolbar), `Resources/Strings*.resx`
+ `docs/i18n-inventory*.md`.

## Implementation notes

- **Terminal files keep a stale `QueueOrder`** (it is simply not displayed and is
  filtered out of admission by state); no need to clear it on finish.
- **Sorting the grid by Order** with package rows present: package rows sort by
  their min-order value, so a package and its files won't necessarily stay
  adjacent under that sort. Acceptable; confirm the visual during implementation.
