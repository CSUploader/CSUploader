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

    // While true (a bulk Files population — see BulkMutateFiles), the per-item validation + footer recompute is
    // suspended and run ONCE at the end. Otherwise each Files.Add re-runs RecomputeHosterValidation (O(files))
    // and the footer stats (O(files)), making a directory scan O(files²) on the UI thread.
    private bool _bulkLoadingFiles;

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
        if (!_bulkLoadingFiles)
        {
            RecomputeHosterValidation();
            NotifySelectionStats(); // adds/removes change the footer's live count + total size
        }
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

        // The list the filter counts against just changed, so "N of M" has to move with it — the
        // hosters are added one by one during LoadFileHosters.
        OnPropertyChanged(nameof(VisibleHosterCount));
        OnPropertyChanged(nameof(HosterFilterSummary));
        OnPropertyChanged(nameof(AllListedHostersChecked));

        RecomputeHosterValidation();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    public partial int CurrentStep { get; set; }

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
    public partial string PackageTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileFilter { get; set; } = string.Empty;
    public ObservableCollection<FileEntry> Files { get; } = [];

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    /// <summary>
    /// Name filter for the File Hosters step, matched case-insensitively anywhere in the hoster's
    /// name. Empty shows everything.
    /// </summary>
    [ObservableProperty]
    public partial string HosterFilterText { get; set; } = string.Empty;

    /// <summary>
    /// Narrows the File Hosters step to hosters that accept uploads with no account
    /// (<see cref="FileHosterSelectionViewModel.SupportsAnonymous"/>). Combines with
    /// <see cref="HosterFilterText"/> — both must match.
    /// </summary>
    [ObservableProperty]
    public partial bool AnonymousHostersOnly { get; set; }

    /// <summary>
    /// Raised when either hoster filter changes. The head re-evaluates its DataGrid collection view
    /// in response — the same split the Uploads tab uses (<c>UploadsViewModel.FilterInvalidated</c>),
    /// which keeps this ViewModel framework-free and, more importantly, keeps the filter a VIEW
    /// concern: <see cref="FileHosters"/> itself is never touched, so a hoster ticked and then
    /// filtered out of sight still uploads.
    /// </summary>
    public event EventHandler? HosterFilterInvalidated;

    /// <summary>
    /// The File Hosters step's filter predicate, applied by the head to its collection view. A row
    /// passes when its name contains <see cref="HosterFilterText"/> (case-insensitive, trimmed) AND,
    /// when <see cref="AnonymousHostersOnly"/> is set, the hoster supports anonymous upload.
    /// </summary>
    public bool MatchesHosterFilter(object item)
    {
        if (item is not FileHosterSelectionViewModel hoster)
        {
            return false;
        }

        if (AnonymousHostersOnly && !hoster.SupportsAnonymous)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(HosterFilterText))
        {
            return true;
        }

        return hoster.FileHosterName.Contains(HosterFilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when either filter is narrowing the list — drives the "showing N of M" hint and
    /// the Clear button, both of which are noise when everything is visible.</summary>
    public bool IsHosterFilterActive => AnonymousHostersOnly || !string.IsNullOrWhiteSpace(HosterFilterText);

    /// <summary>How many hosters the current filter leaves visible.</summary>
    public int VisibleHosterCount => FileHosters.Count(MatchesHosterFilter);

    /// <summary>
    /// "12 of 83 shown", for the hint beside the filter box. It exists because a filter that hides
    /// TICKED hosters would otherwise be invisible: the count is what tells the user the list they
    /// are looking at is not the list that will upload. (The Summary step shows the real destinations
    /// either way.)
    /// </summary>
    public string HosterFilterSummary => string.Format(
        CultureInfo.CurrentCulture,
        Localizer.Instance["Wizard_Step2_FilterCount_Format"],
        VisibleHosterCount,
        FileHosters.Count);

    /// <summary>
    /// The "tick everything listed" box in the hoster grid's Use column header. Tri-state: checked
    /// when every listed hoster is ticked, unchecked when none is, indeterminate when some are.
    /// <para>
    /// "Listed" means what the FILTER leaves visible, not the whole catalogue — with "Anonymous only"
    /// on, this ticks the anonymous hosters and nothing else, which is the entire point of having it
    /// next to a filter. It also skips hosters that cannot be used at all (no account, no anonymous):
    /// their row shows a padlock instead of a checkbox, and ticking them would be a state the grid
    /// has no way to show.
    /// </para>
    /// <para>
    /// Writing null is ignored, as on the tree's folder ticks: a three-state box cycles into
    /// indeterminate, and "make this selection partial" is not an instruction anyone can mean.
    /// </para>
    /// </summary>
    public bool? AllListedHostersChecked
    {
        get
        {
            bool anyTicked = false;
            bool anyUnticked = false;
            foreach (FileHosterSelectionViewModel hoster in ListedUsableHosters())
            {
                if (hoster.Use)
                {
                    anyTicked = true;
                }
                else
                {
                    anyUnticked = true;
                }

                if (anyTicked && anyUnticked)
                {
                    return null;
                }
            }

            return anyTicked;
        }

        set
        {
            if (value is not bool ticked)
            {
                return;
            }

            foreach (FileHosterSelectionViewModel hoster in ListedUsableHosters())
            {
                hoster.Use = ticked;
            }
        }
    }

    /// <summary>The rows the header box acts on: visible under the current filter, and usable.</summary>
    private IEnumerable<FileHosterSelectionViewModel> ListedUsableHosters()
        => FileHosters.Where(h => h.CanUse && MatchesHosterFilter(h));

    /// <summary>Resets both filters — the one-click way back to the whole list.</summary>
    [RelayCommand]
    private void ClearHosterFilter()
    {
        HosterFilterText = string.Empty;
        AnonymousHostersOnly = false;
    }

    partial void OnHosterFilterTextChanged(string value) => RaiseHosterFilterChanged();

    partial void OnAnonymousHostersOnlyChanged(bool value) => RaiseHosterFilterChanged();

    private void RaiseHosterFilterChanged()
    {
        OnPropertyChanged(nameof(IsHosterFilterActive));
        OnPropertyChanged(nameof(VisibleHosterCount));
        OnPropertyChanged(nameof(HosterFilterSummary));

        // Filtering changes WHICH rows the header box speaks for, so its own state moves with it.
        OnPropertyChanged(nameof(AllListedHostersChecked));

        HosterFilterInvalidated?.Invoke(this, EventArgs.Empty);
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduledMode))]
    public partial UploadStartMode StartMode { get; set; } = UploadStartMode.Immediately;

    [ObservableProperty]
    public partial DateTime ScheduledDate { get; set; } = DateTime.Now.Date.AddDays(1);

    [ObservableProperty]
    public partial string ScheduledTime { get; set; } = "00:00";

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
        1 => HasSelectedHoster && !_hasHardBlock,
        2 => !_summaryHasOverCapacity,
        _ => true,
    };

    /// <summary>
    /// Whether any hoster is ticked — the File Hosters step's own precondition for Next.
    /// <para>
    /// Deliberately independent of <see cref="HosterValidationWarnings"/>: those are computed from the
    /// pipeline registry and short-circuit when there isn't one, and "you picked nothing" is true
    /// regardless of what any pipeline declares. Without this the wizard walked on to a Summary that
    /// could only be empty, and Finish queued a package with no destination.
    /// </para>
    /// <para>
    /// Counts ticks across the WHOLE list, not the filtered view — a hoster ticked and then filtered
    /// out of sight still uploads, so it still satisfies this.
    /// </para>
    /// </summary>
    public bool HasSelectedHoster => FileHosters.Any(h => h.Use);

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
    /// <summary>
    /// The one account-state rule both wizard pages obey: an account that is switched off, or whose
    /// last verification failed, has no chance of accepting an upload — so its hoster is skipped
    /// entirely, exactly as if it had never been ticked. The synthetic Anonymous selection carries no
    /// such state and is never skipped.
    /// <para>
    /// Returns the resource key of the sentence to show, or null when the account is usable. Both
    /// callers go through this so they can't drift apart again: they did, and the symptom was a
    /// summary page that dropped every file with no explanation while the hoster page raised nothing.
    /// </para>
    /// </summary>
    private static string? UnusableAccountReason(FileHosterLoginDto account)
    {
        if (account.IsAnonymous)
        {
            return null;
        }

        if (account.Disabled)
        {
            return "Wizard_Hoster_AccountDisabled_Format";
        }

        // Checked BEFORE the status: an account whose stored session has run out is unusable no
        // matter how well its last check went, and the last check is often days old. The app knows
        // this without asking anything — see FileHosterLoginDto.HasExpiredSession for what it cost
        // to learn that at upload time instead.
        if (account.HasExpiredSession)
        {
            return "Wizard_Hoster_AccountSessionExpired_Format";
        }

        return account.CheckStatus == AccountCheckStatus.Failed
            ? "Wizard_Hoster_AccountCheckFailed_Format"
            : null;
    }

    private void RecomputeHosterValidation()
    {
        HosterValidationWarnings.Clear();
        _hasHardBlock = false;

        // Raised on both exits: the "no hoster ticked" gate is registry-independent, so it has to be
        // re-read even on the early return below.
        OnPropertyChanged(nameof(HasSelectedHoster));

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

            // An account the summary would skip is called out HERE, on the page where the hoster was
            // ticked. Until this ran, the two pages disagreed: this one counted every file as eligible
            // and let Next through, then the summary silently dropped the hoster and reported "N files
            // won't be uploaded to any hoster" — naming the files, but never the reason.
            if (account is not null && UnusableAccountReason(account) is string reasonKey)
            {
                HosterValidationWarnings.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance[reasonKey],
                    hoster.FileHosterName,
                    account.DisplayName));

                // Contributes nothing to anyHosterCanUploadSomething: if it was the only hoster ticked,
                // Next is blocked with the warning above rather than at the summary with none.
                continue;
            }
            long? hosterMaxFileSize = account is not null ? pipeline.MaxFileSizeFor(account) : pipeline.MaxFileSize;

            // Classify each selected file once against the hoster's per-file constraints. A file that
            // fails two checks (too big AND a name the hoster won't accept, e.g. Buzzheavier's '#'/';')
            // is counted under just one — size takes precedence — so eligibility never double-subtracts.
            List<string> oversizedNames = [];
            List<string> rejectedNameNames = [];
            List<string> rejectedTypeNames = [];
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
                else if (pipeline.RejectedFileExtensionReason(f.FileName) is not null)
                {
                    // Kept apart from the name rule because the user-facing sentence differs: telling
                    // someone "rls.r00" uses a character this hoster won't accept sends them hunting
                    // for a character that isn't there. The type is the problem, so the message says so.
                    rejectedTypeNames.Add(f.FileName);
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

            if (rejectedTypeNames.Count > 0)
            {
                HosterValidationWarnings.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance["Wizard_Hoster_FileTypeRejected_Format"],
                    hoster.FileHosterName,
                    string.Join("\n", rejectedTypeNames)));
            }

            eligibleForThisHoster -= oversizedNames.Count + rejectedNameNames.Count + rejectedTypeNames.Count;

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
            if (!_bulkLoadingFiles)
            {
                RecomputeHosterValidation();
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

    private void Hoster_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The Use toggle and switching account both affect eligibility — anonymous ⇄ a login
        // can change the per-file size and per-batch count caps (e.g. Hexload).
        if (e.PropertyName is nameof(FileHosterSelectionViewModel.Use)
            or nameof(FileHosterSelectionViewModel.SelectedAccount))
        {
            _summaryDirty = true;
            RecomputeHosterValidation();

            // One row's tick can flip the header box between all, none and partial.
            OnPropertyChanged(nameof(AllListedHostersChecked));
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

            // Account state filter — see UnusableAccountReason. The hoster page warns about this and
            // blocks Next, so reaching the summary with one of these is only possible when ANOTHER
            // hoster can still upload; dropping it here keeps the page showing only what will run.
            if (UnusableAccountReason(account) is not null)
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
    public partial bool Completed { get; set; }

    partial void OnFileFilterChanged(string value)
    {
        ApplyFilter();
    }


    // Prefill the scheduled date + time to NOW the moment the user picks Scheduled, so they adjust from the
    // current time rather than the tomorrow-at-midnight placeholder. Fires only on a real transition INTO
    // Scheduled (re-selecting the already-active mode raises no change), so it won't clobber edits unless the
    // user leaves Scheduled and returns. HH:mm to match the field's format hint.
    partial void OnStartModeChanged(UploadStartMode value)
    {
        if (value == UploadStartMode.Scheduled)
        {
            DateTime now = DateTime.Now;
            ScheduledDate = now.Date;
            ScheduledTime = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// "Add folder…" — appends each chosen folder's files (recursively) to the list. Several folders
    /// can be chosen in one dialog; each becomes its own <see cref="UploadSource"/>.
    /// </summary>
    [RelayCommand]
    private async Task AddFoldersAsync()
    {
        string? startAt = Sources.LastOrDefault(s => s.IsFolder)?.Path;
        string[]? folders = await dialogService.BrowseFoldersAsync(
            startAt,
            Localizer.Instance["Wizard_Step0_BrowseDialogTitle"]);

        if (folders is null || folders.Length == 0)
        {
            return;
        }

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
        string[]? picked = await dialogService.BrowseFilesAsync(
            Localizer.Instance["Wizard_Step0_Files_BrowseDialogTitle"]);

        if (picked is null || picked.Length == 0)
        {
            return;
        }

        AddFileSources(picked);
        SeedPackageTitleFromFirstSource();
        SourcesChanged();
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
            logger.Log(this, LogType.Error, $"Couldn't read {folder}: {ex.Message}");
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
    private async Task GoNextAsync()
    {
        if (CurrentStep == 0)
        {
            // One list, however it was filled — the folder walk and the file picker both append to it,
            // so there is a single thing to validate rather than a per-mode branch.
            if (Files.Count == 0)
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickAtLeastOneFile"]);
                return;
            }

            if (string.IsNullOrWhiteSpace(PackageTitle))
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_TitleRequired"]);
                return;
            }

            if (!Files.Any(f => f.IsSelected))
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickFile"]);
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

        // The account dialog's "Sign in" delegate is supplied here rather than resolved inside
        // DialogService. Constructor-injecting IAccountVerifier into DialogService would close a
        // DI cycle (DialogService → IAccountVerifier → IFileHosterRegistry → IFileHosterPipeline[]
        // → ExLoadPipeline → IInteractiveAuthService → WebViewInteractiveAuthService → IDialogService)
        // that MS.Extensions.DependencyInjection's cycle detector can't see — it closes through
        // sp.GetServices<IFileHosterPipeline>() inside a factory, which the detector treats as opaque,
        // so startup loops instead of throwing. The cycle only bites during singleton ctor-graph
        // construction; this VM is `new`-constructed after the graph is built and already holds the
        // verifier, so supplying the delegate at command time is safe.
        // The dialog does the checking, so a rejected password is corrected in place rather than
        // costing the user everything they typed: Save shows a status and disables itself, Cancel
        // stops the check, a failure is reported over the dialog and leaves it open, and only a
        // successful check closes it. Null here means the account was never proved — cancelled, or
        // abandoned after a failure — so nothing is written.
        //
        // Checking matters beyond catching typos: for several hosters it is the step that PRODUCES
        // the upload credential (FileMirage's api_token, DropMB's access_token, FileCat's SESS), and
        // the dialog stamps that onto the account it returns.
        FileHosterLoginDto? result = await dialogService.ShowAddAccountDialogAsync(
            hoster.FileHosterName,
            availableHosters,
            h => _accountVerifier!.CheckAsync(h, string.Empty, string.Empty, null),
            Localizer.Instance["EditAccount_AddTitle"],
            BuildAccountValidator(hoster.FileHosterName));

        if (result is null)
        {
            return;
        }

        result.CreatedDateTime ??= DateTime.Now;

        try
        {
            await fileHosterLoginRepository.InsertAsync(result);
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to save new {hoster.FileHosterName} account: {ex}");
            await dialogService.ShowErrorAsync(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return;
        }

        FileHosterLoginDto[] accounts = await FindSelectableAccountsAsync(hoster.FileHosterName);
        hoster.SetAccounts(accounts);

        // Auto-tick "Use" now that a WORKING account exists — saves the user a click and
        // matches the flow they were already in (they clicked "Add account…" because
        // they wanted to upload to this hoster).
        hoster.Use = true;
    }

    /// <summary>
    /// The check the Add Account dialog runs when Save is pressed, or null when this app has nothing
    /// to check with — no verifier, or a hoster with no pipeline — in which case Save closes at once
    /// rather than punishing the user for the app's own gap.
    /// </summary>
    private Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? BuildAccountValidator(string hosterName)
    {
        if (_accountVerifier is null || FileHosterClient.FindByHost(hosterName, Protocol.Http, logger) is null)
        {
            return null;
        }

        return (dto, ct) => _accountVerifier.CheckAsync(
            hosterName, dto.Username ?? string.Empty, dto.Password ?? string.Empty, dto.ApiKey, dto.SessionCookie, ct);
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
            _summaryDirty = true;
            RecomputeHosterValidation();
            NotifySelectionStats();
        }
    }

    // Clears Files, detaching each entry's PropertyChanged first: ObservableCollection.Clear() raises a Reset
    // with no OldItems, so Files_CollectionChanged can't unsubscribe them, and a lingering reference (e.g. a
    // Summary's File) would keep firing stale IsSelected changes into this VM.
    private void ClearFiles()
    {
        foreach (FileEntry entry in Files)
        {
            entry.PropertyChanged -= FileEntry_PropertyChanged;
        }

        Files.Clear();
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
    /// on it (the same split as the hoster grid's <see cref="HosterFilterInvalidated"/>).</summary>
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

    /// <summary>
    /// The accounts this hoster's dropdown may offer: the saved ones the user has left switched ON in
    /// Settings → Accounts. An account they unticked there is not a choice — putting it in the picker
    /// only invites selecting a hoster that everything downstream then skips
    /// (see <see cref="UnusableAccountReason"/>). A hoster whose only account is switched off falls
    /// back to anonymous if it supports it, and otherwise reads as having none, which is what it is.
    /// </summary>
    private async Task<FileHosterLoginDto[]> FindSelectableAccountsAsync(string fileHosterName)
        => [.. (await fileHosterLoginRepository.FindAsync(fileHosterName)).Where(a => !a.Disabled)];

    private async Task LoadFileHostersAsync()
    {
        if (FileHosters.Count > 0)
        {
            return;
        }

        foreach (string fileHosterName in FileHosterClient.NamesAlphabetical)
        {
            FileHosterLoginDto[] accounts = await FindSelectableAccountsAsync(fileHosterName);
            IFileHosterPipeline? pipeline = _fileHosterRegistry?.Find(fileHosterName);
            bool supportsAnonymous = pipeline?.SupportsAnonymousUpload ?? false;

            // Same account-vs-fallback rule RecomputeHosterValidation applies, so the "Max file
            // size" column always shows the number the oversize warning would enforce.
            Func<FileHosterLoginDto?, long?>? maxFileSizeResolver = pipeline is null
                ? null
                : account => account is not null ? pipeline.MaxFileSizeFor(account) : pipeline.MaxFileSize;

            // The same figure the scheduler caps this hoster's uploads by. A blank DTO stands in for
            // "no account chosen yet" so the column reads sensibly before a selection is made — every
            // pipeline that caps does so per tier at most, and none dereference the DTO.
            Func<FileHosterLoginDto?, int?>? maxConcurrentResolver = pipeline is null
                ? null
                : account => pipeline.MaxConcurrentUploadsFor(account ?? new FileHosterLoginDto { FileHosterName = fileHosterName });

            // How long the host keeps a file, for the "Kept for" column. The blank-DTO fallback reads
            // as a signed-in free tier, which is the tier a user adding an account would land on.
            Func<FileHosterLoginDto?, FileRetention>? retentionResolver = pipeline is null
                ? null
                : account => pipeline.RetentionFor(account ?? new FileHosterLoginDto { FileHosterName = fileHosterName });

            FileHosterSelectionViewModel? sticky = _stickyHosters.Find(
                h => string.Equals(h.FileHosterName, fileHosterName, StringComparison.Ordinal));

            FileHosterSelectionViewModel vm = new(
                fileHosterName, accounts, supportsAnonymous, maxFileSizeResolver, maxConcurrentResolver, retentionResolver);
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
            await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickHoster"]);
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
            await dialogService.ShowErrorAsync(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
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
