# Upload Files wizard + DirectoryPath cleanup — design

**Date:** 2026-05-10
**Topic:** Add a "Upload files" entry point to the upload wizard, alongside the existing "Upload directory" flow. Use the change as an opportunity to remove the now-vestigial `Package.DirectoryPath` / `Package.SaveFrom` storage from the package abstraction.

## Goal

Today the upload wizard always starts at "Browse directory", then on the next step the user filters which files to include. For users who already know exactly which files they want, this is two steps too many. The new "Upload files" flow lets them pick files directly via a multi-select file dialog, skipping the directory step.

The `+` button in the Uploads toolbar — currently a single button that opens the wizard — becomes a dropdown menu with two items: "Upload directory…" and "Upload files…".

## Background — what `DirectoryPath` does today, and what to do with it

Investigating every reference to `Package.DirectoryPath` / `Package.SaveFrom`:

1. **Name fallback** at `src/Upload/Package.cs:75-77` — used only when `options.Title` is empty; throws otherwise. Trivially replaced by making `Title` required.
2. **Create-time rescan** at `src/Upload/PackageManager.cs:387-389` — calls `Package.AddPackageFiles(SaveFrom)`, which enumerates the directory recursively and filters by `Options.SelectedFiles`. Since the wizard always populates `SelectedFiles` with full paths, the enumeration is redundant: iterating `SelectedFiles` directly produces the same result.
3. **DB round-trip** at `src/Upload/PackageManager.cs:234, 412` — saved and loaded but never read for behavior on the reload path (file rows are reconstructed from `UploadPackageFileDto` directly).
4. **Per-file path** is stored on `PackageFile.SaveFrom` (= `Path.GetDirectoryName(filePath)`), which is what `src/Upload/UploadScheduler.cs:307` reads to actually open files. Independent of the package-level value.
5. **UI display — "Save To" column** at `src/Views/UploadsView.xaml:494` binds `{Binding SaveFrom}` for both `Package` and `PackageFile` rows.
6. **UI command — "Open source directory"** at `src/ViewModels/UploadsViewModel.cs:375` reads `pkg.SaveFrom` for package rows.
7. **Sort/group key** at `src/ViewModels/ColumnValueExtractor.cs:50` maps the column id `"SaveTo"` → property `"SaveFrom"`.

(1)-(4) are not load-bearing once the redundancy is removed. (5)-(7) are the genuine UI consumers. The cleanup keeps `Package.SaveFrom` for those, but makes it a **derived computed property** — no storage, no DB column, no wizard-supplied directory.

## Pass 1 — Foundation: remove stored `DirectoryPath`, derive `SaveFrom` from files

### `src/Upload/PackageOptions.cs`
- Remove the `DirectoryPath` property.
- Make `Title` non-nullable and required (type changes from `string?` to `string`; callers must always supply it).
- `SelectedFiles` becomes the single source of truth for what to upload. Type stays `List<string>?` for now, but in practice it is always set.

### `src/Upload/Package.cs`
- Remove the stored `SaveFrom` property's setter and `options.DirectoryPath` initializer; replace with a **computed get-only `SaveFrom`** that returns the common parent of all `PackageFile.SaveFrom` values:
  - 0 files: `null`.
  - 1+ files all sharing the same `SaveFrom`: that path.
  - 1+ files with differing `SaveFrom`s: the longest shared directory prefix (`Path.GetDirectoryName(...)` chained until equal), or `null` if they don't share one (e.g. different drives).
- `Name` becomes simply `options.Title` — no fallback to `Path.GetFileNameWithoutExtension`, no throw branch.
- Replace `AddPackageFiles(string directory)` with a parameterless `AddPackageFiles()` that iterates `Options.SelectedFiles` directly and constructs `PackageFile` instances per (file × hoster). If `SelectedFiles` is null/empty, no files are added.
- The DataGrid binding `{Binding SaveFrom}` continues to work because `SaveFrom` is still a public string-typed member; package rows now show the derived value (or empty when null).

