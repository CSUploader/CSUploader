// <copyright file="WizardHostersViewModel.cs" company="CSUploader">
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

/// <summary>
/// The Upload Wizard's File Hosters step: the hoster list, its filters and Use-column header,
/// per-hoster limit validation, and the in-step "Add account…" flow. Owned and constructed by
/// <see cref="UploadWizardViewModel"/>, which hands it the sources step's live <c>Files</c>
/// collection (one input of the validation) and the mark-summary-dirty callback; validation-state
/// changes are surfaced through <see cref="ValidationStateChanged"/> so the parent re-reads
/// <c>CanGoNext</c> at exactly the moments the pre-split code raised it.
/// </summary>
public sealed partial class WizardHostersViewModel : ObservableObject
{
    private readonly FileHosterLoginRepository _loginRepository;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    /// <summary>The sources step's live file list — validation classifies its selected entries
    /// against each ticked hoster's declared limits.</summary>
    private readonly ObservableCollection<FileEntry> _files;

    /// <summary>Parent-supplied: flags the Summary step for a rebuild on next entry.</summary>
    private readonly Action _markSummaryDirty;

    // Pipeline registry is optional so existing test fixtures that exercise non-validation
    // flows don't need to construct one. When null, no per-hoster limit validation runs
    // — the pipeline still pre-checks file size at upload time as the safety net.
    private readonly IFileHosterRegistry? _fileHosterRegistry;

    // Optional: the in-step Add Account dialog's verify delegate. Null in tests that never add.
    private readonly IAccountVerifier? _accountVerifier;

    private static readonly List<FileHosterSelectionViewModel> _stickyHosters = [];

    public WizardHostersViewModel(
        FileHosterLoginRepository loginRepository,
        IDialogService dialogService,
        IAppLogger logger,
        ObservableCollection<FileEntry> files,
        Action markSummaryDirty,
        IFileHosterRegistry? fileHosterRegistry = null,
        IAccountVerifier? accountVerifier = null)
    {
        _loginRepository = loginRepository;
        _dialogService = dialogService;
        _logger = logger;
        _files = files;
        _markSummaryDirty = markSummaryDirty;
        _fileHosterRegistry = fileHosterRegistry;
        _accountVerifier = accountVerifier;

        // Hook collection-changed once: any new entry into FileHosters has its PropertyChanged
        // subscribed so validation auto-refreshes on selection toggles, regardless of which code
        // path added it.
        FileHosters.CollectionChanged += FileHosters_CollectionChanged;
    }

    /// <summary>
    /// Raised whenever the validation outcome the parent's <c>CanGoNext</c> depends on may have
    /// changed (<see cref="HasSelectedHoster"/>, <see cref="HasHardBlock"/>) — the split's stand-in
    /// for the pre-split code raising <c>OnPropertyChanged(nameof(CanGoNext))</c> on the one object.
    /// Synchronous, from the same call sites.
    /// </summary>
    public event EventHandler? ValidationStateChanged;

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
        _markSummaryDirty();

        // The list the filter counts against just changed, so "N of M" has to move with it — the
        // hosters are added one by one during LoadFileHosters.
        OnPropertyChanged(nameof(VisibleHosterCount));
        OnPropertyChanged(nameof(HosterFilterSummary));
        OnPropertyChanged(nameof(AllListedHostersChecked));

