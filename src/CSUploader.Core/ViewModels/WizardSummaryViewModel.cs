// <copyright file="WizardSummaryViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;

namespace CSUploader.ViewModels;

/// <summary>
/// The Upload Wizard's Summary step: per-hoster summaries, orphan files, the capacity auto-fit and
/// its notices, and the non-interactive storage refresh. Owned and constructed by
/// <see cref="UploadWizardViewModel"/>, which hands it the other steps' live collections and calls
/// <see cref="RecomputeSummary"/> on entry to the step; capacity-state changes are surfaced through
/// <see cref="CapacityStateChanged"/> so the parent re-reads <c>CanGoNext</c> at exactly the moments
/// the pre-split code raised it.
/// </summary>
public sealed partial class WizardSummaryViewModel : ObservableObject
{
    private readonly IAppLogger _logger;

    /// <summary>The sources step's live file list — the summary is built from its selected entries.</summary>
    private readonly ObservableCollection<FileEntry> _files;

    /// <summary>The hoster step's live list — the summary walks its ticked rows.</summary>
    private readonly ObservableCollection<FileHosterSelectionViewModel> _fileHosters;

    private readonly IFileHosterRegistry? _fileHosterRegistry;

    // Optional: when present, the Summary step refreshes each selected account's free space
    // non-interactively (no WebView) so the capacity fit uses up-to-date numbers. Null in tests.
    private readonly IAccountVerifier? _accountVerifier;

    // Bumped on every Summary (re)build; a refresh result from a superseded generation is ignored.
    private int _refreshGeneration;

    /// <summary>The in-flight (or last) Summary-page storage refresh, exposed so tests can await it.</summary>
    internal Task? PendingStorageRefresh { get; private set; }

    public WizardSummaryViewModel(
        IAppLogger logger,
        ObservableCollection<FileEntry> files,
        ObservableCollection<FileHosterSelectionViewModel> fileHosters,
        IFileHosterRegistry? fileHosterRegistry = null,
        IAccountVerifier? accountVerifier = null)
    {
        _logger = logger;
        _files = files;
        _fileHosters = fileHosters;
        _fileHosterRegistry = fileHosterRegistry;
        _accountVerifier = accountVerifier;
    }

    /// <summary>
    /// Raised when <see cref="HasOverCapacity"/> flipped — the split's stand-in for the pre-split
    /// code raising <c>OnPropertyChanged(nameof(CanGoNext))</c> on the one object. Synchronous, from
    /// the same call site.
    /// </summary>
    public event EventHandler? CapacityStateChanged;

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
    public partial string AutoFitNotice { get; set; } = string.Empty;

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

    /// <summary>True when at least one hoster on the Summary step has more bytes checked than its
    /// account's available storage — blocks Next until the user unchecks enough files. Only
    /// quota-reporting hosters (IcerBox/FileBoom/HitFile) can ever set it.</summary>
    private bool _summaryHasOverCapacity;

    /// <summary>The parent's <c>CanGoNext</c> half for the Summary step — see <see cref="_summaryHasOverCapacity"/>.</summary>
    internal bool HasOverCapacity => _summaryHasOverCapacity;

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
    internal void RecomputeSummary()
    {
        // Detach the previous summaries' capacity listeners before rebuilding.
        foreach (HosterUploadSummary previous in Summaries)
        {
            previous.CapacityChanged -= OnSummaryCapacityChanged;
        }

        Summaries.Clear();
        OrphanFiles.Clear();

        FileEntry[] selected = [.. _files.Where(f => f.IsSelected)];
        HashSet<FileEntry> withDestination = [];

        foreach (FileHosterSelectionViewModel hoster in _fileHosters)
        {
            if (!hoster.Use || hoster.SelectedAccount is not FileHosterLoginDto account)
            {
                continue;
            }

            // Account state filter — see WizardHostersViewModel.UnusableAccountReason. The hoster page
            // warns about this and blocks Next, so reaching the summary with one of these is only
            // possible when ANOTHER hoster can still upload; dropping it here keeps the page showing
            // only what will run.
            if (WizardHostersViewModel.UnusableAccountReason(account) is not null)
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
                // A file TYPE it refuses (qu.ax's allowlist, Uploadrar's and filedot's blocklists) is
                // dropped the same way: a different sentence for the user, the same outcome here.
                if (pipeline?.RejectedFileNameReason(file.FileName) is not null
                    || pipeline?.RejectedFileExtensionReason(file.FileName) is not null)
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
                account.DisplayName,
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
            _logger.Log(this, LogType.Status, $"Wizard storage refresh failed for {summary.HosterName}: {ex.Message}");
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
    /// tells the parent (via <see cref="CapacityStateChanged"/>) to re-read <c>CanGoNext</c> — the
    /// Summary step blocks Next while any hoster is over.</summary>
    private void RecomputeSummaryCapacity()
    {
        RecomputeAutoFitNotice();
        OnPropertyChanged(nameof(TotalUploadSummary));

        bool over = Summaries.Any(s => s.IsOverCapacity);
        if (over != _summaryHasOverCapacity)
        {
            _summaryHasOverCapacity = over;
            CapacityStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Keeps the page-level "N file(s) unchecked to fit" banner in sync with the live count of files
    /// the capacity auto-fit evicted for space (and post-refresh re-fits) — excluding files the user
    /// unchecked by hand — so it never goes stale when a landing storage refresh shrinks available space and
    /// never miscounts a manual uncheck. Matches the per-hoster clue.</summary>
    private void RecomputeAutoFitNotice()
    {
        // Count ONLY files the capacity auto-fit evicted for space — never a file the user unchecked by hand
        // (that isn't a space eviction, yet the banner text specifically claims "to fit the available space").
        List<HosterUploadSummary> constrained = [.. Summaries.Where(s => s.SpaceUncheckedCount > 0)];
        int unchecked_ = constrained.Sum(s => s.SpaceUncheckedCount);
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
}
