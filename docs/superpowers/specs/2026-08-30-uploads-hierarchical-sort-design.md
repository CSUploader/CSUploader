# Hierarchical sorting on the Uploads tab

Date: 2026-08-30
Status: approved (design), revision 2

Revision 2 replaces the comparer-based approach of revision 1, which probing falsified. The
record of that is kept below under "What we probed", because the failure is the reason the
design looks the way it does.

## The problem

Two defects, one visible and one structural.

**Six columns do not sort at all.** `Name`, `Size`, `Hoster`, `Status`, `Progress` and `Order`
are `DataGridTemplateColumn`s, and a template column has no binding to derive a sort path from.
Without an explicit `SortMemberPath` its header is inert. The other fifteen columns are
`DataGridTextColumn`s and sort implicitly. Two template columns (`Hash`, `URL`) do carry a
`SortMemberPath` and work. This matches the old WPF head and was carried over deliberately —
see `tests/CSUploader.Tests/Views/UploadsViewTests.cs:118`.

**Sorting has no concept of the tree.** `UploadsViewModel.VisibleRows` is a flat
`RangeObservableCollection<object>` holding `Package` rows with their `PackageFile` rows spliced
in behind them (`UploadsViewModel.cs:1079-1108`), wrapped by the view in a
`DataGridCollectionView` carrying a filter and no sort descriptions
(`UploadsView.axaml.cs:144`). A stock sort ranks every row against every other, so files drift
away from their packages and expander rows land wherever their own value puts them.

Sorting files by hoster *inside* a package — the request — is not reachable, and is the only
version that means much: a package row's `HosterDisplay` is just `FileHosters[0].Name`
(`Package.cs:458`).

## Decisions taken

1. **Both levels sort.** Packages ranked among themselves; each package's files ranked within it,
   the package row directly above its own files.
2. **View-only.** Sorting never touches upload order.
3. **Persisted.** The active sort survives restart, alongside the column state already persisted.
4. **Move clears the sort** — with a caveat discovered during design, see "Move and queue order".

Out of scope: multi-column sort (Ctrl+click); any change to the Uploaded tab; and fixing the
pre-existing queue-order display gap described under "Move and queue order".

## What we probed

Revision 1 proposed doing the whole feature as an `IComparer` installed through
`DataGridSortDescription.FromComparer`, leaving `VisibleRows` mutation untouched. Throwaway
probes against Avalonia 12.1.2 (deleted after use) **falsified that**:

| Probe | Result |
| --- | --- |
| Initial sort with a custom comparer | Correct |
| Insert whose view index is mid-list | Correct (comparer consulted) |
| **Insert whose view index lands at `Count-1`** | **Wrong position** — comparer consulted once, then the source index trusted |
| **The real tree shape: expanding a package** | **`pkgA, pkgZ-file, pkgZ`** — the file placed *above its own package* |
| **Insert into a *filtered* sorted view** | **Wrong position**, and the comparer was handed a **null row** |
| In-place change of a sorted key | No re-sort until `Refresh()` |
| `SortDescriptions.Clear()` | Returns the view to source order |
| `FromComparer(cmp, Descending)` | Inverts the comparer's whole result |
| `DataGridTextColumn.SortMemberPath` with a binding | **Empty string** — the path is not recoverable from the column |

`DataGridCollectionView.ProcessInsertToCollection` only validates the neighbour when the insert
index is below `Count-1`; otherwise it trusts the source index. Our tree splices files in at
exactly such positions, so the comparer approach breaks the invariant it exists to protect. It
is abandoned.

## Approach: the ViewModel owns row order

The ViewModel builds `VisibleRows` in sorted tree order itself, and **no sort description is ever
installed on the collection view**. The view keeps using the collection view for filtering only,
exactly as today.

This removes the dependency on Avalonia's insert heuristics, the null-row hazard and the
stale-list binary search in one move, and it puts the ordering logic in Core where it can be
tested without Avalonia at all.

Order is produced by construction rather than by a row comparer:

```
BuildRows(packages, sort):
    for package in packages.OrderBy(p => Key(p, sort.Path), sort.Direction):
        emit package
        if package.IsExpanded:
            for file in package.OrderBy(f => Key(f, sort.Path), sort.Direction):
                emit file
```

A package row is emitted immediately before its own files, so the parent/child invariant is
structural — it cannot be violated by a comparison bug. `OrderBy` is a **stable** sort, so equal
keys keep their existing order for free: no queue-index injection, no tiebreaker, no risk of a
subtraction overflow, and none of the strict-weak-ordering hazards a row comparer would carry.
`OrderBy` also computes each key once per element rather than per comparison, so a value
mutating on an upload thread mid-sort cannot produce an inconsistent ordering.

What remains is a **key comparer** — how two values of one column rank — which is pure, small,
and the thing worth testing hard.

### When rows are rebuilt

Only while a sort is active. With no sort the VM keeps today's incremental splicing untouched,
so all risk is confined to the sorted mode.

While sorted, a **structural** change rebuilds the list in one `Reset`: package added or
removed, files added, expand/collapse. Value changes (`Speed`, `Progress`, `BytesLoaded`, `ETA`)
do **not** rebuild — rows hold still while the queue runs, which is what makes the grid usable
during uploads, and re-clicking the header re-ranks on demand. A structural change re-ranks with
current values; that is acceptable precisely because the user just did something structural.

