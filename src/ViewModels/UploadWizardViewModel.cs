// <copyright file="UploadWizardViewModel.cs" company="CSUploader">
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
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;

namespace CSUploader.ViewModels;

public partial class UploadWizardViewModel : ObservableObject
{
    private readonly PackageManager packageManager;
    private readonly FileHosterLoginRepository fileHosterLoginRepository;
    private readonly IDialogService dialogService;
    private readonly IAppLogger logger;
    private readonly AppSettings settings;

    // Pipeline registry is optional so existing test fixtures that exercise non-validation
    // flows don't need to construct one. When null, no per-hoster limit validation runs
    // — the pipeline still pre-checks file size at upload time as the safety net.
    private readonly IFileHosterRegistry? _fileHosterRegistry;
    private static readonly List<FileHosterSelectionViewModel> _stickyHosters = [];

    public UploadWizardViewModel(
        PackageManager packageManager,
        FileHosterLoginRepository fileHosterLoginRepository,
        IDialogService dialogService,
        IAppLogger logger,
        AppSettings settings,
        IFileHosterRegistry? fileHosterRegistry = null)
    {
        this.packageManager = packageManager;
        this.fileHosterLoginRepository = fileHosterLoginRepository;
        this.dialogService = dialogService;
        this.logger = logger;
        this.settings = settings;
        _fileHosterRegistry = fileHosterRegistry;

        // Hook collection-changed once: any new entry into Files / FileHosters has its
        // PropertyChanged subscribed so validation auto-refreshes on selection toggles,
        // regardless of which code path added it.
        Files.CollectionChanged += Files_CollectionChanged;
        FileHosters.CollectionChanged += FileHosters_CollectionChanged;
    }

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
        RecomputeHosterValidation();
    }

    private void FileHosters_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (object? item in e.NewItems)
            {
                if (item is FileHosterSelectionViewModel h)
                {
                    h.PropertyChanged += Hoster_PropertyChanged;
                }
            }
        }
        if (e.OldItems is not null)
        {
            foreach (object? item in e.OldItems)
            {
                if (item is FileHosterSelectionViewModel h)
                {
                    h.PropertyChanged -= Hoster_PropertyChanged;
                }
            }
        }
        RecomputeHosterValidation();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectoryMode))]
    [NotifyPropertyChangedFor(nameof(IsFilesMode))]
    private UploadWizardMode mode;

    public bool IsDirectoryMode => Mode == UploadWizardMode.Directory;

    public bool IsFilesMode => Mode == UploadWizardMode.Files;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    private int currentStep;

    [ObservableProperty]
    private string directoryPath = string.Empty;

    [ObservableProperty]
    private string packageTitle = string.Empty;

    [ObservableProperty]
    private string fileFilter = string.Empty;

    public ObservableCollection<FileEntry> Files { get; } = [];

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduledMode))]
    private UploadStartMode startMode = UploadStartMode.Immediately;

    [ObservableProperty]
    private DateTime scheduledDate = DateTime.Now.Date.AddDays(1);

    [ObservableProperty]
    private string scheduledTime = "00:00";

    public bool IsScheduledMode => StartMode == UploadStartMode.Scheduled;

    public bool CanGoBack => CurrentStep > 0;

    /// <summary>
    /// Disables the Next button on the hoster-selection step (CurrentStep==1) when a
    /// hoster's declared limits are violated in a way the user must resolve manually —
    /// either too many files for the package, or every used hoster has zero files within
    /// its size limit. Size warnings on their own are informational (oversized files are
    /// dropped at upload time) and don't block.
    /// </summary>
    public bool CanGoNext => CurrentStep != 1 || !_hasHardBlock;

    public bool IsLastStep => CurrentStep == 2;

    /// <summary>
    /// Human-readable list of currently-violated hoster limits (e.g. "BRupload: 35 files
    /// selected, limit is 30"). Bound to the wizard's step-1 warning panel; empty when
    /// no constraints are violated. Always-empty if no <see cref="IFileHosterRegistry"/>
    /// was injected.
    /// </summary>
    public ObservableCollection<string> HosterValidationWarnings { get; } = [];

    public bool HasHosterValidationWarnings => HosterValidationWarnings.Count > 0;

    /// <summary>
    /// True when validation found a violation the user must resolve manually before
    /// proceeding (e.g. count over limit, or every used hoster has zero eligible files).
    /// Size violations are normally informational — oversized files are silently skipped
    /// at upload time — so they don't set this flag unless they'd leave the package
    /// completely empty.
    /// </summary>
    private bool _hasHardBlock;

    /// <summary>
    /// Recomputes <see cref="HosterValidationWarnings"/> by walking each enabled hoster's
    /// declared limits and comparing against the current file selection. Size violations
    /// are reported with a filename list so the user knows exactly which files will be
    /// skipped; they only block Next when there's nothing left to upload anywhere. Count
    /// violations always block — the user has to decide which files to drop. Idempotent.
    /// No-op when no registry was injected (test fixtures).
    /// </summary>
    private void RecomputeHosterValidation()
    {
        HosterValidationWarnings.Clear();
        _hasHardBlock = false;

        if (_fileHosterRegistry is null)
        {
            OnPropertyChanged(nameof(HasHosterValidationWarnings));
            OnPropertyChanged(nameof(CanGoNext));
            return;
        }

        FileEntry[] selected = [.. Files.Where(f => f.IsSelected)];
        bool anyUsedHoster = false;
        bool anyHosterCanUploadSomething = false;

        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use)
            {
                continue;
            }

            anyUsedHoster = true;
            IFileHosterPipeline? pipeline = _fileHosterRegistry.Find(hoster.FileHosterName);

            // Unknown hoster — no declared limits, so it accepts every selected file.
            if (pipeline is null)
            {
                if (selected.Length > 0) anyHosterCanUploadSomething = true;
                continue;
            }

            int eligibleForThisHoster = selected.Length;

            if (pipeline.MaxFileSize is long maxBytes)
            {
                List<string> oversizedNames = [];
                foreach (FileEntry f in selected)
                {
                    if (f.Size > maxBytes) oversizedNames.Add(f.FileName);
                }
                if (oversizedNames.Count > 0)
                {
                    string sizeStr = ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString();
                    // Render the file list one per line so the warning panel can scroll
                    // when the user has many oversized files. The resx string already
                    // ends with a newline after the colon (see Wizard_Hoster_FileTooLarge_Format).
                    string fileList = string.Join("\n", oversizedNames);
                    HosterValidationWarnings.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        Localizer.Instance["Wizard_Hoster_FileTooLarge_Format"],
                        hoster.FileHosterName,
                        sizeStr,
                        fileList));
                    eligibleForThisHoster -= oversizedNames.Count;
                }
            }

            if (eligibleForThisHoster > 0)
            {
                anyHosterCanUploadSomething = true;
            }

            // Count limit is checked against eligible (post-size-filter) files: if size
            // already drops the package below the cap, no need to complain about count.
            if (pipeline.MaxFilesPerPackage is int maxCount && eligibleForThisHoster > maxCount)
            {
                HosterValidationWarnings.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance["Wizard_Hoster_TooManyFiles_Format"],
                    hoster.FileHosterName,
                    eligibleForThisHoster,
                    maxCount));
                _hasHardBlock = true;
            }
        }

        // Block Next when files+hosters were chosen but nothing would actually upload —
        // the all-too-big case the user called out explicitly.
        if (selected.Length > 0 && anyUsedHoster && !anyHosterCanUploadSomething)
        {
            _hasHardBlock = true;
        }

        OnPropertyChanged(nameof(HasHosterValidationWarnings));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private void FileEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileEntry.IsSelected))
        {
            RecomputeHosterValidation();
        }
    }

    private void Hoster_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileHosterSelectionViewModel.Use))
        {
            RecomputeHosterValidation();
        }
    }

    public string NextButtonText => IsLastStep
        ? Localizer.Instance["Wizard_Btn_Add"]
        : Localizer.Instance["Wizard_Btn_Next"];

    [ObservableProperty]
    private bool completed;

    partial void OnFileFilterChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnModeChanged(UploadWizardMode value)
    {
        Files.Clear();
        DirectoryPath = string.Empty;
        FileFilter = string.Empty;
    }

    [RelayCommand]
    private void BrowseDirectory()
    {
        string? folder = dialogService.BrowseFolder(
            string.IsNullOrEmpty(DirectoryPath) ? null : DirectoryPath,
            Localizer.Instance["Wizard_Step0_BrowseDialogTitle"]);

        if (folder is not null)
        {
            DirectoryPath = folder;
        }
    }

    [RelayCommand]
    private void BrowseFiles()
    {
        string[]? picked = dialogService.BrowseFiles(
            Localizer.Instance["Wizard_Step0_Files_BrowseDialogTitle"]);

        if (picked is null || picked.Length == 0)
        {
            return;
        }

        AppendFiles(picked);

        if (string.IsNullOrWhiteSpace(PackageTitle) && Files.Count > 0)
        {
            PackageTitle = Path.GetFileNameWithoutExtension(Files[0].FullPath) ?? string.Empty;
        }
    }

    private void AppendFiles(IEnumerable<string> filePaths)
    {
        HashSet<string> existing = new(
            Files.Select(f => f.FullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in filePaths)
        {
            if (existing.Contains(filePath))
            {
                continue;
            }

            FileInfo fi = new(filePath);
            string display = fi.Name;
            if (Files.Any(f => string.Equals(f.FileName, fi.Name, StringComparison.OrdinalIgnoreCase)))
            {
                string folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
                display = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Instance["Wizard_Step1_DuplicateFilenameSuffixFormat"],
                    fi.Name,
                    folderName);
            }

            FileEntry entry = new()
            {
                FullPath = filePath,
                RelativePath = display,
                FileName = fi.Name,
                Size = fi.Length,
                IsSelected = true,
                IsVisible = true,
            };
            Files.Add(entry);
            existing.Add(filePath);
        }
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 0)
        {
            // Source picked + files validated + title set.
            if (IsDirectoryMode)
            {
                if (string.IsNullOrWhiteSpace(DirectoryPath) || !Directory.Exists(DirectoryPath))
                {
                    dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickValidDir"]);
                    return;
                }

                // Files may not have loaded yet if the user typed a path; LoadFiles is
                // idempotent (clears + re-enumerates).
                if (Files.Count == 0)
                {
                    LoadFiles();
                }
            }
            else // Files mode
            {
                if (Files.Count == 0)
                {
                    dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickAtLeastOneFile"]);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(PackageTitle))
            {
                dialogService.ShowError(Localizer.Instance["Wizard_Validation_TitleRequired"]);
                return;
            }

            if (!Files.Any(f => f.IsSelected))
            {
                dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickFile"]);
                return;
            }

            await LoadFileHostersAsync();
            CurrentStep = 1;
        }
        else if (CurrentStep == 1)
        {
            CurrentStep = 2;
        }
        else if (CurrentStep == 2)
        {
            if (await StartUploadAsync())
            {
                Completed = true;
            }
        }
    }

    partial void OnDirectoryPathChanged(string value)
    {
        if (IsDirectoryMode && !string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            LoadFiles();
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
        }
    }

    [RelayCommand]
    private async Task AddAccountForHosterAsync(FileHosterSelectionViewModel? hoster)
    {
        if (hoster is null)
        {
            return;
        }

        string[] availableHosters = [hoster.FileHosterName];
        FileHosterLoginDto? result = dialogService.ShowAddAccountDialog(
            hoster.FileHosterName,
            availableHosters,
            Localizer.Instance["EditAccount_AddTitle"]);

        if (result is null)
        {
            return;
        }

        try
        {
            await fileHosterLoginRepository.InsertAsync(result);
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to save new {hoster.FileHosterName} account: {ex}");
            dialogService.ShowError(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return;
        }

        FileHosterLoginDto[] accounts = await fileHosterLoginRepository.FindAsync(hoster.FileHosterName);
        hoster.SetAccounts(accounts);

        // Auto-tick "Use" now that an account exists — saves the user a click and
        // matches the flow they were already in (they clicked "Add account…" because
        // they wanted to upload to this hoster).
        hoster.Use = true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (FileEntry file in Files)
        {
            file.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (FileEntry file in Files)
        {
            file.IsSelected = false;
        }
    }

    private void LoadFiles()
    {
        Files.Clear();
        FileFilter = string.Empty;
        if (string.IsNullOrEmpty(PackageTitle))
        {
            PackageTitle = Path.GetFileName(DirectoryPath) ?? DirectoryPath;
        }

        foreach (string filePath in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(DirectoryPath, filePath);
            FileInfo fi = new(filePath);
            FileEntry entry = new()
            {
                FullPath = filePath,
                RelativePath = relativePath,
                FileName = fi.Name,
                Size = fi.Length,
                IsSelected = true,
                IsVisible = true,
            };
            Files.Add(entry);
        }
    }

    private void ApplyFilter()
    {
        string filter = FileFilter.Trim();
        foreach (FileEntry file in Files)
        {
            if (string.IsNullOrEmpty(filter))
            {
                file.IsVisible = true;
            }
            else
            {
                file.IsVisible = file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private async Task LoadFileHostersAsync()
    {
        if (FileHosters.Count > 0)
        {
            return;
        }

        foreach (string fileHosterName in FileHosterClient.FileHosters.Keys)
        {
            FileHosterLoginDto[] accounts = await fileHosterLoginRepository.FindAsync(fileHosterName);

            FileHosterSelectionViewModel? sticky = _stickyHosters.Find(
                h => string.Equals(h.FileHosterName, fileHosterName, StringComparison.Ordinal));

            FileHosterSelectionViewModel vm = new(fileHosterName, accounts);
            if (sticky is not null)
            {
                vm.Use = sticky.Use;
                if (sticky.SelectedAccount is not null && accounts.Any(a => a.Id == sticky.SelectedAccount.Id))
                {
                    vm.SelectedAccount = accounts.First(a => a.Id == sticky.SelectedAccount.Id);
                }
            }

            FileHosters.Add(vm);
        }
    }

    private async Task<bool> StartUploadAsync()
    {
        PackageOptions options = new()
        {
            Title = PackageTitle.Trim(),
            Logger = logger,
            Settings = settings,
            SelectedFiles = [.. Files.Where(f => f.IsSelected).Select(f => f.FullPath)],
        };

        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use)
            {
                continue;
            }

            var client = FileHosterClient.FindByHost(hoster.FileHosterName, Protocol.Http, logger);
            if (client is not null)
            {
                FileHosterLoginDto account = hoster.SelectedAccount ?? new FileHosterLoginDto
                {
                    FileHosterName = hoster.FileHosterName,
                };
                options.FileHosters[client] = account;
            }
        }

        if (options.FileHosters.Count == 0)
        {
            dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickHoster"]);
            return false;
        }

        SaveStickySelections();

        try
        {
            switch (StartMode)
            {
                case UploadStartMode.Immediately:
                    await packageManager.AddAndStartPackageAsync(options);
                    packageManager.StartPackages();
                    break;

                case UploadStartMode.Later:
                    await packageManager.AddPackageOnlyAsync(options);
                    break;

                case UploadStartMode.Scheduled:
                    if (!TimeSpan.TryParse(ScheduledTime, out TimeSpan time))
                    {
                        time = TimeSpan.Zero;
                    }

                    Package package = await packageManager.AddPackageOnlyAsync(options);
                    DateTime scheduled = ScheduledDate.Date + time;
                    packageManager.ScheduleDelayedStart(package, scheduled);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to add upload job: {ex}");
            dialogService.ShowError(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return false;
        }
    }

    private void SaveStickySelections()
    {
        _stickyHosters.Clear();
        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            _stickyHosters.Add(hoster);
        }
    }
}

public partial class FileEntry : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isVisible = true;

    public string FullPath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }
}
