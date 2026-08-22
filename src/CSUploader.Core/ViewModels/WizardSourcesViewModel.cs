// <copyright file="WizardSourcesViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

/// <summary>
/// The Upload Wizard's FIRST step: sources (folders walked, files picked, drops), the file list they
/// build, the package title it seeds, the source tree, the text filter, and selection. Owned and
/// constructed by <see cref="UploadWizardViewModel"/>, which supplies the two cross-step callbacks —
/// mark-summary-dirty and revalidate-hosters — invoked SYNCHRONOUSLY at exactly the call sites the
/// pre-split code ran them, so the selection→validation→summary ordering is unchanged.
/// </summary>
public sealed partial class WizardSourcesViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    /// <summary>Parent-supplied: flags the Summary step for a rebuild on next entry (the parent owns
    /// the dirty bit because the hoster step sets it too).</summary>
    private readonly Action _markSummaryDirty;

    /// <summary>Parent-supplied: re-runs the hoster step's limit validation — the file selection is
    /// one of its two inputs.</summary>
    private readonly Action _revalidateHosters;

    /// <summary>Supplies the browse-start mode, the fixed folder, and the remembered one. Null in
    /// tests that don't care, in which case the pickers behave as they did before the setting.</summary>
    private readonly AppSettings? _settings;

    /// <summary>Persists the remembered folder so it survives a restart. Null means remember for
    /// this run only — the in-memory <see cref="AppSettings.LastBrowsedFolder"/> still updates, so
    /// the wizard behaves correctly either way and only the durability is lost.</summary>
    private readonly SettingRepository? _settingRepository;

    public WizardSourcesViewModel(
        IDialogService dialogService,
        IAppLogger logger,
        Action markSummaryDirty,
        Action revalidateHosters,
        AppSettings? settings = null,
        SettingRepository? settingRepository = null)
    {
        _dialogService = dialogService;
        _logger = logger;
        _markSummaryDirty = markSummaryDirty;
        _revalidateHosters = revalidateHosters;
        _settings = settings;
        _settingRepository = settingRepository;

        // Hook collection-changed once: any new entry into Files has its PropertyChanged subscribed
        // so validation auto-refreshes on selection toggles, regardless of which code path added it.
        Files.CollectionChanged += Files_CollectionChanged;
    }

    // While true (a bulk Files population — see BulkMutateFiles), the per-item validation + footer recompute is
    // suspended and run ONCE at the end. Otherwise each Files.Add re-runs RecomputeHosterValidation (O(files))
    // and the footer stats (O(files)), making a directory scan O(files²) on the UI thread.
    private bool _bulkLoadingFiles;

    // Subscribes/unsubscribes each entry as it enters and leaves the list. ⚠ It cannot do that for a
    // Reset: ObservableCollection.Clear() raises one with no OldItems, so anything still holding an
    // entry (a Summary's File, say) would keep firing stale IsSelected changes into this VM. Nothing
    // clears Files today — every removal path goes through Remove/RemoveAt — and a future one has to
    // detach the handlers itself first.
    private void Files_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (object? item in e.NewItems)
            {
                if (item is FileEntry entry)
                {
                    entry.PropertyChanged += FileEntry_PropertyChanged;
                }
            }
        }
        if (e.OldItems is not null)
        {
            foreach (object? item in e.OldItems)
            {
                if (item is FileEntry entry)
                {
                    entry.PropertyChanged -= FileEntry_PropertyChanged;
                }
            }
        }
        _markSummaryDirty();
        if (!_bulkLoadingFiles)
        {
            _revalidateHosters();
            NotifySelectionStats(); // adds/removes change the footer's live count + total size
        }
    }

    [ObservableProperty]
    public partial string PackageTitle { get; set; } = string.Empty;

    /// <summary>
    /// Everything the user has added on the first step — folders that were walked and files that were
    /// picked, in the order they were added.
    /// <para>
    /// This replaced a Directory/Files MODE, where choosing a folder cleared whatever was already
    /// there. A package routinely draws from more than one place (the rips here, the artwork there),
    /// and under the old model the second choice silently discarded the first.
    /// </para>
    /// </summary>
    public ObservableCollection<UploadSource> Sources { get; } = [];

    /// <summary>True once anything has been added — the first step's empty-state hint hangs off it.</summary>
    public bool HasSources => Sources.Count > 0;

    /// <summary>
    /// The source tree the wizard's first step shows on the left: one "All files" root, a node per
    /// added folder (with its real subdirectory structure beneath), and a bucket for individually
    /// picked files. Selecting a node narrows the grid to that node and everything under it.
    /// </summary>
    public ObservableCollection<UploadTreeNode> TreeRoots { get; } = [];

    /// <summary>
    /// The node whose files the grid shows. Null (nothing selected) reads as the whole package, so an
    /// empty selection never hides everything.
    /// </summary>
    [ObservableProperty]
    public partial UploadTreeNode? SelectedNode { get; set; }

    partial void OnSelectedNodeChanged(UploadTreeNode? value) => ApplyFilter();

    /// <summary>
    /// Rebuilds the tree from <see cref="Files"/> and <see cref="Sources"/>.
    /// <para>
    /// Rebuilt wholesale rather than patched: the nodes hold nothing that isn't derivable from those
    /// two collections, so there is no state to drift, and the alternative — incrementally inserting
    /// folder chains as files arrive — is where a tree like this usually goes wrong.
    /// </para>
    /// </summary>
    private void RebuildTree()
    {
        Guid? previouslySelected = SelectedNode?.Source?.Id;

        UploadTreeNode all = new(Localizer.Instance["Wizard_Step0_TreeAllFiles"], UploadTreeNodeKind.All);

        foreach (UploadSource source in Sources)
        {
            FileEntry[] files = [.. Files.Where(f => f.SourceId == source.Id)];
            if (files.Length == 0)
            {
                continue;
            }

            if (!source.IsFolder)
            {
                // Individually picked files share one bucket: a node per file would be a tree of
                // leaves, which is just the flat list again with more indentation.
                UploadTreeNode loose = all.Children.FirstOrDefault(c => c.Kind == UploadTreeNodeKind.LooseFiles)
                    ?? AddLooseNode(all);
                loose.OwnFiles.AddRange(files);
                continue;
            }

            UploadTreeNode root = new(source.DisplayName, UploadTreeNodeKind.Folder, source);
            all.AddChild(root);

            foreach (FileEntry file in files)
            {
                // The file's own folder chain BELOW the source root, from the path on disk rather than
                // the display path (which may carry a disambiguating prefix — see AppendFiles).
                string relative = Path.GetRelativePath(source.Path, file.FullPath);
                string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                UploadTreeNode target = root;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    UploadTreeNode? next = target.Children.FirstOrDefault(
                        c => c.Kind == UploadTreeNodeKind.Folder && string.Equals(c.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                    if (next is null)
                    {
                        next = new UploadTreeNode(segments[i], UploadTreeNodeKind.Folder);
                        target.AddChild(next);
                    }

                    target = next;
                }

                target.OwnFiles.Add(file);
            }
        }

        TreeRoots.Clear();
        TreeRoots.Add(all);

        // Keep the user where they were when a rebuild is caused by something else (another folder
        // added, a file unticked). Falls back to All, which shows everything.
        SelectedNode = previouslySelected is Guid id
            ? FindBySource(all, id) ?? all
            : all;

        OnPropertyChanged(nameof(HasSources));
    }

    private static UploadTreeNode AddLooseNode(UploadTreeNode all)
    {
        UploadTreeNode loose = new(Localizer.Instance["Wizard_Step0_TreeLooseFiles"], UploadTreeNodeKind.LooseFiles);
        all.AddChild(loose);
        return loose;
    }

    private static UploadTreeNode? FindBySource(UploadTreeNode node, Guid sourceId)
    {
        if (node.Source?.Id == sourceId)
        {
            return node;
        }

        foreach (UploadTreeNode child in node.Children)
        {
            if (FindBySource(child, sourceId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Re-reads the tick state of every node holding this file, up to the root — a leaf toggle can
    /// flip a whole chain from partial to full and back.
    /// </summary>
    private void RefreshTreeChecks(FileEntry file)
    {
        foreach (UploadTreeNode root in TreeRoots)
        {
            RefreshNodeFor(root, file);
        }

        static bool RefreshNodeFor(UploadTreeNode node, FileEntry file)
        {
            bool holdsIt = node.OwnFiles.Contains(file);
            foreach (UploadTreeNode child in node.Children)
            {
                holdsIt |= RefreshNodeFor(child, file);
            }

            if (holdsIt)
            {
                node.RefreshCheckState();
            }

            return holdsIt;
        }
    }

    [ObservableProperty]
    public partial string FileFilter { get; set; } = string.Empty;

    public ObservableCollection<FileEntry> Files { get; } = [];

    private void FileEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileEntry.IsSelected))
        {
            _markSummaryDirty();
            if (!_bulkLoadingFiles)
            {
                _revalidateHosters();
                NotifySelectionStats();

                // A leaf toggle can flip a whole chain of folders between ticked, partial and clear.
                if (sender is FileEntry entry)
                {
                    RefreshTreeChecks(entry);
                }
            }
        }
    }

    /// <summary>Live count of files ticked for upload — the Step-1 footer's "Selected: N file(s)". Counts
    /// <see cref="FileEntry.IsSelected"/> regardless of filter visibility: the filter only HIDES rows, and a
    /// hidden-but-ticked file still uploads, so the footer must agree with what Finish actually queues.</summary>
    public int SelectedFileCount => Files.Count(f => f.IsSelected);

    /// <summary>Live friendly total ("2.71 GiB") of the ticked files' sizes — the Step-1 footer's
    /// "Total size:" opposite the count. Same IsSelected-only basis as <see cref="SelectedFileCount"/>.</summary>
    public string SelectedTotalSizeDisplay
        => ByteUnit.FromBytes(Files.Where(f => f.IsSelected).Sum(f => f.Size), ByteBase.Binary).ToFriendlyString();

    private void NotifySelectionStats()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedTotalSizeDisplay));
    }

    partial void OnFileFilterChanged(string value)
    {
        ApplyFilter();
    }

    /// <summary>
    /// "Add folder…" — appends each chosen folder's files (recursively) to the list. Several folders
    /// can be chosen in one dialog; each becomes its own <see cref="UploadSource"/>.
    /// </summary>
    [RelayCommand]
    private async Task AddFoldersAsync()
    {
        string[]? folders = await _dialogService.BrowseFoldersAsync(
            ResolveBrowseStart(),
            Localizer.Instance["Wizard_Step0_BrowseDialogTitle"]);

        if (folders is null || folders.Length == 0)
        {
            return;
        }

        RememberBrowsedDirectory(folders[0]);

        foreach (string folder in folders)
        {
            AddFolderSource(folder);
        }

        SeedPackageTitleFromFirstSource();
        SourcesChanged();
    }

    /// <summary>"Add files…" — appends the picked files, each as its own source row.</summary>
    [RelayCommand]
    private async Task AddFilesAsync()
    {
        string[]? picked = await _dialogService.BrowseFilesAsync(
            Localizer.Instance["Wizard_Step0_Files_BrowseDialogTitle"],
            filter: null,
            initialDirectory: ResolveBrowseStart());

        if (picked is null || picked.Length == 0)
        {
            return;
        }

        RememberBrowsedDirectory(picked[0]);

        AddFileSources(picked);
        SeedPackageTitleFromFirstSource();
        SourcesChanged();
    }

    /// <summary>
    /// Whether this platform can actually receive a dropped file, which is what decides if the
    /// step's "…or drop files and folders anywhere on this page" hint is shown at all.
    /// <para>
    /// FALSE ON LINUX, and not as a precaution: Avalonia's X11 backend implements no XDND. Verified
    /// against the shipped assemblies — Avalonia.Win32 carries a full OLE drop target
    /// (<c>OleDropTarget</c>, <c>IDropTarget</c>, <c>DROPFILES</c>) and Avalonia.Native a macOS one
    /// (<c>AvaloniaNativeDragSource</c>, <c>DndCallback</c>), while Avalonia.X11 defines no
    /// drag-drop types and, decisively, none of the <c>Xdnd*</c> atoms the protocol requires. It
    /// therefore never sets <c>XdndAware</c> on its windows, so no file manager will even offer one
    /// a drop. Same in 11.3.12, 11.3.18 and 12.0.5, so this is not waiting on a version bump.
    /// </para>
    /// <para>
    /// The drop HANDLER in the head is left wired regardless — it costs nothing when the platform
    /// never raises the event, and the feature comes back on its own if Avalonia implements XDND.
    /// Only the promise is withheld: advertising a drop that silently does nothing reads as a
    /// broken app rather than an absent feature, which is exactly how it was reported.
    /// </para>
    /// </summary>
    /// <remarks>The setter is internal so a head test can force it. CI runs the head's suite on
    /// Windows only, where this is always true — so without the seam the FALSE case, which is the
    /// entire fix, could never be exercised at all. (A misspelt binding is not the risk here:
    /// compiled bindings plus the panel's x:DataType make that a build error, verified by
    /// deliberately breaking it.) Nothing in the app writes this.</remarks>
    public bool SupportsFileDrop { get; internal set; } = !OperatingSystem.IsLinux();

    /// <summary>
    /// Where the next pick should open. A configured
    /// <see cref="AppSettings.DefaultUploadDirectory"/> always wins; blank falls back to wherever
    /// the last pick was made, and blank again to the last folder added in THIS wizard — the
    /// behaviour that predates the setting, so a first run is no worse than before. Null at the end
    /// of that chain means "no suggestion", handing the choice back to the OS.
    /// </summary>
    internal string? ResolveBrowseStart()
    {
        if (!Blank(_settings?.DefaultUploadDirectory))
        {
            return _settings!.DefaultUploadDirectory;
        }

        return Blank(_settings?.LastBrowsedFolder)
            ? Sources.LastOrDefault(s => s.IsFolder)?.Path
            : _settings!.LastBrowsedFolder;

        static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
    }

    /// <summary>
    /// Records the directory the picker was SHOWING when <paramref name="picked"/> was chosen —
    /// the parent, for both a picked file and a picked folder. Deliberately the parent and not the
    /// folder itself: reopening one level up shows the pick and its siblings, which is what makes
    /// "the next season" or "the next release" a single click. A drive root has no parent, so
    /// nothing is recorded and the previous value stands.
    /// <para>
    /// Recorded even while <see cref="AppSettings.DefaultUploadDirectory"/> is set and therefore
    /// winning: the moment that box is cleared this is what the picker falls back to, and it should
    /// not be a cold start.
    /// </para>
    /// </summary>
    private void RememberBrowsedDirectory(string picked)
    {
        if (_settings is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(picked);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        _settings.LastBrowsedFolder = directory;
        _ = PersistLastBrowsedDirectoryAsync(directory);
    }

    /// <summary>Fire-and-forget by design: the pick must not wait on a DB write, and losing the
    /// memory of one directory is not worth surfacing to the user. Logged, not swallowed.</summary>
    private async Task PersistLastBrowsedDirectoryAsync(string directory)
    {
        if (_settingRepository is null)
        {
            return;
        }

        try
        {
            await _settingRepository.UpsertAsync(SettingKey.LastBrowsedFolder, directory);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to remember the last browsed folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Files and folders dropped onto the wizard — the same append path the buttons take, so a drop
    /// dedupes against what is already listed exactly as a pick does. Paths that are neither an
    /// existing file nor an existing folder are ignored rather than reported: a drop can carry all
    /// sorts of things, and refusing the whole gesture over one of them helps nobody.
    /// </summary>
    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        List<string> files = [];
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                AddFolderSource(path);
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        if (files.Count > 0)
        {
            AddFileSources(files);
        }

        SeedPackageTitleFromFirstSource();
        SourcesChanged();
    }

    /// <summary>
    /// Removes a source and the files it contributed, leaving every other source's files — and their
    /// tick state — untouched. Removing the last source does NOT reset the package title: the user may
    /// have typed it, and re-deriving it from whatever is left would overwrite that.
    /// </summary>
    [RelayCommand]
    private void RemoveSource(UploadSource? source)
    {
        if (source is null)
        {
            return;
        }

        BulkMutateFiles(() =>
        {
            for (int i = Files.Count - 1; i >= 0; i--)
            {
                if (Files[i].SourceId == source.Id)
                {
                    Files[i].PropertyChanged -= FileEntry_PropertyChanged;
                    Files.RemoveAt(i);
                }
            }
        });

        Sources.Remove(source);
        SourcesChanged();
    }

    /// <summary>Walks one folder and appends what it finds, recording it as a source.</summary>
    private void AddFolderSource(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        UploadSource source = new(folder, isFolder: true);
        string[] found;
        try
        {
            found = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that turns unreadable mid-pick shouldn't take the wizard down with it.
            _logger.Log(this, LogType.Error, $"Couldn't read {folder}: {ex.Message}");
            return;
        }

        int added = AppendFiles(found, source, relativeTo: folder);
        if (added == 0 && Sources.Any(s => string.Equals(s.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            // Same folder added twice: everything was already listed, so there is nothing to show for
            // it and a second identical row would just be confusing.
            return;
        }

        source.FileCount = added;
        Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));
    }

    /// <summary>Adds individually-picked files, one source row each (as the Sources strip shows them).</summary>
    private void AddFileSources(IEnumerable<string> filePaths)
    {
        foreach (string filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                continue;
            }

            UploadSource source = new(filePath, isFolder: false);
            if (AppendFiles([filePath], source, relativeTo: null) == 0)
            {
                continue;   // already in the list from an earlier source
            }

            source.FileCount = 1;
            Sources.Add(source);
        }

        OnPropertyChanged(nameof(HasSources));
    }

    /// <summary>
    /// Fills the package title from the first source when the user hasn't typed one — a folder's name,
    /// or a lone file's name without its extension. Only ever fills a BLANK title.
    /// </summary>
    private void SeedPackageTitleFromFirstSource()
    {
        if (!string.IsNullOrWhiteSpace(PackageTitle) || Sources.Count == 0)
        {
            return;
        }

        UploadSource first = Sources[0];
        PackageTitle = first.IsFolder
            ? first.DisplayName
            : Path.GetFileNameWithoutExtension(first.Path);
    }

    /// <summary>Every path that changes what is in the list ends here: the tree is derived from the
    /// list, so it is rebuilt from it rather than nudged alongside it.</summary>
    private void SourcesChanged()
    {
        RebuildTree();
        ApplyFilter();
    }

    /// <summary>
    /// Appends files that aren't listed yet and returns how many were actually added.
    /// <para>
    /// <paramref name="relativeTo"/> is the folder a walked source is rooted at, so the Path column
    /// reads as the layout inside that folder. Two folders can produce the SAME relative path
    /// ("Season 1\e01.mkv" from two different rips), so a collision is prefixed with the source
    /// folder's own name — the list still says which is which. Individually picked files (no root)
    /// keep the existing same-name disambiguation.
    /// </para>
    /// </summary>
    private int AppendFiles(IEnumerable<string> filePaths, UploadSource source, string? relativeTo)
    {
        int added = 0;

        BulkMutateFiles(() =>
        {
            HashSet<string> existingPaths = new(
                Files.Select(f => f.FullPath),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> existingDisplays = new(
                Files.Select(f => f.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in filePaths)
            {
                if (existingPaths.Contains(filePath))
                {
                    continue;
                }

                FileInfo fi = new(filePath);
                string display;
                if (relativeTo is not null)
                {
                    display = Path.GetRelativePath(relativeTo, filePath);
                    if (existingDisplays.Contains(display))
                    {
                        display = Path.Combine(source.DisplayName, display);
                    }
                }
                else
                {
                    display = fi.Name;
                    if (existingDisplays.Contains(display))
                    {
                        string folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
                        display = string.Format(
                            CultureInfo.CurrentCulture,
                            Localizer.Instance["Wizard_Step1_DuplicateFilenameSuffixFormat"],
                            fi.Name,
                            folderName);
                    }
                }

                FileEntry entry = new()
                {
                    FullPath = filePath,
                    RelativePath = display,
                    FileName = fi.Name,
                    Size = fi.Length,
                    IsSelected = true,
                    SourceId = source.Id,
                };
                Files.Add(entry);
                existingPaths.Add(filePath);
                existingDisplays.Add(display);
                added++;
            }
        });

        return added;
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    /// <summary>
    /// Ticks or unticks everything through the bulk guard, so the tree's tri-state is recomputed ONCE
    /// at the end rather than per file — a leaf toggle walks its ancestors, which across a few
    /// thousand files is the difference between instant and a visible stall.
    /// </summary>
    private void SetAllSelected(bool selected)
    {
        BulkMutateFiles(() =>
        {
            foreach (FileEntry file in Files)
            {
                file.IsSelected = selected;
            }
        });

        foreach (UploadTreeNode root in TreeRoots)
        {
            RefreshSubtree(root);
        }

        static void RefreshSubtree(UploadTreeNode node)
        {
            node.RefreshCheckStateLocal();
            foreach (UploadTreeNode child in node.Children)
            {
                RefreshSubtree(child);
            }
        }
    }

    /// <summary>
    /// Removes the rows the user picked in the Files DataGrid. Bound from the Remove
    /// button and from the Delete keyboard shortcut on the grid. <paramref name="selectedItems"/>
    /// is the non-generic <see cref="System.Collections.IList"/> exposed by
    /// <c>DataGrid.SelectedItems</c>; we snapshot it before mutating <see cref="Files"/>
    /// because removing from the source collection invalidates the live SelectedItems view.
    /// </summary>
    [RelayCommand]
    private void RemoveSelectedFiles(System.Collections.IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0)
        {
            return;
        }

        FileEntry[] toRemove = [.. selectedItems.OfType<FileEntry>()];
        foreach (FileEntry file in toRemove)
        {
            Files.Remove(file);
        }
    }

    // Runs a bulk Files population with the per-item validation + footer recompute SUSPENDED, then recomputes
    // ONCE. Turns an otherwise O(files²) scan (each Add re-running both) into O(files). Re-entrancy-safe.
    private void BulkMutateFiles(Action mutate)
    {
        bool wasBulk = _bulkLoadingFiles;
        _bulkLoadingFiles = true;
        try
        {
            mutate();
        }
        finally
        {
            _bulkLoadingFiles = wasBulk;
        }

        if (!_bulkLoadingFiles)
        {
            _markSummaryDirty();
            _revalidateHosters();
            NotifySelectionStats();
        }
    }

    /// <summary>
    /// Which file rows the grid shows: those under the SELECTED tree node that also match the text
    /// filter. Applied by the head to its collection view, so a row that doesn't match is ABSENT from
    /// the view rather than present-and-collapsed.
    /// <para>
    /// That distinction is the whole point. The grid used to hide rows by setting
    /// <c>DataGridRow.IsVisible</c> false on them, which leaves zero-height rows inside the row
    /// presenter's layout — and a row re-shown after being collapsed could end up drawn over its
    /// neighbour, which is exactly what two files re-appearing from a de-selected folder looked like
    /// on screen. Filtering the view removes the possibility rather than papering over it, and it is
    /// the idiom the hoster grid and the Uploads tab already use.
    /// </para>
    /// </summary>
    public bool MatchesFileFilter(object item)
    {
        if (item is not FileEntry file)
        {
            return false;
        }

        // A null selection (nothing picked yet) means the whole package, same as the All node.
        if (_filterScope is not null && !_filterScope.Contains(file))
        {
            return false;
        }

        string filter = FileFilter.Trim();
        return filter.Length == 0 || file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Raised when the tree selection or the text filter changes; the head refreshes its view
    /// on it (the same split as the hoster grid's <see cref="WizardHostersViewModel.HosterFilterInvalidated"/>).</summary>
    public event EventHandler? FileFilterInvalidated;

    /// <summary>The selected node's files, or null for "everything". Recomputed when the selection
    /// changes rather than per row, since the predicate runs once per file on every refresh.</summary>
    private HashSet<FileEntry>? _filterScope;

    private void ApplyFilter()
    {
        _filterScope = SelectedNode is null or { Kind: UploadTreeNodeKind.All }
            ? null
            : [.. SelectedNode.AllFiles()];

        FileFilterInvalidated?.Invoke(this, EventArgs.Empty);
    }
}
