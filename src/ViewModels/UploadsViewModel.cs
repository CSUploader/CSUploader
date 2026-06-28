// <copyright file="UploadsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadsViewModel : ObservableObject, IDisposable
{
    private readonly PackageManager _packageManager;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    /// <summary>
    /// Exposed to the view's code-behind so the column-toggle menu can persist visibility
    /// via <see cref="Lib.UI.DataGridColumnVisibilityPersistence"/>. Optional in tests.
    /// </summary>
    internal SettingRepository? SettingRepo { get; }

    /// <summary>
    /// Exposed to the view's code-behind so the "Reset columns" entry can prompt via
    /// the standard opt-out confirmation flow.
    /// </summary>
    internal IDialogService DialogServiceForView { get; }

    public UploadsViewModel(PackageManager packageManager, AppSettings settings, IDialogService dialogService, SettingRepository? settingRepo = null)
    {
        _packageManager = packageManager;
        _settings = settings;
        DialogServiceForView = dialogService;
        SettingRepo = settingRepo;
        _packageManager.PackageAdded += PackageManager_PackageAdded;
        _packageManager.FileCompleted += PackageManager_FileCompleted;
        _packageManager.PackageCompleted += PackageManager_PackageCompleted;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    [ObservableProperty]
    private bool showUploadOverview = true;

    /// <summary>
    /// Whether the Upload Overview's stats row is shown beneath its title bar.
    /// Toggled by clicking the chevron next to the title; <see cref="ShowUploadOverview"/>
    /// (driven by the ✕ button and the View menu) hides the whole panel instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverviewToggleGlyph))]
    private bool isOverviewExpanded = true;

    /// <summary>
    /// ▼ when the stats row is showing, ▶ when collapsed. Bound to the chevron next
    /// to "Upload Overview".
    /// </summary>
    public string OverviewToggleGlyph => IsOverviewExpanded ? "\u25BC" : "\u25B6";

    // -- Overview field visibility toggles --

    [ObservableProperty]
    private bool showPackages = true;

    [ObservableProperty]
    private bool showLinks = true;

    [ObservableProperty]
    private bool showTotalBytes = true;

    [ObservableProperty]
    private bool showUploadspeed = true;

    [ObservableProperty]
    private bool showBytesLoaded = true;

    [ObservableProperty]
    private bool showRemainingBytes = true;

    [ObservableProperty]
    private bool showEta = true;

    [ObservableProperty]
    private bool showRunningUploads = true;

    [ObservableProperty]
    private bool showOpenConnections = true;

    [ObservableProperty]
    private bool showFinishedLinks;

    [ObservableProperty]
    private bool showSkippedLinks;

    [ObservableProperty]
    private bool showFailedLinks;

    public ObservableCollection<Package> Packages { get; } = [];

    /// <summary>
    /// Flat list of rows for the DataGrid: packages interleaved with their files
    /// (when the package is expanded). Single shared column widths across all rows.
    /// </summary>
    public ObservableCollection<object> VisibleRows { get; } = [];

    /// <summary>
    /// Filter text bound to the JD2-style filter bar at the bottom of the Uploads tab.
    /// Filters by package name (for package rows) and by file name (for file rows).
    /// </summary>
    [ObservableProperty]
    private string filterText = string.Empty;

    /// <summary>
    /// Wraps <see cref="VisibleRows"/> with a name-filter applied on top of <see cref="FilterText"/>.
    /// Bound by the DataGrid as its ItemsSource.
    /// </summary>
    public ICollectionView FilteredRows
    {
        get
        {
            if (field is null)
            {
                field = CollectionViewSource.GetDefaultView(VisibleRows);
                field.Filter = MatchesFilter;
            }

            return field;
        }
    }

    private bool MatchesFilter(object item)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        string needle = FilterText.Trim();
        string? haystack = item switch
        {
            Package package => package.Name,
            PackageFile file => file.Name,
            _ => null,
        };
        return haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnFilterTextChanged(string value) => FilteredRows.Refresh();

    // -- Summary properties for status bar --

    public int PackageCount => Packages.Count;

    public int FileCount => Packages.Sum(p => p.Count());

    public string TotalBytes => ByteUnit.FromBytes(
        Packages.Sum(p => p.Size ?? 0), ByteBase.Binary).ToFriendlyString();

    public string BytesLoaded => ByteUnit.FromBytes(
        Packages.Sum(p => p.BytesLoaded ?? 0), ByteBase.Binary).ToFriendlyString();

    public string RemainingBytes => ByteUnit.FromBytes(
        Packages.Sum(p => p.BytesRemaining ?? 0), ByteBase.Binary).ToFriendlyString();

    public string UploadSpeed
    {
        get
        {
            long speed = Packages.Sum(p => p.Speed ?? 0);
            return speed > 0
                ? ByteUnit.FromBytes(speed, ByteBase.Binary).ToFriendlyString() + "/s"
                : "0 B/s";
        }
    }

    public int RunningUploads => Packages.Sum(p =>
        p.Count(pf => pf.State is FileState.Uploading));

    public int FinishedLinks => Packages.Sum(p =>
        p.Count(pf => pf.State == FileState.Completed));

    public int SkippedLinks => Packages.Sum(p =>
        p.Count(pf => pf.State == FileState.Cancelled));

    public int FailedLinks => Packages.Sum(p =>
        p.Count(pf => pf.State == FileState.Failed));

    public string Eta
    {
        get
        {
            long remaining = Packages.Sum(p => p.BytesRemaining ?? 0);
            long speed = Packages.Sum(p => p.Speed ?? 0);
            if (speed <= 0 || remaining <= 0)
            {
                return "~";
            }

            var eta = TimeSpan.FromSeconds(remaining / (double)speed);
            return eta.Hours > 0
                ? eta.ToString(@"h\h\:mm\m\:ss\s", CultureInfo.InvariantCulture)
                : eta.Minutes > 0
                    ? eta.ToString(@"mm\m\:ss\s", CultureInfo.InvariantCulture)
                    : eta.ToString(@"ss\s", CultureInfo.InvariantCulture);
        }
    }

    // -- Commands --

    [RelayCommand]
    private void Start() => _packageManager.StartPackages();

    [RelayCommand]
    private void Pause() => _packageManager.PausePackages(resume: _packageManager.IsPaused);

    [RelayCommand]
    private void Stop() => _packageManager.StopPackages();

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

    /// <summary>
    /// Right-click → Move submenu. Negative delta = sooner (toward #1). Operates on all selected
    /// file rows as a block (preserving their relative order).
    /// </summary>
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

    private void RebuildVisibleRows()
    {
        // Snapshot expansion state, rebuild VisibleRows in package order, restore expansion.
        VisibleRows.Clear();
        foreach (Package package in Packages)
        {
            VisibleRows.Add(package);
            if (package.IsExpanded)
            {
                foreach (PackageFile file in package)
                {
                    VisibleRows.Add(file);
                }
            }
        }

        FilteredRows.Refresh();
    }

    [RelayCommand]
    private void Retry(object? item)
    {
        if (item is not null)
        {
            _packageManager.StartPackage(item);
        }
    }

    /// <summary>
    /// Starts every selected row. The Uploads context menu's "Start" item binds here
    /// (passing the grid's SelectedItems) so a multi-row selection all starts — previously
    /// it bound to the single SelectedItem and only the focused row started.
    /// </summary>
    [RelayCommand]
    private void StartSelected(IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0)
        {
            return;
        }

        // Snapshot — StartPackage mutates file state, and the live SelectedItems collection
        // can shift underneath us as rows transition. Selecting a package and some of its
        // files is harmless: StartPackage is idempotent (ForceQueueIfStartable skips files
        // already queued/running).
        foreach (object item in selectedItems.Cast<object>().ToArray())
        {
            _packageManager.StartPackage(item);
        }
    }

    /// <summary>
    /// Force-starts every selected row — launches each upload immediately past the concurrency
    /// limit instead of queuing it to wait for a free slot. Mirrors <see cref="StartSelected"/>'s
    /// multi-row snapshot semantics; <see cref="PackageManager.ForceStartPackage"/> skips files
    /// already running or completed, so a mixed selection is safe.
    /// </summary>
    [RelayCommand]
    private void ForceStartSelected(IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0)
        {
            return;
        }

        object[] items = [.. selectedItems.Cast<object>()];

        // Re-uploading an already-completed file spends bandwidth/quota on a file that uploaded
        // successfully — always confirm first (a plain prompt, no opt-out, so it can never happen
        // by accident). Non-completed files force-start without a prompt.
        int completedCount = DistinctCompletedCount(items);
        if (completedCount > 0)
        {
            string msg = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_ForceStart_Reupload_Format"], completedCount);
            if (!DialogServiceForView.ShowConfirmation(msg, Localizer.Instance["Uploads_ForceStart_Reupload_Title"]))
            {
                return;
            }
        }

        foreach (object item in items)
        {
            _packageManager.ForceStartPackage(item);
        }
    }

    /// <summary>Completed-file count across the selection, de-duplicated so a selected child whose
    /// package is ALSO selected is counted once (via the package) rather than twice — mirrors
    /// <see cref="RemoveSelected"/>'s package/loose-file split. Used only to size the re-upload
    /// confirmation prompts (never to drive what actually runs). Internal for unit testing.</summary>
    internal static int DistinctCompletedCount(object[] items)
    {
        HashSet<Package> packages = [.. items.OfType<Package>()];
        int inPackages = packages.Sum(p => p.Count(f => f.State == FileState.Completed));
        int looseCompleted = items
            .OfType<PackageFile>()
            .Count(f => !packages.Contains(f.Package) && f.State == FileState.Completed);
        return inPackages + looseCompleted;
    }

    /// <summary>Snapshots the live grid <c>SelectedItems</c> into a stable array — the context-menu
    /// commands all operate on the WHOLE selection, and the live collection can shift under us as rows
    /// transition. A single right-clicked row arrives as a one-element list, so single-select is just
    /// the N=1 case.</summary>
    private static object[] Snapshot(IList? selectedItems)
        => selectedItems is null ? [] : [.. selectedItems.Cast<object>()];

    [RelayCommand]