        RecomputeHosterValidation();
    }

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    /// <summary>
    /// Name filter for the File Hosters step, matched case-insensitively anywhere in the hoster's
    /// name. Empty shows everything.
    /// </summary>
    [ObservableProperty]
    public partial string HosterFilterText { get; set; } = string.Empty;

    /// <summary>
    /// Narrows the File Hosters step by UPLOAD MODE: everything, only hosters that take uploads
    /// with no account, or only hosters that offer accounts. Combines with the other filters — all
    /// must match. Seeded from <see cref="Upload.AppSettings.WizardHosterAccountFilter"/> when the
    /// wizard opens, so someone who only ever uploads one way stops re-picking it every time.
    /// <para>
    /// The two narrowing modes are NOT each other's inverse — see <see cref="HosterAccountFilter"/>.
    /// A hoster that does both (catbox, gofile, upload.ee) is listed under either.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial HosterAccountFilter AccountFilter { get; set; }

    /// <summary>The three modes for the filter bar's dropdown. "Anonymous only" reuses the string
    /// the checkbox this replaced already had, in all six languages.</summary>
    public LocalizedOption<HosterAccountFilter>[] AccountFilterOptions { get; } =
    [
        new(HosterAccountFilter.Both, "Wizard_Step2_FilterAccountBoth"),
        new(HosterAccountFilter.AnonymousOnly, "Wizard_Step2_FilterAnonymous"),
        new(HosterAccountFilter.AccountOnly, "Wizard_Step2_FilterAccountOnly"),
    ];

    /// <summary>
    /// Narrows the File Hosters step to hosters whose ORDINARY free-download flow was verified not
    /// to require a captcha (<see cref="Upload.Pipeline.DownloadCaptchaRequirement.NotRequired"/>) —
    /// for picking destinations that put no puzzle in a downloader's way. Combines with the other
    /// filters; all must match.
    /// <para>
    /// Unverified hosters (the column's em dash) and hosters with no pipeline verdict are hidden,
    /// not kept: this filter promises no captcha, and Unknown has never meant that. See
    /// <c>docs/hoster-download-captcha.md</c>.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool NoDownloadCaptchaOnly { get; set; }

    /// <summary>
    /// Raised when any hoster filter changes. The head re-evaluates its DataGrid collection view
    /// in response — the same split the Uploads tab uses (<c>UploadsViewModel.FilterInvalidated</c>),
    /// which keeps this ViewModel framework-free and, more importantly, keeps the filter a VIEW
    /// concern: <see cref="FileHosters"/> itself is never touched, so a hoster ticked and then
    /// filtered out of sight still uploads.
    /// </summary>
    public event EventHandler? HosterFilterInvalidated;

    /// <summary>
    /// The File Hosters step's filter predicate, applied by the head to its collection view. Every
    /// active filter must match: the name contains <see cref="HosterFilterText"/> (case-insensitive,
    /// trimmed), AND — unless <see cref="AccountFilter"/> is
    /// <see cref="HosterAccountFilter.Both"/> — the hoster declares the matching capability, AND —
    /// when <see cref="NoDownloadCaptchaOnly"/> is set — its downloads were verified captcha-free
    /// (an unverified verdict does NOT pass; see that property).
    /// </summary>
    public bool MatchesHosterFilter(object item)
    {
        if (item is not FileHosterSelectionViewModel hoster)
        {
            return false;
        }

        // Each mode asks the hoster for the capability it names. AccountOnly is deliberately
        // SupportsAccounts and not !SupportsAnonymous: the two overlap, and inverting one would
        // hide every hoster that offers the user a choice of route.
        bool modeMatches = AccountFilter switch
        {
            HosterAccountFilter.AnonymousOnly => hoster.SupportsAnonymous,
            HosterAccountFilter.AccountOnly => hoster.SupportsAccounts,
            _ => true,
        };
        if (!modeMatches)
        {
            return false;
        }

        if (NoDownloadCaptchaOnly
            && hoster.DownloadCaptcha != Upload.Pipeline.DownloadCaptchaRequirement.NotRequired)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(HosterFilterText))
        {
            return true;
        }

        return hoster.FileHosterName.Contains(HosterFilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when any filter is narrowing the list — drives the "showing N of M" hint and
    /// the Clear button, both of which are noise when everything is visible.</summary>
    public bool IsHosterFilterActive
        => AccountFilter != HosterAccountFilter.Both
           || NoDownloadCaptchaOnly
           || !string.IsNullOrWhiteSpace(HosterFilterText);

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

    /// <summary>
    /// Resets all three filters — the one-click way back to the whole list. Clear means CLEAR, so
    /// the account mode returns to <see cref="HosterAccountFilter.Both"/> rather than to the
    /// configured startup mode: the button's job is to show everything, and a Clear that left rows
    /// hidden would be lying about what it did.
    /// </summary>
    [RelayCommand]
    private void ClearHosterFilter()
    {
        HosterFilterText = string.Empty;
        AccountFilter = HosterAccountFilter.Both;
        NoDownloadCaptchaOnly = false;
    }

    partial void OnHosterFilterTextChanged(string value) => RaiseHosterFilterChanged();

    partial void OnAccountFilterChanged(HosterAccountFilter value) => RaiseHosterFilterChanged();

    partial void OnNoDownloadCaptchaOnlyChanged(bool value) => RaiseHosterFilterChanged();

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

    /// <summary>The parent's <c>CanGoNext</c> half for the hoster step — see <see cref="_hasHardBlock"/>.</summary>
    internal bool HasHardBlock => _hasHardBlock;

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
    internal static string? UnusableAccountReason(FileHosterLoginDto account)
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

    /// <summary>
    /// Recomputes <see cref="HosterValidationWarnings"/> by walking each enabled hoster's
    /// declared limits and comparing against the current file selection. Size violations
    /// are reported with a filename list so the user knows exactly which files will be
    /// skipped; they only block Next when there's nothing left to upload anywhere. Count
    /// violations always block — the user has to decide which files to drop. Idempotent.
    /// No-op when no registry was injected (test fixtures).
    /// </summary>
    internal void RecomputeHosterValidation()
    {
        HosterValidationWarnings.Clear();
        _hasHardBlock = false;

        // Raised on both exits: the "no hoster ticked" gate is registry-independent, so it has to be
        // re-read even on the early return below.
        OnPropertyChanged(nameof(HasSelectedHoster));

        if (_fileHosterRegistry is null)
        {
            OnPropertyChanged(nameof(HasHosterValidationWarnings));
            ValidationStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        FileEntry[] selected = [.. _files.Where(f => f.IsSelected)];
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
                    // The roundness-picking base, like the "Max file size" column — the warning
                    // quotes the same cap and must never show a different number for it.
                    ByteUnit.FromBytesPreferRoundUnit(limitBytes).ToFriendlyString(),
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
        ValidationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Hoster_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The Use toggle and switching account both affect eligibility — anonymous ⇄ a login
        // can change the per-file size and per-batch count caps (e.g. Hexload).
        if (e.PropertyName is nameof(FileHosterSelectionViewModel.Use)
            or nameof(FileHosterSelectionViewModel.SelectedAccount))
        {
            _markSummaryDirty();
            RecomputeHosterValidation();

            // One row's tick can flip the header box between all, none and partial.
            OnPropertyChanged(nameof(AllListedHostersChecked));
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
        FileHosterLoginDto? result = await _dialogService.ShowAddAccountDialogAsync(
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
            await _loginRepository.InsertAsync(result);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to save new {hoster.FileHosterName} account: {ex}");
            await _dialogService.ShowErrorAsync(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
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
        if (_accountVerifier is null || FileHosterClient.FindByHost(hosterName, Protocol.Http, _logger) is null)
        {
            return null;
        }

        return (dto, ct) => _accountVerifier.CheckAsync(
            hosterName, dto.Username ?? string.Empty, dto.Password ?? string.Empty, dto.ApiKey, dto.SessionCookie, ct);
    }

    /// <summary>
    /// The accounts this hoster's dropdown may offer: the saved ones the user has left switched ON in
    /// Settings → Accounts. An account they unticked there is not a choice — putting it in the picker
    /// only invites selecting a hoster that everything downstream then skips
    /// (see <see cref="UnusableAccountReason"/>). A hoster whose only account is switched off falls
    /// back to anonymous if it supports it, and otherwise reads as having none, which is what it is.
    /// </summary>
    private async Task<FileHosterLoginDto[]> FindSelectableAccountsAsync(string fileHosterName)
        => [.. (await _loginRepository.FindAsync(fileHosterName)).Where(a => !a.Disabled)];

    internal async Task LoadFileHostersAsync()
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

            // Both default FALSE with no pipeline, matching supportsAnonymous above: an unregistered
            // hoster has declared nothing, and a capability filter must not claim one on its behalf.
            bool supportsAccounts = pipeline?.SupportsAccounts ?? false;

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
                fileHosterName,
                accounts,
                supportsAnonymous,
                supportsAccounts,
                maxFileSizeResolver,
                maxConcurrentResolver,
                retentionResolver,

                // Fixed per hoster, not per account — this reports the host's ordinary
                // free/anonymous download flow and intentionally ignores the uploader's
                // credentials. Null (blank cell) when the wizard has no pipeline to ask.
                pipeline?.DownloadCaptcha);
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

    internal void SaveStickySelections()
    {
        _stickyHosters.Clear();
        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            _stickyHosters.Add(hoster);
        }
    }
}