### `src/Upload/PackageManager.cs`
- `CreatePackageAsync` (around line 383) calls `package.AddPackageFiles()` (no argument); drop the `if (!string.IsNullOrEmpty(package.SaveFrom))` guard.
- `PersistNewPackageAsync` (around line 412) no longer writes `DirectoryPath`.
- DB-reload (around line 232) no longer sets `options.DirectoryPath`. **Important:** the same `PackageOptions` initializer is reload's path into `Package`; it must now set `Title = pkgDto.Name ?? string.Empty` (or use a sane default if `Name` is null) so the new "`Title` is required" contract is honored on reload too. The post-construct override `package.Name = pkgDto.Name ?? package.Name` at line 247 stays, ensuring the persisted name wins regardless.
- `OpenSourceDirectoryCommand` at `UploadsViewModel.cs:371-384` is unchanged: its existing null/empty guard already handles the files-mode case (mixed-directory packages return `null` from the new computed `SaveFrom`, and the command no-ops).

### DAL
- Remove `DirectoryPath` from:
  - `src/Dal/UploadPackageDbm.cs` (entity property + `[Required]` attribute).
  - `src/Dal/UploadPackageDto.cs`.
  - `src/Dal/UploadPackageRepository.cs` — three sites at lines 84, 119, 132.
- Remove the `("UploadPackage", "DirectoryPath", "ALTER TABLE …")` line at `src/FirstRun.cs:75`.
- **DB compatibility for upgraded installs:** `Repository<TDbm,TDto>.InsertAsync` (`src/Dal/Repository.cs:25-33`) uses `DbContext.Add` + `SaveChangesAsync`, which respects EF column mapping — EF will omit the unmapped legacy column from the INSERT. With `NOT NULL DEFAULT ''` already in place, that succeeds. `FirstRun.cs` only adds columns and runs a hard-coded compression-column drop (no general drop/rename). No migration code, no auto-reset. Users who want a clean DB can manually delete the SQLite file (acceptable per discussion).

### Tests — sites needing updates

Compile-time breaks from the property removals + `Title`-required change:

- `tests/Dal/UploadPackageRepositoryTests.cs` — drop `DirectoryPath` assertions and DTO writes.
- `tests/Dal/UploadPackageFileRepositoryTests.cs:208` — drop the `DirectoryPath = string.Empty` DTO write.
- `tests/ViewModels/UploadedViewModelTests.cs:146` — drop the `DirectoryPath = string.Empty` DTO write.
- `tests/Upload/PackageFilePipelineEventsTests.cs:128` — currently relies on the rescan to populate files via `DirectoryPath = Path.GetDirectoryName(path)!`. Switch to `SelectedFiles = [path]` (and add `Title = "..."`).
- `tests/Upload/PackageManagerSoftRemoveTests.cs:71, 90, 353` — same pattern: replace `DirectoryPath` with `SelectedFiles` + `Title`.

New coverage:

- `tests/Upload/PackageTests.cs` (or extend an existing test class): `Package.AddPackageFiles()` adds one `PackageFile` per (selected file × hoster) when `SelectedFiles` is set; adds nothing when `SelectedFiles` is null/empty.
- `tests/Upload/PackageTests.cs` — `Package.SaveFrom` derivation:
  - Returns `null` when no files.
  - Returns the shared directory when all files come from the same directory.
  - Returns the longest common parent when files share an ancestor.
  - Returns `null` when files span drives.

## Pass 2 — UI: dropdown entry point + files-only wizard mode

### Entry point: dropdown off `+`

`src/Views/UploadsView.xaml.cs:160` (`AddUploadButton_Click`) currently opens the wizard directly. Replace with: open a `ContextMenu` attached to the `+` button (set `PlacementTarget` and `IsOpen = true`). The menu has two items:
- "Upload directory…" → opens the wizard in **Directory** mode (current behavior).
- "Upload files…" → opens the wizard in **Files** mode (new).