Rebuilding costs one `Reset` where today's splice raises `Reset` for large packages anyway.

## Components

**`UploadRowSortKeys` (Core).** Resolves a sort path to a row's value via a cached per-`(Type,
path)` property accessor. `Package` and `PackageFile` expose all 21 paths under the same names
(verified) except `QueueOrder`, which only files have; a path a type lacks yields null.

**`UploadRowKeyComparer` (Core).** Ranks two key values. Nulls last in **both** directions
(an idle queue keeps its blank Speed/ETA rows at the bottom either way); strings compare
`CurrentCultureIgnoreCase`, the app being localized; `Status` ranks by `FileState` enum order,
grouping by lifecycle rather than localized spelling; otherwise `IComparable`. Values of
differing runtime types — impossible across the current 21 paths, since `DateTime?`/`DateTime`
and `string`/`string?` box identically, but cheap to guard — fall back to an ordinal comparison
of their string forms rather than letting `CompareTo` throw.

**`UploadRowOrder` (Core).** The `BuildRows` projection above, plus the default (unsorted) order.

**ViewModel.** Holds the active sort, rebuilds on structural change while sorted, and exposes
`SortChanged` for the view. Stays framework-free, as `FilterInvalidated` already does.

**View (thin).** Handles `DataGrid.Sorting`, marks it handled, toggles direction, hands the VM
the new sort, and sets the header's `:sortascending` / `:sortdescending` pseudo-class itself —
bypassing `ProcessSort` means the stock indicator no longer sets itself.

**XAML.** An explicit `SortMemberPath` on **all 21 columns**, not just the six inert ones: a
bound column reports an empty `SortMemberPath` (probed), so a handled `Sorting` event cannot
recover the path for the fifteen that sort today. A coverage test walks every column in the XAML
and asserts each declares a path that resolves on `PackageFile`, in the idiom of
`HosterIconCoverageTests`.

**Persistence.** New `SettingKey.UploadsTabSort`, value `<path>|asc` / `<path>|desc`, absent or
blank meaning default order. Read on grid load after the column state is applied; written on
every change including a clear. An unknown path, or one whose column is hidden, restores nothing
and leaves default order — the same fallback the column-state persistence already takes.

## Two invariants that cannot hold, and why

**The filter already breaks parent/child adjacency.** `MatchesFilter` matches package rows on
package name and file rows on file name independently (`UploadsViewModel.cs:186`), so a filter
can show a file whose package row is filtered out — `UploadsViewTests.cs:219` asserts exactly
that, deliberately. No ordering can put a file below a parent that is not there. The invariant
is therefore stated as: **within the rows the filter admits**, a package is immediately followed
by its own admitted files. Sorting does not change filtering, and this design does not touch it.

**Move and queue order.** The agreed rule was "moving clears the sort so the row is seen to
move". Half of that is not achievable, for a reason that predates this work: `MoveFilesBy` and
`MoveFileTo` only rewrite `QueueOrder` values (`UploadScheduler.cs:264-281`); they never reorder
`VisibleRows` or the package's file list. **The grid has never been displayed in queue order** —
it is in insertion order, and the `Order` column is what shows queue position. So clearing the
sort returns the grid to its ordinary default order, and the moved row's *number* changes while
its *position* does not — exactly as today, unsorted.

The clear is still worth doing: leaving the grid sorted after a move would show a stale ranking.
But the "see it move" half needs the grid to be orderable by queue position, which is a separate
change, flagged and deliberately not smuggled in here. Sorting by the `Order` column is the
workaround available to a user who wants that view, and it now works, since `Order` is one of
the six columns this change makes sortable.

The clear is also **deferred**, not synchronous: `SetOrderCommand` runs from
`UploadsGrid_CellEditEnding` (`UploadsView.axaml.cs:394`), and mutating rows inside a
collection-view edit transaction is unsafe. The VM posts the rebuild instead.

## Error handling

An unknown or unreadable persisted sort path leaves default order. A key that cannot be compared
falls back to a string comparison rather than throwing, so one odd row cannot break a sort.
Sorting never mutates a `Package` or `PackageFile`.

## Testing

Core, no Avalonia: packages ranked; files ranked within a package; a package row always
immediately before its own files; both directions; nulls last in both directions; stability of
equal keys; unknown path yields default order; `QueueOrder` (absent on packages) ranks files and
leaves packages in default order; key resolution per type; `Status` by enum order; `Package`'s
aggregated `FileUrl` and empty `FileHash` rank as the values they actually are (`Package.cs:649`,
`Package.cs:665`) rather than as nulls.

Headless view: a `Hoster` header click sorts (it did nothing before); **every one of the 21
headers sorts** — the regression codex's review predicted for the fifteen bound columns; tree
adjacency holds after sorting; expanding a package while sorted places its files under it (the
exact case that killed revision 1); a package added while sorted lands in rank; sorting with an
active filter; committing an edited `Order` cell while sorted; a move clears the sort; a
persisted sort round-trips and restores its indicator; the existing
`HeaderClick_OnCustomTemplate_StillSortsTheGrid` probe keeps passing.
