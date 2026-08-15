# Compiled Bindings + ViewModel Splits — Design

**Date:** 2026-08-15
**Status:** Agreed (conversation review, 2026-08-15)

## Problem

Two structural debts left over from the WPF→Avalonia port:

1. **Reflection bindings everywhere.** `AvaloniaUseCompiledBindingsByDefault` is `false`
   (`src/CSUploader/CSUploader.csproj`), a deliberate parity-first choice during the port. 28 AXAML
   files carry 511 `{Binding}` uses; exactly one (`ToastWindow`) declares `x:DataType`. A mistyped
   or drifted binding fails silently at runtime — `BindingErrorSink.cs`'s own doc comment records
   one that sat unnoticed in LogsView for months.
2. **Three files concentrate risk.** `UploadWizardViewModel.cs` (2,189 lines — actually five
   top-level classes in one file), `SettingsViewModel.cs` (1,777 lines — general settings plus a
   ~900-line account manager sharing one class), and `XFileSharingApiPipeline.cs` (3,124 lines of
   protocol-family base).

## Decision

Six steps, sequenced so the binding compiler ends up guarding the freshly split ViewModels, and so
no view's `x:DataType` work is done twice:

1. **Move the wizard's companion classes** (`FileEntry`, `UploadSource`, `SummaryFileItem`,
   `HosterUploadSummary`) into their own files. Pure moves, same namespace.
2. **Convert stable views to compiled bindings, per view** (`x:CompileBindings="True"` +
   `x:DataType` on each root; project default stays `false` for now). Dialogs first, then the main
   views. SettingsView and the wizard window wait for their VM splits.
3. **Split `SettingsViewModel`**: extract `AccountManagerViewModel` (account CRUD, verification,
   refresh-all, enable/disable, status map). Parent exposes it as `AccountManager`; the accounts
   section of SettingsView re-points its DataContext. Convert SettingsView in the same step.
4. **Split `UploadWizardViewModel` by wizard step**: `WizardSourcesViewModel` (sources tree, file
   list building, selection/filter), `WizardHostersViewModel` (hoster list, filter, validation,
   account add), `WizardSummaryViewModel` (per-hoster summaries, capacity, auto-fit, storage
   refresh). Parent keeps navigation, options, `StartUploadAsync`, sticky selections. Convert the
   wizard window in the same step.
5. **Flip `AvaloniaUseCompiledBindingsByDefault` to `true`**, drop the per-root opt-ins, keep only
   documented `x:CompileBindings="False"` islands.
6. **Split `XFileSharingApiPipeline` into `partial class` files by concern** — file moves only, no
   behavioral decomposition. The class is essential complexity (2 credential paths × 2 upload
   protocols × session refresh × scraping) pinned by a dense test lattice; restructuring it
   behaviorally has poor risk/reward.

## Constraints

- **Behavior-preserving throughout.** The existing 2,762 tests (523 head + 2,239 Core) are the
  executable spec. No test's *assertions* change; only construction sites move where a class moves.
- **Known reflection islands** (genuinely dynamic DataContexts) stay reflection *explicitly*:
  the UploadsView grid rows (`Package` or `PackageFile` per row), UploadedView group headers
  (`DataGridCollectionViewGroup`), and any `ColumnValueExtractor`-driven cell. Each gets a comment
  naming why.
- `Views/Cef/**` is excluded from the Windows compile (csproj) and cannot be verified locally —
  the CefGlue login window is out of scope for binding conversion.
- Repo conventions hold: copyright header on new files, file-scoped namespaces, `CSUploader.*`
  namespaces unchanged by the Core/head file split, xUnit/Moq test conventions per `tests/CLAUDE.md`.
- **Codex reviews every step** (user requirement): after each task's tests pass and before its
  commit is considered done, a Codex session reviews the diff; findings are verified against the
  code before being acted on.

## Non-goals

- No credential encryption (explicitly deferred by the user, 2026-08-15).
- No behavior or UI changes; no new features; no CHANGELOG entry (not user-visible).
- No conversion of `{loc:Loc}` localization (it builds bindings in code, unaffected by x:DataType).