Each menu item's click handler calls a new `OpenWizard(UploadWizardMode mode)` method that does what `AddUploadButton_Click` does today, parameterized by mode.

### Wizard structure: one window, mode-aware step 0

Single wizard window/VM remains. Add a mode enum and thread it through:

```csharp
public enum UploadWizardMode
{
    Directory,
    Files,
}
```

- `UploadWizardViewModel` gets a `Mode` property, set via constructor (added as a new last parameter).
- `UploadWizardWindow`'s constructor signature changes from `(UploadsViewModel)` to `(UploadsViewModel, UploadWizardMode)` — a single signature, no overload. The only existing call site at `UploadsView.xaml.cs:164` updates to pass the chosen mode.
- The step-0 indicator label uses **two `TextBlock`s** (one with `loc:Loc Wizard_Step_DirectorySource`, one with `loc:Loc Wizard_Step_FilesSource`), each `Visibility`-bound to `IsDirectoryMode` / `IsFilesMode` computed properties on the VM. This avoids needing dynamic key swapping inside `LocExtension` (which `ProvideValue`s once at parse time and synthesizes a `Binding` to `Localizer.Instance[fixedKey]` — see `src/Lib/Localization/LocExtension.cs:32-40`).

### Step 0 panels

**Directory mode (unchanged):** TextBox + Browse button picking a folder. `LoadFiles` enumerates the directory recursively as today.

**Files mode (new):** A "Pick files…" button, plus a small summary `"{0} file(s) selected"` (formatted via `Wizard_Step0_Files_CountFormat`). Clicking the button opens an `OpenFileDialog` with `Multiselect = true`. The result populates the existing `Files` collection with `FileEntry` rows where `RelativePath` is set to `FileName` (since there is no shared root in files mode).

XAML: two `StackPanel`s under step 0 with `Visibility` bound to `IsDirectoryMode` / `IsFilesMode` on the VM.

### `IDialogService` additions

```csharp
string[]? BrowseFiles(string? title = null, string? filter = null);
```

- `filter` follows full Win32 filter syntax (e.g. `"All files|*.*"`) and is passed through to `OpenFileDialog.Filter`. `null` means no filter (default `"*.*"` behavior).
- Return value: `dialog.FileNames` on OK, `null` on cancel.
- Implementation in `src/Services/DialogService.cs` uses `Microsoft.Win32.OpenFileDialog` with `Multiselect = true`. This matches existing precedent at `src/ViewModels/ConnectionManagerViewModel.cs:451`. Folder picker uses `Ookii.Dialogs.Wpf.VistaFolderBrowserDialog`; there's no precedent for a Vista-style multi-file dialog in this codebase, so the WPF `Microsoft.Win32` flavor is the natural fit.
- Mocks for `IDialogService` in tests gain the new method.

### Step 0 → Step 1 transition

`GoNextAsync` at `src/ViewModels/UploadWizardViewModel.cs:99` branches on `Mode`:

- **Directory:** existing logic — validate `DirectoryPath`, `LoadFiles()`.
- **Files:** validate `Files.Count > 0` (showing a new `Wizard_Validation_PickAtLeastOneFile` error); skip enumeration; default `PackageTitle` to `Path.GetFileNameWithoutExtension(Files[0].FullPath)` if currently empty.

`PackageTitle` is required (matches the new `PackageOptions.Title` requirement). Validation on Next from step 1 surfaces a new `Wizard_Validation_TitleRequired` error if empty in either mode.

### Step 1 — files-only additions

In files mode, add an "Add more files…" button alongside "Select all / Deselect all". Clicking it (`AddMoreFilesCommand`) reopens the multi-file picker; new entries are appended to `Files`, deduped by `FullPath` using `StringComparer.OrdinalIgnoreCase`. Existing checkbox state is preserved on duplicates.

`FileEntry.RelativePath` in files mode is the filename. UX trade-offs to acknowledge:

- **Filter loses subfolder semantics:** `ApplyFilter` at `UploadWizardViewModel.cs:236` filters by `RelativePath.Contains(filter)`. In files mode this becomes filename-only filtering — defensible (the user picked specific files; they can filter by name). Documented in this spec, no code change.
- **Duplicate filenames from different folders:** `Add more files…` dedups by full path, so two files named `data.zip` from different folders both appear. They show identical `RelativePath` text. Resolution: when a duplicate filename is detected, render as `filename` for the first and `filename (in <last-folder-name>)` for subsequent ones. Implemented inside the wizard VM's append routine — no XAML change.

### `StartUploadAsync`

In `UploadWizardViewModel.StartUploadAsync` (around line 269): drop `DirectoryPath = DirectoryPath` from the `PackageOptions` initializer. `Title` is required and is already non-empty by step-1 validation. The flow is otherwise identical for both modes.

### Localization — pipeline + new keys

Localization in this project is generated: source of truth lives in `docs/i18n-inventory.md` (English) and one `.<locale>.md` file per locale (`zh-Hans`, `ja`, `ko`, `vi`, `fil`), and `scripts/md-to-resx.py` regenerates `src/Resources/Strings*.resx` from them. New keys must be added to **all 6 inventory files**, then the resx files regenerated. A spec note flags translation: translated entries can be added in the same change for staff bilingual locales, or marked with the existing inventory conventions for follow-up.

New keys to add (in `Wizard` section):

- `Wizard_Menu_UploadDirectory` = `Upload directory…`
- `Wizard_Menu_UploadFiles` = `Upload files…`
- `Wizard_Step_DirectorySource` = `1. Directory` (replaces existing step-0 use of `Wizard_Step_Directory`; the existing `Wizard_Step_Directory` is dropped to avoid the ambiguity flagged in review where `Wizard_Step_Files` is already in use as the **step-1** label `"2. Files"`)
- `Wizard_Step_FilesSource` = `1. Files`
- `Wizard_Step0_Files_Title` = `Select files`
- `Wizard_Step0_Files_Desc` = `Pick the files you want to upload. You can add more later.`
- `Wizard_Step0_Files_Pick` = `Pick files…`
- `Wizard_Step0_Files_BrowseDialogTitle` = `Pick files to upload`
- `Wizard_Step0_Files_CountFormat` = `{0} file(s) selected` (`# {0} = file count`)
- `Wizard_Step1_BtnAddMore` = `Add more files…`
- `Wizard_Validation_PickAtLeastOneFile` = `Pick at least one file before continuing.`
- `Wizard_Validation_TitleRequired` = `Enter a package title.`
- `Wizard_Step1_DuplicateFilenameSuffixFormat` = `{0} (in {1})` (`# {0} = filename, {1} = folder name`)

The existing `Wizard_Step_Directory` key is removed from inventories (replaced by `Wizard_Step_DirectorySource`); the existing `Wizard_Step_Files` (`"2. Files"`, used for step 1) is unchanged.

### Tests

- `tests/ViewModels/UploadWizardViewModelTests.cs`: new tests for `UploadWizardMode.Files`:
  - Empty list at step 0 → Next shows the new validation error.
  - `AddMoreFilesAsync` appends with case-insensitive dedup on `FullPath`.
  - Duplicate filename rendering shows the folder suffix.
  - Empty `PackageTitle` at step 1 → Next shows the new title-required error.
  - Transition step 0 → step 1 in files mode does not call `Directory.EnumerateFiles` (verified indirectly by populating `Files` via mock and asserting `Files` is preserved).
- Existing directory-mode tests continue to pass after passing `UploadWizardMode.Directory` (or `default`) into the new VM constructor parameter.

## Out of scope

- No mixed mode (you can't pick a directory and then also add stand-alone files in one wizard session).
- No drag-and-drop of files onto the wizard (orthogonal; could come later).
- No persistence of last-used file dialog directory beyond what `OpenFileDialog` does on its own.
- No new tray-menu / hotkey entry point — the `+` dropdown is the only entry change.
- No new `Repository<TDbm,TDto>` plumbing — purely subtractive in the DAL.
