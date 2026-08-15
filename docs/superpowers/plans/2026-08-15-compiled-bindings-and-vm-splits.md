# Compiled Bindings + ViewModel Splits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn silent binding failures into build errors (compiled bindings across all 28 views) and dissolve the three god files (`UploadWizardViewModel`, `SettingsViewModel`, `XFileSharingApiPipeline`) — with zero behavior change.

**Architecture:** Per-view compiled-binding opt-in first (project default stays off), then VM splits whose views convert in the same pass, then flip the project default, then a mechanical `partial class` file split of the XFS pipeline base. The 2,762 existing tests are the executable spec throughout.

**Tech Stack:** .NET 10, Avalonia 11.3.18 (+ DataGrid 11.3.13), CommunityToolkit.Mvvm 8.4.2, xUnit + Avalonia.Headless.

**Spec:** `docs/superpowers/specs/2026-08-15-compiled-bindings-and-vm-splits-design.md`

## Global Constraints

- Behavior-preserving: no test assertion changes; only construction sites move with classes.
- Every new file starts with the repo's 4-line `// <copyright …>` header (copy from any existing file, fix the filename).
- Namespaces do not change when files move (`CSUploader.ViewModels`, `CSUploader.Upload.Pipeline.Hosters`).
- `LangVersion=preview`, `Nullable=enable` — new files match surrounding style (explicit types, doc comments).
- Verification loop for EVERY task: `dotnet build CSUploader.sln -v:q -nologo` → `dotnet test CSUploader.sln --no-build -v:q -nologo` → expect `Failed: 0` on both suites (523 + 2,239 at baseline; counts may grow, never shrink).
- **Codex review gate for EVERY task** (user requirement): after tests pass, run a read-only Codex session over the task's diff (`git diff HEAD` pre-commit, or the commit range). Triage findings with superpowers:receiving-code-review rigor — verify each claim in code before acting; fix real issues, rebut false positives with evidence, re-run tests if anything changed. Only then finalize the commit.
- Commits in repo voice (`refactor(scope): narrative summary`), each ending with the `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` trailer.
- Do NOT touch `Views/Cef/**` (excluded from the Windows compile — unverifiable locally).

---

### Task 0: Baseline (worktree, submodule, green suite)

- [x] **Step 1:** Create isolated worktree (`EnterWorktree`, branch `worktree-compiled-bindings-and-splits`).
- [x] **Step 2:** `git submodule update --init --recursive` (build guard requires `external/vscode-icons/icons/file_type_json.svg`).
- [x] **Step 3:** `dotnet build CSUploader.sln -v:q -nologo` → Build succeeded, 0 warnings.
- [x] **Step 4:** `dotnet test CSUploader.sln --no-build` → 523 + 2,239 passed, 0 failed.
- [x] **Step 5:** Commit this plan + spec: `git add docs/superpowers && git commit -m "docs: the plan for compiled bindings and the VM splits"`.

---

### Task 1: Move the wizard's companion classes to their own files

**Files:**
- Modify: `src/CSUploader.Core/ViewModels/UploadWizardViewModel.cs` (delete lines 1802–2189: the four trailing classes)
- Create: `src/CSUploader.Core/ViewModels/FileEntry.cs` (class `FileEntry`, currently lines 1802–1826)
- Create: `src/CSUploader.Core/ViewModels/UploadSource.cs` (class `UploadSource`, currently lines 1828–1861)
- Create: `src/CSUploader.Core/ViewModels/SummaryFileItem.cs` (class `SummaryFileItem`, currently lines 1863–1894)
- Create: `src/CSUploader.Core/ViewModels/HosterUploadSummary.cs` (class `HosterUploadSummary`, currently lines 1896–end)

**Interfaces:** All four classes keep their exact public surface and namespace `CSUploader.ViewModels` — zero reference changes anywhere.

