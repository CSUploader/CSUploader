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

    // Optional: when present, the Summary step refreshes each selected account's free space
    // non-interactively (no WebView) so the capacity fit uses up-to-date numbers. Null in tests.
    private readonly IAccountVerifier? _accountVerifier;

    // Bumped on every Summary (re)build; a refresh result from a superseded generation is ignored.
    private int _refreshGeneration;

    private static readonly List<FileHosterSelectionViewModel> _stickyHosters = [];

    /// <summary>The in-flight (or last) Summary-page storage refresh, exposed so tests can await it.</summary>
    internal Task? PendingStorageRefresh { get; private set; }

    public UploadWizardViewModel(
        PackageManager packageManager,
        FileHosterLoginRepository fileHosterLoginRepository,
        IDialogService dialogService,
        IAppLogger logger,
        AppSettings settings,
        IFileHosterRegistry? fileHosterRegistry = null,
        IAccountVerifier? accountVerifier = null)
    {
        this.packageManager = packageManager;
        this.fileHosterLoginRepository = fileHosterLoginRepository;
        this.dialogService = dialogService;
        this.logger = logger;
        this.settings = settings;
        _fileHosterRegistry = fileHosterRegistry;
        _accountVerifier = accountVerifier;

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
        _summaryDirty = true;
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
        _summaryDirty = true;
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

    /// <summary>
    /// Populated when the user advances to the Summary step (CurrentStep==2) from the
    /// File Hosters step. Each entry is one hoster that will receive at least one file;
    /// hosters that were checked on step 2 but turned out to have zero eligible files
    /// (every selected file too big, no usable account, etc.) are omitted entirely so
    /// the page only shows what will actually upload.
    /// </summary>
    public ObservableCollection<HosterUploadSummary> Summaries { get; } = [];

    /// <summary>
    /// Selected files that won't be uploaded to ANY hoster — every chosen hoster
    /// rejected them via a size cap, count cap, or account-state filter. Surfaces in
    /// the Summary page's warning banner so the user can decide whether to go back
    /// and adjust, or accept the partial coverage and proceed.
    /// </summary>
    public ObservableCollection<FileEntry> OrphanFiles { get; } = [];

    public int OrphanFilesCount => OrphanFiles.Count;

    public bool HasOrphanFiles => OrphanFiles.Count > 0;

    /// <summary>Non-empty when the Summary step's capacity fit auto-unchecked one or more files to
    /// keep a hoster within its available space — shown as an informational notice on Page 3.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoFitNotice))]
    private string autoFitNotice = string.Empty;

    public bool HasAutoFitNotice => !string.IsNullOrEmpty(AutoFitNotice);

    /// <summary>Grand total across all selected hosters for the Summary page footer: the number of file
    /// uploads (a file sent to two hosters counts twice) and their combined size. Updated live as files
    /// are toggled or a landing storage refresh re-fits — nudged from <see cref="RecomputeSummaryCapacity"/>.</summary>
    public string TotalUploadSummary
    {
        get
        {
            int files = Summaries.Sum(s => s.IncludedCount);
            long bytes = Summaries.Sum(s => s.IncludedBytes);
            string size = ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
            return string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Summary_TotalFooter_Format"], files, size);
        }
    }

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
    /// dropped at upload time) and don't block. The Summary step (CurrentStep==2) blocks
    /// Next only while a hoster has more bytes checked than its account's available storage
    /// (<see cref="_summaryHasOverCapacity"/>) — orphan files still just surface a warning
    /// banner the user may proceed past.
    /// </summary>
    public bool CanGoNext => CurrentStep switch
    {
        1 => !_hasHardBlock,
        2 => !_summaryHasOverCapacity,
        _ => true,
    };

    public bool IsLastStep => CurrentStep == 3;

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

    /// <summary>True when at least one hoster on the Summary step has more bytes checked than its
    /// account's available storage — blocks Next until the user unchecks enough files. Only
    /// quota-reporting hosters (IcerBox/FileBoom/HitFile) can ever set it.</summary>
    private bool _summaryHasOverCapacity;

    /// <summary>True when a Page 1/2 selection changed since the Summary was last built, so the next
    /// entry to step 2 rebuilds it. Starts true (first entry always builds), cleared after a build,
    /// and set again by any file/hoster selection change — so a Back from the later start-mode step
    /// (no selection change) preserves the user's manual Page 3 checkbox edits instead of re-fitting.</summary>
    private bool _summaryDirty = true;

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
                if (selected.Length > 0)
                {
                    anyHosterCanUploadSomething = true;
                }

                continue;
            }

            int eligibleForThisHoster = selected.Length;

            // The per-file size cap can vary by the selected account (e.g. Hexload's anonymous
            // tier allows a larger file than its API tier).
            FileHosterLoginDto? account = hoster.SelectedAccount;
            long? hosterMaxFileSize = account is not null ? pipeline.MaxFileSizeFor(account) : pipeline.MaxFileSize;

            // Classify each selected file once against the hoster's per-file constraints. A file that
            // fails two checks (too big AND a name the hoster won't accept, e.g. Buzzheavier's '#'/';')
            // is counted under just one — size takes precedence — so eligibility never double-subtracts.
            List<string> oversizedNames = [];
            List<string> rejectedNameNames = [];
            foreach (FileEntry f in selected)
            {
                if (hosterMaxFileSize is long maxBytes && f.Size > maxBytes)
                {
                    oversizedNames.Add(f.FileName);
                }
                else if (pipeline.RejectedFileNameReason(f.FileName) is not null)
                {
                    rejectedNameNames.Add(f.FileName);
                }
            }

            // Render each file list one per line so the warning panel can scroll when there are many.
            // Both resx strings already end with a newline after the colon.
            if (hosterMaxFileSize is long limitBytes && oversizedNames.Count > 0)
            {
                HosterValidationWarnings.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance["Wizard_Hoster_FileTooLarge_Format"],
                    hoster.FileHosterName,
                    ByteUnit.FromBytes(limitBytes, ByteBase.Binary).ToFriendlyString(),
                    string.Join("\n", oversizedNames)));
            }

            if (rejectedNameNames.Count > 0)
            {
                HosterValidationWarnings.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance["Wizard_Hoster_FileNameRejected_Format"],
                    hoster.FileHosterName,
                    string.Join("\n", rejectedNameNames)));
            }

            eligibleForThisHoster -= oversizedNames.Count + rejectedNameNames.Count;

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
            _summaryDirty = true;
            RecomputeHosterValidation();
        }
    }

    private void Hoster_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The Use toggle and switching account both affect eligibility — anonymous ⇄ a login
        // can change the per-file size and per-batch count caps (e.g. Hexload).
        if (e.PropertyName is nameof(FileHosterSelectionViewModel.Use)
            or nameof(FileHosterSelectionViewModel.SelectedAccount))
        {
            _summaryDirty = true;
            RecomputeHosterValidation();
        }
    }

    /// <summary>
    /// Builds <see cref="Summaries"/> and <see cref="OrphanFiles"/> from the current
    /// file selection + checked hosters. A hoster makes it into the summary only when
    /// (a) it's checked on step 2, (b) it has a selected account whose CheckStatus
    /// isn't Failed and that isn't Disabled, and (c) at least one selected file passes
    /// the hoster's per-file size cap (oversized files are silently dropped). Within
    /// each surviving hoster's row, the file list is then truncated to its declared
    /// MaxFilesPerPackage cap — same per-file-with-position rule the upload pipeline
    /// would apply. Files that don't make it into ANY hoster's row become orphans and
    /// drive the warning banner.
    /// </summary>
    private void RecomputeSummary()
    {
        // Detach the previous summaries' capacity listeners before rebuilding.
        foreach (HosterUploadSummary previous in Summaries)
        {
            previous.CapacityChanged -= OnSummaryCapacityChanged;
        }

        Summaries.Clear();
        OrphanFiles.Clear();

        FileEntry[] selected = [.. Files.Where(f => f.IsSelected)];
        HashSet<FileEntry> withDestination = [];

        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use || hoster.SelectedAccount is not FileHosterLoginDto account)
            {
                continue;
            }

            // Account state filter: a disabled account or one whose last verification failed
            // has no chance of accepting an upload, so the hoster gets dropped from the summary
            // (same effect as unchecking it). The synthetic Anonymous selection has no such
            // state, so it's never filtered here.
            if (!account.IsAnonymous && (account.Disabled || account.CheckStatus == AccountCheckStatus.Failed))
            {
                continue;
            }

            IFileHosterPipeline? pipeline = _fileHosterRegistry?.Find(hoster.FileHosterName);
            long? maxFileSize = pipeline?.MaxFileSizeFor(account);
            int? maxFilesPerPackage = pipeline?.MaxFilesPerPackage;

            List<FileEntry> eligible = [];
            foreach (FileEntry file in selected)
            {
                if (maxFileSize is long cap && file.Size > cap)
                {
                    continue;
                }

                // A name the hoster's server would reject (e.g. Buzzheavier's '#'/';') is dropped here
                // just like an oversized file — the file falls through to OrphanFiles and the banner.
                if (pipeline?.RejectedFileNameReason(file.FileName) is not null)
                {
                    continue;
                }

                eligible.Add(file);
                if (maxFilesPerPackage is int limit && eligible.Count >= limit)
                {
                    break;
                }
            }

            if (eligible.Count == 0)
            {
                continue;
            }

            // Eligible (size-cap-passing) files have a destination even if the capacity fit later
            // unchecks some of them — those are intentional drops, not size-orphans.
            foreach (FileEntry file in eligible)
            {
                withDestination.Add(file);
            }

            // Remaining free space for accounts whose hoster reports a quota; null = unlimited.
            long? available = account.StorageQuotaBytes is long quota && account.StorageUsedBytes is long used
                ? Math.Max(0L, quota - used)
                : null;

            List<SummaryFileItem> items = [.. eligible.Select(f => new SummaryFileItem(f, included: true))];
            HosterUploadSummary summary = new(
                hoster.FileHosterName,
                account.Username ?? string.Empty,
                items,
                available,
                maxFileSize,
                account);

            // Auto-fit BEFORE wiring the wizard's listener so the initial fit doesn't churn CanGoNext.
            summary.AutoFit();
            summary.CapacityChanged += OnSummaryCapacityChanged;
            Summaries.Add(summary);
        }

        foreach (FileEntry file in selected)
        {
            if (!withDestination.Contains(file))
            {
                OrphanFiles.Add(file);
            }
        }

        RecomputeSummaryCapacity(); // also refreshes the page-level "N unchecked" banner
        OnPropertyChanged(nameof(OrphanFilesCount));
        OnPropertyChanged(nameof(HasOrphanFiles));

        // Non-blocking, WebView-free refresh of each selected account's free space so the fit uses
        // up-to-date numbers. Each hoster updates live as its result lands; failures keep the snapshot.
        // The generation token makes a result from a superseded (re)build a no-op.
        PendingStorageRefresh = RefreshSelectedStorageAsync(++_refreshGeneration);
    }

    /// <summary>
    /// Refreshes each selected, storage-refreshable account's free space without any interactive
    /// sign-in, applying each result live as it lands. A failed/null refresh leaves the snapshot.
    /// Results from a superseded generation (the user changed the selection and we rebuilt) are ignored.
    /// </summary>
    private async Task RefreshSelectedStorageAsync(int generation)
    {
        if (_accountVerifier is null || _fileHosterRegistry is null)
        {
            return;
        }

        // Mark the refreshable hosters "checking" up front (a real account + a storage-refreshable
        // pipeline). Snapshot the list so a concurrent rebuild can't mutate it mid-iteration.
        List<HosterUploadSummary> refreshable = [];
        foreach (HosterUploadSummary summary in Summaries)
        {
            if (summary.Account is { IsAnonymous: false }
                && _fileHosterRegistry.Find(summary.HosterName) is IStorageRefreshablePipeline)
            {
                summary.IsRefreshing = true;
                refreshable.Add(summary);
            }
        }

        await Task.WhenAll(refreshable.Select(summary => RefreshOneAsync(summary, generation)));
    }

    private async Task RefreshOneAsync(HosterUploadSummary summary, int generation)
    {
        StorageUsage? usage = null;
        try
        {
            usage = await _accountVerifier!.RefreshStorageAsync(summary.HosterName, summary.Account!, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Status, $"Wizard storage refresh failed for {summary.HosterName}: {ex.Message}");
        }

        // Ignore a result from a superseded build — the summaries it refers to are gone.
        if (generation != _refreshGeneration)
        {
            return;
        }

        summary.IsRefreshing = false;
        if (usage is { } fresh)
        {
            ApplyRefreshedStorage(summary, fresh);
        }
    }

    /// <summary>Writes refreshed usage onto the account DTO (so a later rebuild reuses it) and onto the
    /// summary's available figure — which live-re-fits the hoster when the user hasn't edited it.</summary>
    private static void ApplyRefreshedStorage(HosterUploadSummary summary, StorageUsage usage)
    {
        if (summary.Account is { } account)
        {
            account.StorageUsedBytes = usage.UsedBytes;
            account.StorageQuotaBytes = usage.QuotaBytes;
        }

        long? available = usage.QuotaBytes is long quota && usage.UsedBytes is long used
            ? Math.Max(0L, quota - used)
            : null;
        summary.ApplyRefreshedAvailable(available);
    }

    private void OnSummaryCapacityChanged(object? sender, EventArgs e) => RecomputeSummaryCapacity();

    /// <summary>Recomputes whether any hoster on the Summary page is over its available space and
    /// refreshes <see cref="CanGoNext"/> (the Summary step blocks Next while any hoster is over).</summary>
    private void RecomputeSummaryCapacity()
    {
        RecomputeAutoFitNotice();
        OnPropertyChanged(nameof(TotalUploadSummary));

        bool over = Summaries.Any(s => s.IsOverCapacity);
        if (over != _summaryHasOverCapacity)
        {
            _summaryHasOverCapacity = over;
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    /// <summary>Keeps the page-level "N file(s) unchecked to fit" banner in sync with the live total of
    /// unchecked files — auto-fit drops plus any manual toggles and post-refresh re-fits — so it never
    /// goes stale when a landing storage refresh shrinks available space. Matches the per-hoster clue.</summary>
    private void RecomputeAutoFitNotice()
    {
        List<HosterUploadSummary> constrained = [.. Summaries.Where(s => s.Files.Any(item => !item.Included))];
        int unchecked_ = constrained.Sum(s => s.Files.Count(item => !item.Included));
        if (unchecked_ == 0)
        {
            AutoFitNotice = string.Empty;
            return;
        }

        // When a SINGLE quota hoster drove the unchecking, name its free space so the banner answers
        // "unchecked to fit WHAT". Multiple constrained hosters have no single free figure, so fall back
        // to the plain count.
        AutoFitNotice = constrained.Count == 1 && constrained[0].AvailableBytes is long available
            ? string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_AutoFitNoticeWithFree_Format"],
                unchecked_,
                ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString())
            : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Summary_AutoFitNotice_Format"], unchecked_);
    }

    /// <summary>
    /// Builds the per-hoster file allow-list (<see cref="PackageOptions.IncludedFilesPerHoster"/>)
    /// from the Summary page's current checkbox state: each hoster → the <c>FullPath</c>s of its
    /// still-checked files. Returns null when there are no summaries (nothing to restrict → the
    /// package keeps its default cross-product). Internal for testing.
    /// </summary>
    internal Dictionary<string, HashSet<string>>? BuildIncludedFilesPerHoster()
    {
        if (Summaries.Count == 0)
        {
            return null;
        }

        Dictionary<string, HashSet<string>> includedPerHoster = [with(StringComparer.Ordinal)];
        foreach (HosterUploadSummary summary in Summaries)
        {
            includedPerHoster[summary.HosterName] =
                [.. summary.Files.Where(item => item.Included).Select(item => item.File.FullPath)];
        }

        return includedPerHoster;
    }

    partial void OnCurrentStepChanged(int value)
    {
        // Lazy-(re)build the summary on entry to step 2, but ONLY when a Page 1/2 selection actually
        // changed since it was last built (_summaryDirty). Otherwise keep the existing summaries so a
        // Back from the later "when to start" step (step 3) preserves the user's manual checkbox edits
        // and the auto-fit result rather than wiping them with a fresh fit.
        if (value == 2 && _summaryDirty)
        {
            RecomputeSummary();
            _summaryDirty = false;
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
                    CultureInfo.CurrentCulture,
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
            // Advance to the new Summary step. OnCurrentStepChanged populates Summaries.
            CurrentStep = 2;
        }
        else if (CurrentStep == 2)
        {
            // Summary → Start/Schedule.
            CurrentStep = 3;
        }
        else if (CurrentStep == 3)
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
            dialogService.ShowError(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
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

        foreach (string fileHosterName in FileHosterClient.NamesAlphabetical)
        {
            FileHosterLoginDto[] accounts = await fileHosterLoginRepository.FindAsync(fileHosterName);
            IFileHosterPipeline? pipeline = _fileHosterRegistry?.Find(fileHosterName);
            bool supportsAnonymous = pipeline?.SupportsAnonymousUpload ?? false;

            // Same account-vs-fallback rule RecomputeHosterValidation applies, so the "Max file
            // size" column always shows the number the oversize warning would enforce.
            Func<FileHosterLoginDto?, long?>? maxFileSizeResolver = pipeline is null
                ? null
                : account => account is not null ? pipeline.MaxFileSizeFor(account) : pipeline.MaxFileSize;

            FileHosterSelectionViewModel? sticky = _stickyHosters.Find(
                h => string.Equals(h.FileHosterName, fileHosterName, StringComparison.Ordinal));

            FileHosterSelectionViewModel vm = new(fileHosterName, accounts, supportsAnonymous, maxFileSizeResolver);
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
                    IsAnonymous = hoster.SupportsAnonymous,
                };
                options.FileHosters[client] = account;
            }
        }

        // Per-hoster file selection from the Summary page's capacity fit (null when there are no
        // summaries → the package keeps its default cross-product; size/quota filters still apply).
        options.IncludedFilesPerHoster = BuildIncludedFilesPerHoster();

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
            dialogService.ShowError(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
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

/// <summary>
/// One file row inside a <see cref="HosterUploadSummary"/> on the wizard's Summary page. Its
/// <see cref="Included"/> checkbox is INDEPENDENT per hoster — unchecking a file for one hoster
/// doesn't affect another hoster's copy or the Page 1 selection — which is how the per-hoster
/// available-space fit lets a big file go to a roomy hoster but not a tight one.
/// </summary>
public sealed partial class SummaryFileItem : ObservableObject
{
    [ObservableProperty]
    private bool included;

    public SummaryFileItem(FileEntry file, bool included)
    {
        File = file;

        // Set the backing field directly: constructing the item must not raise a change
        // notification (nothing is subscribed yet, and the owner recomputes once at the end).
        this.included = included;
    }

    public FileEntry File { get; }

    public string FileName => File.FileName;

    public long Size => File.Size;
}

/// <summary>
/// One row on the Upload Wizard's summary step: one hoster, the account chosen for it, and the
/// files eligible for it after the hoster's per-file size cap and per-package count cap. Each file
/// carries an independent <see cref="SummaryFileItem.Included"/> checkbox. For an account that
/// reports a storage quota, <see cref="AvailableBytes"/> is the remaining free space and the row
/// flips <see cref="IsOverCapacity"/> when the included files exceed it (which blocks the wizard's
/// Next). Hosters that end up with zero eligible files don't get a summary at all.
/// </summary>
public sealed partial class HosterUploadSummary : ObservableObject
{
    public HosterUploadSummary(
        string hosterName,
        string accountUsername,
        IReadOnlyList<SummaryFileItem> files,
        long? availableBytes,
        long? maxFileSize,
        FileHosterLoginDto? account = null)
    {
        HosterName = hosterName;
        AccountUsername = accountUsername;
        Account = account;
        Files = [with(files)];
        AvailableBytes = availableBytes;
        MaxFileSize = maxFileSize;

        foreach (SummaryFileItem item in Files)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        Recompute();
    }

    public string HosterName { get; }

    public string AccountUsername { get; }

    public ObservableCollection<SummaryFileItem> Files { get; }

    /// <summary>Remaining free space on the selected account (quota − used), or null when the hoster
    /// reports no quota — treated as unlimited, so it never constrains and never auto-fits. Updated in
    /// place by <see cref="ApplyRefreshedAvailable"/> when a live storage refresh lands.</summary>
    public long? AvailableBytes { get; private set; }

    /// <summary>The selected account for this hoster, used to refresh its storage on the Summary page.
    /// Null for a synthetic/anonymous selection (nothing to refresh).</summary>
    public FileHosterLoginDto? Account { get; }

    /// <summary>True while a live storage refresh for this hoster is in flight — drives the per-hoster
    /// "checking available space…" indicator.</summary>
    [ObservableProperty]
    private bool isRefreshing;

    /// <summary>True once the user has manually toggled a file on this hoster. A landing storage
    /// refresh then updates the available figure WITHOUT re-running the auto-fit, so it never wipes
    /// the user's own choices.</summary>
    public bool HasUserEdits { get; private set; }

    private bool _applyingAutoFit;

    /// <summary>Per-file size cap the hoster's pipeline declares, or null when it declares none.</summary>
    public long? MaxFileSize { get; }

    /// <summary>Raised when a file's Included toggle changes the included total — the wizard listens
    /// so it can re-evaluate whether Next should be blocked.</summary>
    public event EventHandler? CapacityChanged;

    /// <summary>Bytes of the currently-checked files.</summary>
    [ObservableProperty]
    private long includedBytes;

    /// <summary>Count of currently-checked files.</summary>
    [ObservableProperty]
    private int includedCount;

    /// <summary>True when the checked files exceed <see cref="AvailableBytes"/> (only possible for a
    /// quota-reporting hoster). Drives the red capacity line and the wizard's Next block.</summary>
    [ObservableProperty]
    private bool isOverCapacity;

    /// <summary>True when this hoster's account reports a storage quota (so capacity applies).</summary>
    public bool HasQuota => AvailableBytes is not null;

    /// <summary>Number of this hoster's eligible files currently UNchecked. On first show this is the
    /// auto-fit's drop count, so it doubles as a per-hoster reason for the deselection.</summary>
    public int UncheckedCount => Files.Count - IncludedCount;

    /// <summary>True for a quota hoster with unchecked files — drives a per-hoster "N unchecked to fit"
    /// hint so the user sees, on each hoster, why files were deselected there (not just the page-level
    /// banner). Never shows for an unlimited hoster (no capacity reason to deselect), nor while the
    /// hoster is over capacity — there the red over-capacity hint stands alone rather than pairing
    /// with an "unchecked to fit" line that would read oddly against "you're over, uncheck more".</summary>
    public bool HasUncheckedFiles => HasQuota && UncheckedCount > 0 && !IsOverCapacity;

    /// <summary>"N file(s) unchecked to fit the available space (X free)" for this hoster; empty when
    /// none. HasUncheckedFiles implies HasQuota, so AvailableBytes is always present here.</summary>
    public string UncheckedDisplay => HasUncheckedFiles && AvailableBytes is long available
        ? string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Summary_AutoFitNoticeWithFree_Format"],
            UncheckedCount,
            ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString())
        : string.Empty;

    // Total eligible files / bytes (independent of the checkbox state) — kept for the summary header.
    public int FileCount => Files.Count;

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>The expander-header summary of what's CHECKED — "•  N files  •  &lt;bytes&gt;" plus the
    /// optional per-file-cap hint. A single string (not inline Runs) so it refreshes live as the user
    /// toggles files: inline <c>&lt;Run&gt;</c> text doesn't re-render on a source change.</summary>
    public string IncludedSummary
    {
        get
        {
            // Spell out both halves — "N of M files selected" and "X to upload" — so a header like
            // "0 of 54 files selected • 0 B to upload" reads unambiguously as the current selection
            // (e.g. when a full account's auto-fit unchecked everything), not "this hoster has no files".
            string filesPart = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_FilesSelected_Format"],
                IncludedCount,
                FileCount);
            string sizePart = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_ToUpload_Format"],
                ByteUnit.FromBytes(IncludedBytes, ByteBase.Binary).ToFriendlyString());
            return string.Format(CultureInfo.CurrentCulture, "•  {0}  •  {1}{2}", filesPart, sizePart, MaxFileSizeDisplay);
        }
    }

    /// <summary>"{checked} selected of {free} free" for a quota-reporting hoster; empty otherwise.</summary>
    public string CapacityDisplay
    {
        get
        {
            if (AvailableBytes is not long available)
            {
                return string.Empty;
            }

            string included = ByteUnit.FromBytes(IncludedBytes, ByteBase.Binary).ToFriendlyString();
            string free = ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString();
            return string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_SelectedOfFree_Format"],
                included,
                free);
        }
    }

    /// <summary>The over-capacity hint shown (red) when <see cref="IsOverCapacity"/>; empty otherwise.</summary>
    public string CapacityError => IsOverCapacity ? Localizer.Instance["Wizard_Summary_OverCapacityHint"] : string.Empty;

    /// <summary>Pre-formatted "  •  max X per file" suffix for the summary header, or empty when the
    /// hoster declares no cap.</summary>
    public string MaxFileSizeDisplay
    {
        get
        {
            if (MaxFileSize is not long bytes)
            {
                return string.Empty;
            }

            string size = ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
            return "  •  " + string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_MaxFileSize_Format"],
                size);
        }
    }

    /// <summary>Recomputes the included total + over-capacity flag from the current checkbox states.</summary>
    public void Recompute()
    {
        long sum = 0;
        int count = 0;
        foreach (SummaryFileItem item in Files)
        {
            if (item.Included)
            {
                sum += item.Size;
                count++;
            }
        }

        IncludedBytes = sum;
        IncludedCount = count;
        IsOverCapacity = AvailableBytes is long available && sum > available;

        // CapacityDisplay/CapacityError/IncludedSummary/Unchecked* are computed off
        // IncludedBytes/IncludedCount/IsOverCapacity — nudge them so the header, capacity line and the
        // per-hoster "N unchecked" hint all refresh live as files are toggled.
        OnPropertyChanged(nameof(CapacityDisplay));
        OnPropertyChanged(nameof(CapacityError));
        OnPropertyChanged(nameof(IncludedSummary));
        OnPropertyChanged(nameof(UncheckedCount));
        OnPropertyChanged(nameof(HasUncheckedFiles));
        OnPropertyChanged(nameof(UncheckedDisplay));
    }

    /// <summary>
    /// Greedy "keep biggest that fit": for a quota-reporting hoster, walk files largest-first and keep
    /// each <see cref="SummaryFileItem.Included"/> while the running total stays within
    /// <see cref="AvailableBytes"/>; uncheck the rest. No-op for an unlimited hoster. Returns how many
    /// files THIS call unchecked — only meaningful right after construction (when every item starts
    /// checked); the wizard derives its "N unchecked to fit" notice from the final state instead.
    /// </summary>
    public int AutoFit()
    {
        if (AvailableBytes is not long available)
        {
            return 0;
        }

        // Guard so the auto-fit's own toggles don't register as user edits.
        _applyingAutoFit = true;
        try
        {
            int uncheckedCount = 0;
            long running = 0;
            foreach (SummaryFileItem item in Files.OrderByDescending(f => f.Size))
            {
                if (running + item.Size <= available)
                {
                    running += item.Size;
                    item.Included = true;
                }
                else
                {
                    if (item.Included)
                    {
                        uncheckedCount++;
                    }

                    item.Included = false;
                }
            }

            return uncheckedCount;
        }
        finally
        {
            _applyingAutoFit = false;
        }
    }

    /// <summary>
    /// Applies a freshly-refreshed available figure: updates <see cref="AvailableBytes"/> and, when the
    /// user hasn't manually edited this hoster yet, re-runs the auto-fit against the new number;
    /// otherwise it leaves their selection alone (the capacity line / over-capacity state still
    /// reflects the fresh figure). Raises <see cref="CapacityChanged"/> so the wizard re-evaluates Next.
    /// </summary>
    public void ApplyRefreshedAvailable(long? newAvailable)
    {
        AvailableBytes = newAvailable;
        OnPropertyChanged(nameof(AvailableBytes));
        OnPropertyChanged(nameof(HasQuota));

        if (!HasUserEdits)
        {
            AutoFit();
        }

        Recompute();
        CapacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryFileItem.Included))
        {
            // A toggle outside the auto-fit is the user's own edit — remember it so a landing storage
            // refresh respects their choices rather than re-fitting over them.
            if (!_applyingAutoFit)
            {
                HasUserEdits = true;
            }

            Recompute();
            CapacityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
