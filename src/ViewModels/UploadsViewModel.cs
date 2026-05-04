// <copyright file="UploadsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadsViewModel : ObservableObject, IDisposable
{
    private readonly PackageManager _packageManager;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public UploadsViewModel(PackageManager packageManager, AppSettings settings, IDialogService dialogService)
    {
        _packageManager = packageManager;
        _settings = settings;
        _dialogService = dialogService;
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

    private ICollectionView? _filteredRowsView;

    /// <summary>
    /// Wraps <see cref="VisibleRows"/> with a name-filter applied on top of <see cref="FilterText"/>.
    /// Bound by the DataGrid as its ItemsSource.
    /// </summary>
    public ICollectionView FilteredRows
    {
        get
        {
            if (_filteredRowsView is null)
            {
                _filteredRowsView = CollectionViewSource.GetDefaultView(VisibleRows);
                _filteredRowsView.Filter = MatchesFilter;
            }

            return _filteredRowsView;
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

    /// <summary>
    /// Moves the package owning the given row up in the list. No-op if the row is the
    /// first package or no package can be derived from it.
    /// </summary>
    [RelayCommand]
    private void MoveUp(object? item)
    {
        Package? target = ResolveOwningPackage(item);
        if (target is null)
        {
            return;
        }

        int index = Packages.IndexOf(target);
        if (index <= 0)
        {
            return;
        }

        Packages.Move(index, index - 1);
        RebuildVisibleRows();
    }

    [RelayCommand]
    private void MoveDown(object? item)
    {
        Package? target = ResolveOwningPackage(item);
        if (target is null)
        {
            return;
        }

        int index = Packages.IndexOf(target);
        if (index < 0 || index >= Packages.Count - 1)
        {
            return;
        }

        Packages.Move(index, index + 1);
        RebuildVisibleRows();
    }

    private static Package? ResolveOwningPackage(object? item) => item switch
    {
        Package p => p,
        PackageFile f => f.Package,
        _ => null,
    };

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

    [RelayCommand]
#pragma warning disable CA1822 // Must be instance method for RelayCommand
    private void StopSelected(object? item)
#pragma warning restore CA1822
    {
        if (item is not null)
        {
            PackageManager.StopPackage(item);
        }
    }

    [RelayCommand]
#pragma warning disable CA1822
    private void SetSpeedLimit(object? item)
#pragma warning restore CA1822
    {
        int? currentLimit;
        int? inheritedLimit;
        switch (item)
        {
            case Package package:
                currentLimit = package.SpeedLimitKBps;
                inheritedLimit = _settings.SpeedLimit is > 0 ? _settings.SpeedLimit : null;
                break;
            case PackageFile file:
                currentLimit = file.SpeedLimitKBps;
                inheritedLimit = file.Package.SpeedLimitKBps
                    ?? (_settings.SpeedLimit is > 0 ? _settings.SpeedLimit : null);
                break;
            default:
                return;
        }

        int? displayLimit = currentLimit ?? inheritedLimit;

        var dialog = new Views.SpeedLimitDialog(displayLimit)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() == true)
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
    private static void OpenSourceDirectory(object? item)
    {
        string? dir = item switch
        {
            Package pkg => pkg.SaveFrom,
            PackageFile file => file.SaveFrom,
            _ => null,
        };

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
    }

    [RelayCommand]
    private static void SkipUpload(object? item)
    {
        if (item is not null)
        {
            PackageManager.StopPackage(item);
        }
    }

    [RelayCommand]
    private void ResetFile(object? item)
    {
        if (item is not null)
        {
            _packageManager.ResetPackage(item);
        }
    }

    [RelayCommand]
    private void Remove(object? item)
    {
        if (item is null)
        {
            return;
        }

        string msg = item switch
        {
            Package p => $"Remove package '{p.Name}' and its {p.Count()} file(s)?",
            PackageFile f => $"Remove '{f.Name}' from the upload list?",
            _ => "Remove this item?",
        };
        if (!_dialogService.ShowOptOutConfirmation(ConfirmationKeys.RemoveUploadPackageOrFile, msg, "Remove"))
        {
            return;
        }

        if (item is Package package)
        {
            // Snapshot the files *before* telling the manager to remove the package,
            // because PackageManager.RemovePackage clears the package's internal list
            // and we'd otherwise leave orphan rows in VisibleRows.
            PackageFile[] files = [.. package];
            _packageManager.RemovePackage(item);
            RemovePackageFromView(package, files);
        }
        else
        {
            _packageManager.RemovePackage(item);
            if (item is PackageFile file)
            {
                VisibleRows.Remove(file);
            }
        }
    }

    /// <summary>
    /// Multi-select removal — bound to the Delete key and the toolbar X button.
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
            (1, 0) => $"Remove package '{packages[0].Name}' and its {packages[0].Count()} file(s)?",
            (0, 1) => $"Remove '{looseFiles[0].Name}' from the upload list?",
            (_, 0) => $"Remove {packages.Length} package(s) ({totalFiles} file(s))?",
            (0, _) => $"Remove {looseFiles.Length} file(s) from the upload list?",
            _ => $"Remove {packages.Length} package(s) and {looseFiles.Length} file(s) ({totalFiles} item(s) total)?",
        };

        if (!_dialogService.ShowOptOutConfirmation(ConfirmationKeys.RemoveUploadPackageOrFile, msg, "Remove"))
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
            _packageManager.RemovePackage(file);
            VisibleRows.Remove(file);
        }
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

    private void RemovePackageFromView(Package package)
    {
        RemovePackageFromView(package, package);
    }

    private void PackageManager_FileCompleted(object? sender, PackageFile file)
    {
        if (!_settings.AutoRemoveCompletedFiles || file.State != FileState.Completed)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Drop this row; PackageCompleted below handles whole-package removal.
            Package package = file.Package;
            _packageManager.RemovePackage(file);
            VisibleRows.Remove(file);

            // If the package just became empty, clean it up too.
            if (package.Count() == 0)
            {
                _packageManager.RemovePackage(package);
                RemovePackageFromView(package, []);
            }
        });
    }

    private void PackageManager_PackageCompleted(object? sender, Package package)
    {
        if (!_settings.AutoRemoveCompletedPackages)
        {
            return;
        }

        // Only auto-remove if every file was successful — leave packages with failures visible.
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
        AutoRemoveFinishedPackages();

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

    private void AutoRemoveFinishedPackages()
    {
        RemoveFinishedUploadsMode mode = _settings.RemoveFinishedUploads;
        if (mode == RemoveFinishedUploadsMode.Never)
        {
            return;
        }

        TimeSpan? threshold = mode switch
        {
            RemoveFinishedUploadsMode.Immediately => TimeSpan.Zero,
            RemoveFinishedUploadsMode.AfterOneHour => TimeSpan.FromHours(1),
            RemoveFinishedUploadsMode.AfterOneDay => TimeSpan.FromDays(1),
            _ => null,
        };

        if (threshold is null)
        {
            return;
        }

        DateTime now = DateTime.Now;
        Package[] toRemove = [.. Packages.Where(p => IsPackageFinished(p) && PackageFinishedAge(p, now) >= threshold.Value)];
        foreach (Package package in toRemove)
        {
            _packageManager.RemovePackage(package);
            RemovePackageFromView(package);
        }
    }

    private static bool IsPackageFinished(Package package)
    {
        bool anyFile = false;
        foreach (PackageFile file in package)
        {
            anyFile = true;
            if (file.State != FileState.Completed)
            {
                return false;
            }
        }

        return anyFile;
    }

    private static TimeSpan PackageFinishedAge(Package package, DateTime now)
    {
        DateTime? latest = null;
        foreach (PackageFile file in package)
        {
            if (file.FinishedDate is { } finished && (latest is null || finished > latest))
            {
                latest = finished;
            }
        }

        return latest is null ? TimeSpan.Zero : now - latest.Value;
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