- [x] **Step 1:** Read `UploadWizardViewModel.cs:1795-2189` to capture each class verbatim, including its doc comments and any preceding blank-line separators.
- [x] **Step 2:** Create the four files. Each = copyright header (filename adjusted) + the `using` directives the moved code needs (copy the source file's usings, then let the build prune: remove any that IDE0005/CS8019 would flag — in practice each file needs only what its members reference) + `namespace CSUploader.ViewModels;` + the class, verbatim.
- [x] **Step 3:** Delete lines 1802–2189 from `UploadWizardViewModel.cs` (keep the file ending at `UploadWizardViewModel`'s closing brace).
- [x] **Step 4:** Build. Expected: succeeds, 0 warnings (unused-using warnings mean Step 2 pruning was missed).
- [x] **Step 5:** Full test suite. Expected: 523 + 2,239 passed (no count change — no test files touched).
- [x] **Step 6:** Codex review gate on `git diff` (staged). Address findings. → Codex verdict: approve, zero findings.
- [x] **Step 7:** Commit: `refactor(wizard): the view-model file stops hosting four other classes`

---

### Task 2: Compiled bindings — dialog batch

**Files (modify each `.axaml`, occasionally its `.axaml.cs`):**
| View | DataContext (verify in code-behind first) |
|---|---|
| `Views/MessageBoxWindow.axaml` | code-behind/self — check ctor |
| `Views/CloseActionDialog.axaml` | code-behind/self — check ctor |
| `Views/SpeedLimitDialog.axaml` | code-behind/self — check ctor |
| `Views/ProxyTextDialog.axaml` | code-behind/self — check ctor |
| `Views/AboutWindow.axaml` | code-behind/self — check ctor |
| `Views/ProgressWindow.axaml` | code-behind/self — check ctor |
| `Views/UpdateProgressWindow.axaml` | code-behind/self — check ctor |
| `Views/ErrorDetailsWindow.axaml` | check ctor |
| `Views/HttpDetailsWindow.axaml` | `HttpTransaction` (Lib.Net.Http) — verify |
| `Views/LogDetailsWindow.axaml` | `LogEntryViewModel` (set in ctor, may be null for the loader ctor) |
| `Views/EditAccountWindow.axaml` | check ctor |
| `Views/EditProxyWindow.axaml` | check ctor |
| `Views/WebViewLoginWindow.axaml` | `WebViewLoginViewModel` (`_vm`, ctor line ~97) |
| `Views/ToastWindow.axaml` | `ToastViewModel` — has `x:DataType` already; add only `x:CompileBindings` |
| `DevTools/GalleryWindow.axaml` | DEBUG-only; convert only if trivial, else skip with a comment |

**Procedure per view (bite-sized loop, ~3 min each):**

- [ ] **Step A:** Read the view's `.axaml.cs` ctor(s); note the concrete DataContext type. If DataContext is the window itself, `x:DataType` is the window's own class. If a view has *no* DataContext (pure code-behind property setting), skip it with an inline comment `<!-- reflection: no DataContext; code-behind drives this view -->`.
- [ ] **Step B:** On the root element add (namespace prefix as needed):
  ```xml
  xmlns:vm="using:CSUploader.ViewModels"
  x:CompileBindings="True"
  x:DataType="vm:TheViewModel"
  ```
- [ ] **Step C:** `dotnet build` → fix every AVLN error the compiler now surfaces: each `DataTemplate` inside gets its own `x:DataType`; bindings to non-DataContext sources (`$parent`, `ElementName`, `x:Static`) usually compile as-is; a genuinely dynamic spot gets `x:CompileBindings="False"` on the smallest containing element + a comment naming why.
- [ ] **Step D:** Run the head suite: `dotnet test tests/CSUploader.Tests/CSUploader.Tests.csproj --no-build` → 523 passed. Any view with an existing window test that lacks a `BindingErrorSink` assertion gets one added while there (`using BindingErrorSink sink = BindingErrorSink.Install();` … `Assert.Empty(sink.Errors);`).

- [ ] **Final:** Full suite green → Codex review gate on the batch diff → commit: `refactor(bindings): the dialogs' bindings are checked by the compiler now`

---

### Task 3: Compiled bindings — main views

**Files:** `Views/LogsView.axaml`, `Views/UploadedView.axaml`, `Views/UploadsView.axaml`, `Views/MainWindow.axaml` (+ their tests for BindingErrorSink assertions).

**Known DataContexts:** MainWindow→`MainViewModel`; UploadsView→`UploadsViewModel`; UploadedView→`UploadedViewModel`; LogsView→`LogsViewModel` (assigned in `MainWindow.axaml:33-36`).

**Known reflection islands (leave explicit, commented):**
- UploadsView's DataGrid rows are `Package` **or** `PackageFile` (mixed types — no single `x:DataType` exists): put `x:CompileBindings="False"` on the DataGrid, compile the chrome around it.
- UploadedView's group headers bind `DataGridCollectionViewGroup`; its rows are `UploadedFileRow` — try `x:DataType="vm:UploadedFileRow"` on the columns; fall back to a grid-scoped opt-out if the group-header bindings fight it.
- LogsView rows are `LogEntryViewModel` — expect full conversion to work.

- [ ] **Step 1:** LogsView: root `x:CompileBindings="True"` + `x:DataType="vm:LogsViewModel"`; DataGrid columns `x:DataType="vm:LogEntryViewModel"` where needed. Build; fix AVLN; head tests.
- [ ] **Step 2:** UploadedView: same procedure with `vm:UploadedViewModel` / `vm:UploadedFileRow`; group-header caveat above. Build; head tests.
- [ ] **Step 3:** UploadsView: root compiles against `vm:UploadsViewModel`; DataGrid stays a documented reflection island. Build; head tests.
- [ ] **Step 4:** MainWindow: root `x:DataType="vm:MainViewModel"`; the four `<views:*View DataContext="{Binding *ViewModel}" />` assignments compile against MainViewModel's properties. Build; head tests.
- [ ] **Step 5:** Full suite green → Codex review gate → commit: `refactor(bindings): the shell and the three tab views compile their bindings`

---

### Task 4: Split SettingsViewModel → AccountManagerViewModel (+ convert SettingsView)

**Files:**
- Create: `src/CSUploader.Core/ViewModels/AccountManagerViewModel.cs`
- Modify: `src/CSUploader.Core/ViewModels/SettingsViewModel.cs`
- Modify: `src/CSUploader.Core/ServiceRegistration.cs` (register the new VM alongside the other singletons)
- Modify: `src/CSUploader/Views/SettingsView.axaml` (accounts section DataContext + compiled bindings)
- Modify tests: `tests/ViewModels/SettingsViewModelTests.cs` (general half stays), `tests/ViewModels/SettingsRefreshAllTests.cs`, `tests/ViewModels/SettingsExpiredSessionTests.cs`, `tests/CSUploader.Tests/Views/SettingsAccountsTests.cs`, `tests/CSUploader.Tests/Views/SettingsViewTests.cs`, plus any DI smoke lists.

**Interfaces:**
- Produces: `public sealed partial class AccountManagerViewModel : ObservableObject` — ctor takes exactly the subset of SettingsViewModel's primary-ctor dependencies its members use (determine from the moved members; expected: `FileHosterLoginRepository`, `IAccountVerifier`, `IDialogService`, `IUiDispatcher`, `IAppLogger`, `Lib.Net.ProxyManager`/handler factory if verification needs it — read before deciding, don't guess).
- Produces: `SettingsViewModel.AccountManager` (get-only property, injected).
- Members that move (from the current file; the split boundary is "everything whose only consumers are account management"): `HasAccounts`, `HostersForEditing`, `ReloadAccountsAsync`, `LoadAccountsAsync`, `AddAccountDialogAsync` [RelayCommand], `VerifyCredentialsAsync`, `NowLocal`, `InteractiveLoginAsync`, `ApplySessionCookieIfPresent`, `AddAccountFromDialogAsync`, `RefreshAllAccountsAsync` [RelayCommand], `CheckOneForRefreshAllAsync`, `AccountCheckOutcomeForRow`, `NeedsInteractiveSignIn`, `CheckAccountAsync`, `AddAccountAsync` [RelayCommand], `RemoveSelectedAccountsAsync`, `ResolveAccountTargets`, `EditAccountAsync` [RelayCommand], `SaveEditedAccountAsync`, `RefreshSelectedAccountsAsync`, `RefreshSingleAccountAsync`, `EnableSelectedAccountsAsync`, `DisableSelectedAccountsAsync`, `ApplyEnabledStateAsync`, `AutoDisableIfFailed`, `RowStatus`, `BuildStatusMap`, `ApplyStatusMap` (×2), `UpdateAccountStatus`, the `Accounts` collection + account-side `[ObservableProperty]` fields (identify by reading lines 60–260: any property only the accounts grid binds), and the account-side portion of `LoadCoreAsync`.
- `Loc`/`LocF` helpers are needed by both halves — duplicate the two one-liners (they're `private static` wrappers over `Localizer.Instance`).

- [ ] **Step 1:** Read `SettingsViewModel.cs` fully. Write the member allocation list (settings-half vs accounts-half) as scratch notes; anything ambiguous (shared state) stays on SettingsViewModel with an explicit pass-through.
- [ ] **Step 2:** Create `AccountManagerViewModel` with the moved members verbatim; primary ctor per Step 1's dependency audit.
- [ ] **Step 3:** SettingsViewModel: remove moved members; add ctor param + `public AccountManagerViewModel AccountManager { get; }`; `LoadCoreAsync` calls `AccountManager.LoadAccountsAsync(...)` where it inlined that work before.
- [ ] **Step 4:** `ServiceRegistration.AddCoreServices`: `services.AddSingleton<AccountManagerViewModel>();` next to the other VM singletons.
- [ ] **Step 5:** Build Core only (`dotnet build src/CSUploader.Core -v:q`) and fix fallout; then full build. The head's SettingsView AXAML still binds account members on SettingsViewModel — fix now:
- [ ] **Step 6:** `SettingsView.axaml`: wrap/point the accounts section at `DataContext="{Binding AccountManager}"`; convert the whole view: root `x:CompileBindings="True"` + `x:DataType="vm:SettingsViewModel"`, accounts section `x:DataType="vm:AccountManagerViewModel"`, the three connection-manager panels (lines ~527/552/687) `x:DataType="vm:ConnectionManagerViewModel"`.
- [ ] **Step 7:** Re-point tests: account-behavior test classes construct `AccountManagerViewModel` directly (rename test classes/files to `AccountManagerViewModel*Tests` per the mirror-the-class convention); `SettingsViewModelTests` keeps the general half. `SettingsAccountsTests` (head) drives the view — update its VM setup to reach accounts via `AccountManager`.
- [ ] **Step 8:** Full suite: same totals (moves, not deletions; add a construction smoke only if DI smoke doesn't already resolve the new VM transitively).
- [ ] **Step 9:** Codex review gate → commit: `refactor(settings): account management moves into its own view-model`

---

### Task 5: Split UploadWizardViewModel by step (+ convert the wizard window)

**Files:**
- Create: `src/CSUploader.Core/ViewModels/WizardSourcesViewModel.cs`, `WizardHostersViewModel.cs`, `WizardSummaryViewModel.cs`
- Modify: `src/CSUploader.Core/ViewModels/UploadWizardViewModel.cs`
- Modify: `src/CSUploader/Views/UploadWizardWindow.axaml` (+ `.axaml.cs` where it reaches VM members)
- Modify tests: `tests/ViewModels/UploadWizardSourcesTests.cs`, `UploadWizardTreeTests.cs`, `UploadWizardHosterFilterTests.cs`, `UploadWizardViewModelTests.cs`, `tests/ViewModels/HosterUploadSummaryTests.cs`, head `tests/CSUploader.Tests/Views/UploadWizard*Tests.cs`

**Interfaces (target shape):**
- `WizardSourcesViewModel`: owns `Sources`, `Files` (the `ObservableCollection<FileEntry>`), `Tree`; members `AddFoldersAsync`/`AddFilesAsync` [RelayCommand], `AddDroppedPaths`, `RemoveSource` [RelayCommand], `AddFolderSource`, `AddFileSources`, `AppendFiles`, `RebuildTree`, `AddLooseNode`, `FindBySource`, `RefreshTreeChecks`, `SourcesChanged`, `SeedPackageTitleFromFirstSource` (raises an event the parent uses to seed the title), selection/filtering: `MatchesFileFilter`, `ApplyFilter`, `SelectAll`/`SelectNone`/`SetAllSelected`, `RemoveSelectedFiles`, `BulkMutateFiles`, `ClearFiles`, `SelectedFileCount`, `NotifySelectionStats`, `Files_CollectionChanged`, `FileEntry_PropertyChanged`.
- `WizardHostersViewModel`: owns `FileHosters`; members `LoadFileHostersAsync`, `FindSelectableAccountsAsync`, `MatchesHosterFilter`, `IsHosterFilterActive`, `VisibleHosterCount`, `HosterFilterSummary`, `ListedUsableHosters`, `ClearHosterFilter` [RelayCommand], `RaiseHosterFilterChanged`, `HasSelectedHoster`, `UnusableAccountReason`, `RecomputeHosterValidation`, `Hoster_PropertyChanged`, `FileHosters_CollectionChanged`, `AddAccountForHosterAsync` [RelayCommand], `BuildAccountValidator`.
- `WizardSummaryViewModel`: owns `HosterSummaries`; members `RecomputeSummary`, `RefreshSelectedStorageAsync`, `RefreshOneAsync`, `ApplyRefreshedStorage`, `OnSummaryCapacityChanged`, `RecomputeSummaryCapacity`, `RecomputeAutoFitNotice`, `AutoFitNotice`/`HasAutoFitNotice`, `BuildIncludedFilesPerHoster`.
- Parent `UploadWizardViewModel`: keeps step index + `GoNextAsync`/`GoBack`, package options (`PackageName`, `ScheduledDate`, start mode…), `StartUploadAsync`, `SaveStickySelections`; exposes `Sources`, `Hosters`, `Summary` (get-only child VMs); constructs children, passing shared collections/callbacks — children never reference the parent type (one-way composition, parent subscribes to child events).
- Cross-step data flow: children communicate via the collections they share (`Files`, `FileHosters`) and plain .NET events the parent bridges — pick the *minimal* seam that keeps every existing test's observable behavior identical.

- [ ] **Step 1:** Read `UploadWizardViewModel.cs` fully; write the member allocation + shared-state map (which `[ObservableProperty]` belongs to which child; which stay parent).
- [ ] **Step 2:** Extract `WizardSourcesViewModel` only. Parent delegates via `Sources` property; AXAML paths untouched for now (window still binds parent — add temporary pass-through members ONLY if a binding needs one, and record each for Step 5 cleanup). Build + Core tests after the move.
- [ ] **Step 3:** Extract `WizardHostersViewModel` the same way. Build + tests.
- [ ] **Step 4:** Extract `WizardSummaryViewModel` the same way. Build + tests.
- [ ] **Step 5:** `UploadWizardWindow.axaml`: point each step panel's DataContext at `{Binding Sources}` / `{Binding Hosters}` / `{Binding Summary}`, drop the temporary pass-throughs, convert the window to compiled bindings (root `x:DataType="vm:UploadWizardViewModel"`, panels per child type, item templates per row type — `FileEntry`, `FileHosterSelectionViewModel`, `SummaryFileItem`, `HosterUploadSummary`, `UploadTreeNode`). Update `.axaml.cs` member reaches.
- [ ] **Step 6:** Re-point Core tests to the child VMs (`UploadWizardSourcesTests`/`TreeTests` → `WizardSourcesViewModel`, `HosterFilterTests` → `WizardHostersViewModel`; `UploadWizardViewModelTests` keeps navigation/start coverage). Head wizard tests drive the window — update VM setup paths only.
- [ ] **Step 7:** Full suite green (same or higher totals) → Codex review gate → commit: `refactor(wizard): one view-model per step, and the window compiles its bindings`

---

### Task 6: Flip the project default

**Files:** `src/CSUploader/CSUploader.csproj`, all `.axaml` touched in Tasks 2–5.

- [ ] **Step 1:** In `CSUploader.csproj` replace `<AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>` with `true`, and rewrite the surrounding comment: the port-parity rationale is done; reflection now lives only in explicit islands.
- [ ] **Step 2:** Remove every per-root `x:CompileBindings="True"` added in Tasks 2–5 (now redundant; `x:DataType` stays). Keep every `x:CompileBindings="False"` island + its comment.
- [ ] **Step 3:** Build. Any view that silently relied on the old default now errors — fix by the Task 2 Step C rules.
- [ ] **Step 4:** Full suite green. `grep -r "CompileBindings=\"True\"" src/` → expect zero hits.
- [ ] **Step 5:** Codex review gate → commit: `refactor(bindings): compiled bindings become the default; reflection is the exception now`

---

### Task 7: Split XFileSharingApiPipeline into partial-class files

**Files:**
- Modify: `src/CSUploader.Core/Upload/Pipeline/Hosters/XFileSharingApiPipeline.cs` (keeps: class doc, abstract/virtual config surface, capability properties, `RunAsync` @537, `BuildSignInSpec` @367, `ComposeStoredSession` @381, `ExtractCookieValue` @258)
- Create (all `public abstract partial class XFileSharingApiPipeline` in namespace `CSUploader.Upload.Pipeline.Hosters`):
  - `XFileSharingApiPipeline.Anonymous.cs` — `RunAnonymousAsync` @778, `AnonymousUploadAsync` @1020, `IsTransientNodeFailure` @909, `IsServerUnreachable` @1016
  - `XFileSharingApiPipeline.WebForm.cs` — `RunWebFormAsync` @1296, `GetOrAcquireXfssCookieAsync` @1178, `DirectLoginForUploadAsync` @1224, `AcquireXfssCookieAsync` @1245, `HasValidStoredSessionCookie` @1589, `ClearSessionCookieAsync` @1598
  - `XFileSharingApiPipeline.AccountCheck.cs` — `CheckAccountAsync` @1905, `CheckAccountViaWebFormAsync` @1619, `ReadWebFormAccountAsync` @1664, `RefreshStorageViaMyFilesAsync` @1755, `TryGetAccountInfoAsync` @2946, `TryReadStorageLong` @3030, `PersistApiKeyAsync` @3045, `ClearApiKeyAsync` @3060
  - `XFileSharingApiPipeline.Transport.cs` — `NormaliseUploadUrlScheme` @2186, `UploadAsync` @2279, `ClassicUploadAsync` @2313, `TryChunkedUploadAsync` @2375, `PostFormWithHeadersAsync` @2534, `TryDeriveChunkedEndpoints` @2558, `GenerateChunkSessionId` @2588, `ChunkResponseIsOk` @2606, `ParseFinalizeFileCode` @2616, `ChunkSnippet` @2626, `GetAsync` @2781, `MergeSetCookies` @2909
  - `XFileSharingApiPipeline.Scrape.cs` — `LooksLikeCloudflareChallenge` @995, `LooksLikeEdgeFailure` @1514, `ScrapeSessId` @1575, `LooksLoggedIn` @1791, `ExtractMyAccountUsername` @1837, `ParseSizeToBytes` @1884, `ExtractApiKey` @2714, `ExtractCsrfToken` @2736, `Snippet` @2752, `BuildFailureDetail` @2773, plus every `[GeneratedRegex]` partial method these reference

**Rules:** verbatim moves; each new file gets the copyright header + only the usings its members need; the main file's class doc comment stays put; private nested types/records move with their sole consumer; a shared private field stays in the main file. Line numbers above are pre-Task-1 references into the current file — re-locate by member name at execution time.

- [ ] **Step 1:** Create the five partial files, move members per the allocation. Build after each file to keep errors local.
- [ ] **Step 2:** Full build → 0 warnings.
- [ ] **Step 3:** Run the XFS-family tests: `dotnet test tests/CSUploader.Core.Tests.csproj --no-build --filter "FullyQualifiedName~XFileSharing|FullyQualifiedName~Xfs"` → all pass; then the full suite.
- [ ] **Step 4:** Codex review gate → commit: `refactor(hosters): the XFS base spreads across six files, one concern each`

---

### Task 8: Final verification + wrap-up

- [ ] **Step 1:** Full build + full suite from clean: `dotnet build CSUploader.sln` fresh, `dotnet test CSUploader.sln` → 0 failures, counts ≥ baseline.
- [ ] **Step 2:** `git log --oneline master..HEAD` — confirm one commit per task, plan checkboxes all ticked (update this file's boxes as tasks complete, committing the tick with each task).
- [ ] **Step 3:** Use superpowers:finishing-a-development-branch to decide merge/PR with the user.