#pragma warning disable CA1822 // Must be instance method for RelayCommand
    private void StopSelected(IList? selectedItems)
#pragma warning restore CA1822
    {
        foreach (object item in Snapshot(selectedItems))
        {
            PackageManager.StopPackage(item);
        }
    }

    [RelayCommand]
    private void SetSpeedLimit(IList? selectedItems)
    {
        object[] items = Snapshot(selectedItems);
        if (items.Length == 0)
        {
            return;
        }

        // Default the dialog from the primary (first) row's effective limit, then apply the chosen
        // value to EVERY selected row — one prompt, applied across the whole selection.
        (int? currentLimit, int? inheritedLimit) = items[0] switch
        {
            Package package => (package.SpeedLimitKBps, _settings.SpeedLimit is > 0 ? _settings.SpeedLimit : (int?)null),
            PackageFile file => (file.SpeedLimitKBps, file.Package.SpeedLimitKBps ?? (_settings.SpeedLimit is > 0 ? _settings.SpeedLimit : (int?)null)),
            _ => ((int?)null, (int?)null),
        };

        int? displayLimit = currentLimit ?? inheritedLimit;

        var dialog = new Views.SpeedLimitDialog(displayLimit)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (object item in items)
        {
            if (item is Package pkg)
            {
                pkg.SpeedLimitKBps = dialog.Result;
            }
            else if (item is PackageFile pf)
            {
                pf.SpeedLimitKBps = dialog.Result;
            }
        }
    }

    [RelayCommand]
    private static void OpenSourceDirectory(IList? selectedItems)
    {
        object[] items = Snapshot(selectedItems);
        if (items.Length == 0)
        {
            return;
        }

        // A single file → open its folder with the file highlighted (explorer /select).
        if (items.Length == 1 && items[0] is PackageFile file
            && TryBuildExplorerSelectArgument(file.Path, file.Name, File.Exists) is string selectArg)
        {
            Process.Start("explorer.exe", selectArg);
            return;
        }

        // Multiple rows (or a package / since-moved file): open each DISTINCT source folder once.
        // Opening one /select window per file would spam the desktop when the selection shares a
        // folder (the common case — all files of a package live in one directory).
        foreach (string dir in SelectedDistinctDirectories(items, Directory.Exists))
        {
            Process.Start("explorer.exe", dir);
        }
    }

    /// <summary>The distinct, existing source directories of the selected rows, in selection order.
    /// Pure + internal (existence injected) so the open-folders behavior is unit-testable without
    /// touching the disk or launching Explorer.</summary>
    internal static IReadOnlyList<string> SelectedDistinctDirectories(IList? selectedItems, Func<string, bool> dirExists)
        => [.. Snapshot(selectedItems)
            .Select(DirectoryOf)
            .Where(d => !string.IsNullOrEmpty(d) && dirExists(d))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string? DirectoryOf(object item) => item switch
    {
        Package pkg => pkg.Path,
        PackageFile pf => pf.Path,
        _ => null,
    };

    /// <summary>
    /// Builds the <c>explorer.exe</c> argument that opens a file's folder with that file selected
    /// (<c>/select,"&lt;path&gt;"</c>), or null when the directory/name is missing or the file no
    /// longer exists (so the caller falls back to just opening the folder). The <c>/select</c> comma
    /// form and the quotes around the path are both required by Explorer. Pure + testable —
    /// <paramref name="fileExists"/> is injected so tests don't touch the disk.
    /// </summary>
    internal static string? TryBuildExplorerSelectArgument(string? directory, string? fileName, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        string fullPath = Path.Combine(directory, fileName);
        return fileExists(fullPath) ? $"/select,\"{fullPath}\"" : null;
    }

    [RelayCommand]
    private static void SkipUpload(IList? selectedItems)
    {
        foreach (object item in Snapshot(selectedItems))
        {
            PackageManager.StopPackage(item);
        }
    }

    [RelayCommand]
    private void ResetFile(IList? selectedItems)
    {
        object[] items = Snapshot(selectedItems);
        if (items.Length == 0)
        {
            return;
        }

        // Resetting a Failed/Cancelled file is the cheap recovery path the user expects on
        // right-click. Resetting a Completed file silently undoes a successful upload —
        // it has to re-hash a (possibly multi-GB) file and re-upload it. Confirm before
        // doing that (once for the whole selection), but skip the prompt when no completed
        // file is in scope.
        int completedCount = DistinctCompletedCount(items);
        if (completedCount > 0)
        {
            string msg = items.Length == 1
                ? items[0] switch
                {
                    Package p => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Reset_Package_Format"], p.Name, completedCount),
                    PackageFile f => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Reset_File_Format"], f.Name),
                    _ => string.Empty,
                }
                : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Reset_Multi_Format"], items.Length, completedCount);

            if (!DialogServiceForView.ShowOptOutConfirmation(ConfirmationKeys.ResetCompletedUpload, msg, Localizer.Instance["Uploads_Reset_Title"]))
            {
                return;
            }
        }

        foreach (object item in items)
        {
            _packageManager.ResetPackage(item);
        }
    }

    /// <summary>
    /// Multi-select removal — bound to the Delete key, the toolbar X button, and the context menu.
    /// Shows a single confirmation for the whole selection. If a Package and one of its
    /// Files are both selected, removing the Package implicitly handles its files, so
    /// we de-duplicate to avoid double-removal.
    /// </summary>
    [RelayCommand]
    private void RemoveSelected(IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0)
        {
            return;
        }

        // Snapshot to avoid mutation surprises if the live SelectedItems collection
        // shifts while we iterate.
        object[] items = [.. selectedItems.Cast<object>()];
        Package[] packages = [.. items.OfType<Package>()];
        HashSet<Package> packageSet = [.. packages];
        PackageFile[] looseFiles = [.. items.OfType<PackageFile>().Where(f => !packageSet.Contains(f.Package))];

        int totalFiles = packages.Sum(p => p.Count()) + looseFiles.Length;
        string msg = (packages.Length, looseFiles.Length) switch
        {
            (1, 0) => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Remove_Package_Format"], packages[0].Name, packages[0].Count()),
            (0, 1) => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Remove_File_Format"], looseFiles[0].Name),
            (_, 0) => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Remove_PackagesOnly_Format"], packages.Length, totalFiles),
            (0, _) => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Remove_FilesOnly_Format"], looseFiles.Length),
            _ => string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploads_Remove_PackagesAndFiles_Format"], packages.Length, looseFiles.Length, totalFiles),
        };

        if (!DialogServiceForView.ShowOptOutConfirmation(ConfirmationKeys.RemoveUploadPackageOrFile, msg, Localizer.Instance["Uploads_Remove_Title"]))
        {
            return;
        }

        foreach (Package package in packages)
        {
            PackageFile[] files = [.. package];
            _packageManager.RemovePackage(package);
            RemovePackageFromView(package, files);
        }

        foreach (PackageFile file in looseFiles)
        {
            // Removing a file that was its package's last one prunes the empty package too, so a
            // package with a single file disappears entirely when that file is removed (the bug:
            // it used to leave an empty package row behind).
            RemoveFileAndPruneEmptyPackage(file);
        }
    }

    /// <summary>
    /// Currently focused row (Package or PackageFile). Driven from the DataGrid's
    /// SelectedItem so the per-column "Copy" submenu can find the row even though
    /// each MenuItem only carries a single CommandParameter (the column key).
    /// </summary>
    [ObservableProperty]
    private object? selectedRow;

    /// <summary>
    /// The full multi-row selection (Package/PackageFile), snapshotted by the view when the
    /// context menu opens, so the per-column "Copy" commands act on every selected row instead
    /// of only the primary <see cref="SelectedRow"/>.
    /// </summary>
    public IReadOnlyList<object> SelectedRows { get; set; } = [];

    /// <summary>
    /// Opens the <c>FileUrl</c> of every selected <see cref="PackageFile"/> row in the user's default
    /// browser (distinct URLs only, so duplicates don't open twice). Package rows aggregate no URL, so
    /// they're skipped; the menu item is enabled only when at least one selected row has a URL.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenUrl))]
    private static void OpenUrl(IList? selectedItems)
    {
        foreach (string url in SelectedDistinctUrls(selectedItems))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort: the URL came from the hoster API and Process.Start rarely
                // fails on a valid http(s) link. A failure here shouldn't crash the UI.
            }
        }
    }

    /// <summary>The distinct, non-empty <c>FileUrl</c>s of the selected <see cref="PackageFile"/> rows,
    /// in selection order. Package rows have none. Pure + internal so the open-all-URLs behavior is
    /// unit-testable without launching a browser.</summary>
    internal static IReadOnlyList<string> SelectedDistinctUrls(IList? selectedItems)
        => [.. Snapshot(selectedItems)
            .OfType<PackageFile>()
            .Select(f => f.FileUrl)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)];

    internal static bool CanOpenUrl(IList? selectedItems) =>
        selectedItems is not null
        && selectedItems.Cast<object>().OfType<PackageFile>().Any(f => !string.IsNullOrEmpty(f.FileUrl));

    /// <summary>
    /// Copies the value of <paramref name="columnKey"/> from <see cref="SelectedRow"/>
    /// to the clipboard. Column keys mirror the resx <c>Uploads_Col_*</c> suffix so
    /// XAML can drive the submenu without a separate enum.
    /// </summary>
    [RelayCommand]
    private void CopyColumn(string? columnKey)
    {
        if (BuildColumnCopyText(columnKey) is not { } text)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard.SetText can throw on rare contention with another app —
            // swallow rather than crash the UI thread for a copy operation.
        }
    }

    /// <summary>
    /// Builds the clipboard payload for a per-column copy: that column's value for every row in
    /// <see cref="SelectedRows"/> (blank values skipped), newline-joined; falls back to the
    /// primary <see cref="SelectedRow"/>. Null when there's nothing to copy. Separated from
    /// <see cref="CopyColumnCommand"/> so the value logic is unit-testable without the clipboard.
    /// </summary>
    internal string? BuildColumnCopyText(string? columnKey)
    {
        if (string.IsNullOrEmpty(columnKey))
        {
            return null;
        }

        IReadOnlyList<object> rows = SelectedRows.Count > 0
            ? SelectedRows
            : (SelectedRow is { } only ? [only] : []);

        string[] values = [.. rows
            .Select(r => ColumnValueExtractor.Extract(r, columnKey, isUploadsTab: true))
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()];

        return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
    }

    private void RemovePackageFromView(Package package, IEnumerable<PackageFile> files)
    {
        package.PropertyChanged -= Package_PropertyChanged;
        package.PackageFilesAdded -= Package_FilesAdded;
        foreach (PackageFile file in files)
        {
            VisibleRows.Remove(file);
        }

        VisibleRows.Remove(package);
        Packages.Remove(package);
    }

    private void RemovePackageFromView(Package package) => RemovePackageFromView(package, package);

    /// <summary>
    /// Removes a single file from the manager and the view, and — if that was its package's last
    /// file — removes the now-empty (and meaningless) package too. Shared by manual removal
    /// (<see cref="RemoveSelected"/>) and auto-remove-on-complete (<see cref="PackageManager_FileCompleted"/>)
    /// so the "don't leave an empty package behind" rule can't drift between the two paths.
    /// </summary>
    private void RemoveFileAndPruneEmptyPackage(PackageFile file)
    {
        Package package = file.Package;
        _packageManager.RemovePackage(file);
        VisibleRows.Remove(file);

        if (package.Count() == 0)
        {
            _packageManager.RemovePackage(package);
            RemovePackageFromView(package, []);
        }
    }

    private void PackageManager_FileCompleted(object? sender, PackageFile file)
    {
        // Immediately mode: drop this single file from the Uploads tab the moment it
        // succeeds. Other modes either ignore per-file events (Never, AtStartup) or
        // wait for the whole package (WhenPackageIsReady, handled below).
        if (_settings.RemoveFinishedUploads != RemoveFinishedUploadsMode.Immediately
            || file.State != FileState.Completed)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RemoveFileAndPruneEmptyPackage(file));
    }

    private void PackageManager_PackageCompleted(object? sender, Package package)
    {
        // WhenPackageIsReady mode: remove the package once every file in it succeeded.
        // Packages with any failure stay visible so the user notices.
        if (_settings.RemoveFinishedUploads != RemoveFinishedUploadsMode.WhenPackageIsReady)
        {
            return;
        }

        foreach (PackageFile f in package)
        {
            if (f.State != FileState.Completed)
            {
                return;
            }
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            PackageFile[] files = [.. package];
            _packageManager.RemovePackage(package);
            RemovePackageFromView(package, files);
        });
    }

    private void PackageManager_PackageAdded(object? sender, PackageAddedEventArgs e)
    {
        if (e.Packages is null)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (Package package in e.Packages)
            {
                if (!Packages.Contains(package))
                {
                    Packages.Add(package);
                    package.PropertyChanged += Package_PropertyChanged;
                    package.PackageFilesAdded += Package_FilesAdded;
                    AddPackageToVisibleRows(package);
                }
            }
        });
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Package.IsExpanded) && sender is Package package)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (package.IsExpanded)
                {
                    InsertPackageFiles(package);
                }
                else
                {
                    RemovePackageFiles(package);
                }
            });
        }
    }

    private void Package_FilesAdded(object? sender, PackageAddedEventArgs e)
    {
        if (sender is not Package package)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (package.IsExpanded)
            {
                // Remove and re-add files to reflect any changes
                RemovePackageFiles(package);
                InsertPackageFiles(package);
            }
        });
    }

    private void AddPackageToVisibleRows(Package package)
    {
        VisibleRows.Add(package);
        if (package.IsExpanded)
        {
            InsertPackageFiles(package);
        }
    }

    private void InsertPackageFiles(Package package)
    {
        int insertIndex = VisibleRows.IndexOf(package) + 1;
        if (insertIndex <= 0)
        {
            return;
        }

        // Insert after any existing file rows for this package (idempotent)
        foreach (PackageFile file in package)
        {
            if (!VisibleRows.Contains(file))
            {
                VisibleRows.Insert(insertIndex++, file);
            }
        }
    }

    private void RemovePackageFiles(Package package)
    {
        foreach (PackageFile file in package)
        {
            VisibleRows.Remove(file);
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Have each Package (and its files) raise PropertyChanged for display props.
        // This updates cells in place without affecting row state.
        foreach (object row in VisibleRows)
        {
            if (row is Package pkg)
            {
                pkg.NotifyDisplayPropertiesChanged();
            }
            else if (row is PackageFile file)
            {
                file.NotifyDisplayPropertiesChanged();
            }
        }

        // Refresh summary stats
        OnPropertyChanged(nameof(PackageCount));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(BytesLoaded));
        OnPropertyChanged(nameof(RemainingBytes));
        OnPropertyChanged(nameof(UploadSpeed));
        OnPropertyChanged(nameof(RunningUploads));
        OnPropertyChanged(nameof(Eta));
        OnPropertyChanged(nameof(FinishedLinks));
        OnPropertyChanged(nameof(SkippedLinks));
        OnPropertyChanged(nameof(FailedLinks));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _refreshTimer.Stop();
            _packageManager.PackageAdded -= PackageManager_PackageAdded;
            _packageManager.FileCompleted -= PackageManager_FileCompleted;
            _packageManager.PackageCompleted -= PackageManager_PackageCompleted;
        }

        _disposed = true;
    }
}
