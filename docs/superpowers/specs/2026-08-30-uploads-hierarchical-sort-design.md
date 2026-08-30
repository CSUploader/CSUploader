# Hierarchical sorting on the Uploads tab

Date: 2026-08-30
Status: implemented, revision 3

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
| **Granular insert into a *filtered* view (no sort descriptions)** | **Wrong position** — the raw SOURCE index is used against the filtered list, never translated |
| `DataGridColumn.CanUserSort` for a path absent on the first row's type | **False** — inferred from the item type, which resolves to `Package` |

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
    comparer = UploadKeyComparer(sort.Direction)     # direction lives INSIDE the comparer
    for package in packages.OrderBy(p => Key(p, sort.Path), comparer):
        emit package
        if package.IsExpanded:
            for file in package.OrderBy(f => Key(f, sort.Path), comparer):
                emit file
```

A package row is emitted immediately before its own files, so the parent/child invariant is
structural — it cannot be violated by a comparison bug. `OrderBy` is a **stable** sort, so equal
keys keep their existing order for free: no queue-index injection, no tiebreaker, no risk of a
subtraction overflow, and none of the strict-weak-ordering hazards a row comparer would carry.
`OrderBy` also computes each key once per element rather than per comparison, so a value
mutating on an upload thread mid-sort cannot produce an inconsistent ordering.

What remains is a **key comparer** — how two values of one column rank — which is pure, small,
and the thing worth testing hard. Note the direction is applied inside it rather than by switching
to `OrderByDescending`: descending must not also flip "nulls last", and reversing the sequence
afterwards would reverse tied groups too.

### When rows are re-ranked

The whole row list is rebuilt in exactly ONE place: an explicit sort, applied by the user
clicking a header or restored at startup. Nothing else ever re-ranks the grid.

Everything structural is instead placed incrementally, keeping the tree without disturbing what
the user is looking at:

| Event | While sorted |
| --- | --- |
| A package arrives | Its block (row + its files) is inserted at its rank, in one positional insert |
| A package is expanded, or gains files | Its files are spliced in ranked, under it, as today |
| Anything is removed | Untouched — removing rows from a ranked list leaves it ranked |
| A value changes (Speed, Progress, …) | Nothing moves |

That the insert index is always a PACKAGE row is what makes the tree structural: a block can
never be dropped between another package and its own files, whatever the ranking says.

**Except while a filter is on**, where positional insertion is abandoned for a full rebuild. The
head wraps these rows in a collection view, and on a granular Add that view inserts at the raw
source index clamped to its own filtered length — it never translates one into the other (probed).
A row aimed at the middle of a filtered view therefore arrives somewhere else, which for a sorted
grid means a mis-ranked package and a package separated from its own file by an unrelated row. A
whole-list replacement raises a Reset, and a Reset makes that view rebuild its filtered list
correctly. It costs the selection, which beats showing a broken tree, and only while a sort and a
filter are both active.

**The Order column needs `CanUserSort="True"` spelled out**, alone among the 21. Avalonia decides
whether a header may sort by looking for the sort path on the collection view's item type, which
resolves to `Package` — the first row — and `QueueOrder` is the one path packages do not have. The
header was inert without it, and a sweep of visible headers could not see it, because Order ships
hidden.

The rebuild uses `RangeObservableCollection.ReplaceAll`, added for this, which raises a single
Reset over the finished contents. `Clear()` followed by an insert would raise two, the first over
an EMPTY collection — the grid drops selection and currency against that empty list and never
gets them back.

### Live values: the ranking is a snapshot

`Speed`, `Progress`, `BytesLoaded`, `ETA` and `Finished` change about twice a second on an active
queue, and the grid does **not** re-rank as they do. A sort ranks the rows as they stood when it
was applied; they hold still afterwards, and clicking the header again re-ranks on demand.

This is a decision, not an omission. Re-ranking live would make rows leap under the pointer on
exactly the columns most worth watching during an upload, and would fight selection and the
editable Order cell. The alternative of re-ranking on unrelated structural events is worse than
either: expanding one package would silently reshuffle the whole grid using values that have
moved since. So a newly arrived package is placed by its rank AT ARRIVAL, and nothing else moves.

The cost is that a rank can go stale — including after a rename under a Name sort. Staleness is
bounded and visible; it can never break the tree, because adjacency is structural.

## Components

**`UploadRowSortKeys` (Core).** Resolves a sort path to a row's value via a cached per-`(Type,
path)` property accessor. `Package` and `PackageFile` expose all 21 paths under the same names
(verified) except `QueueOrder`, which only files have; a path a type lacks yields null.

**`UploadKeyComparer` (Core).** Ranks two key values. Nulls last in **both** directions
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
that, deliberately. No ordering can put a file below a parent that is not there, and an
orphaned file will visually appear under whichever package precedes it.

The honest invariant is therefore narrower than "the tree is preserved": **for every admitted
file whose package is also admitted, that package precedes it with no row from another package
in between.** An admitted file whose package the filter excluded is displayed unparented. Sorting
neither causes nor fixes that; this design does not touch filtering.

**Move and queue order.** The agreed rule was "moving clears the sort so the row is seen to
move". Half of that is not achievable, for a reason that predates this work: `MoveFilesBy` and
`MoveFileTo` only rewrite `QueueOrder` values (`UploadScheduler.cs:264-281`); they never reorder
`VisibleRows` or the package's file list. **The grid has never been displayed in queue order** —
it is in insertion order, and the `Order` column is what shows queue position. So clearing the
sort returns the grid to its ordinary default order, and the moved row's *number* changes while
its *position* does not — exactly as today, unsorted.

The clear is still worth doing: leaving the grid sorted after a move would show a stale ranking.
But the "see it move" half needs the grid to be orderable by queue position, which is a separate
change, flagged and deliberately not smuggled in here. Note that sorting by the `Order` column is
NOT a substitute: `QueueOrder` is global across all packages, while this design ranks files only
WITHIN their package, so that column orders each package's children by queue position rather than
showing the global queue.

The clear is also **deferred**, not synchronous: `SetOrderCommand` runs from
`UploadsGrid_CellEditEnding` (`UploadsView.axaml.cs:394`), and mutating rows inside a
collection-view edit transaction is unsafe. The VM posts the rebuild instead.

## Error handling

An unknown or unreadable persisted sort path leaves default order. A key that cannot be compared
falls back to a string comparison rather than throwing, so one odd row cannot break a sort.
Sorting never mutates a `Package` or `PackageFile`.

## Known and accepted

- A move clears the sort on the REQUEST, not on the reorder completing: the scheduler applies the
  move on its own queue, so a move that turns out to be a no-op still clears. Clearing on
  completion would need `QueueOrderChanged`, which also fires for ordinary queue maintenance and
  would clear sorts nobody touched.
- Hiding the sorted column at runtime leaves the sort active but its indicator off-screen. Only
  the startup restore drops a hidden column's sort (and clears the stored value with it).
- A rename under a Name sort leaves the row at its old rank until the next explicit sort — the
  same snapshot rule as the live value columns.

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
