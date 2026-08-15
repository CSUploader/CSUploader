// <copyright file="AccountManagerViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

/// <summary>
/// The Settings tab's account manager, split out of <see cref="SettingsViewModel"/> (which now owns
/// only the settings pages and exposes this as <see cref="SettingsViewModel.AccountManager"/>): the
/// Accounts grid, add/edit/remove, verification, Refresh all, and enable/disable with re-verify.
/// The split moved members, not behavior — every doc comment below travels from the original site.
/// </summary>
public partial class AccountManagerViewModel(
    FileHosterLoginRepository accountRepository,
    IDialogService dialogService,
    IAppLogger logger,
    IAccountVerifier accountVerifier,
    Upload.Pipeline.IFileHosterRegistry? fileHosterRegistry = null) : ObservableObject
{
    private readonly FileHosterLoginRepository _accountRepository = accountRepository;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IAppLogger _logger = logger;
    private readonly IAccountVerifier _accountVerifier = accountVerifier;

    // Supplies each hoster's capabilities; the one consulted here is SupportsAccounts. Optional so a
    // test that never opens the account dialog doesn't have to build a registry — when it's absent the
    // list falls back to every hoster, which is what this property did before.
    private readonly Upload.Pipeline.IFileHosterRegistry? _fileHosterRegistry = fileHosterRegistry;

    private static string Loc(string key) => Localizer.Instance[key];

    private static string LocF(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Localizer.Instance[key], args);

    [ObservableProperty]
    public partial FileHosterLoginDto? SelectedAccount { get; set; }

    /// <summary>The status line under the accounts grid — what the last add / check / refresh did.
    /// Written by every path that talks to a verifier, and bound by the Accounts page.</summary>
    [ObservableProperty]
    public partial string CheckAccountStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCheckingAccount { get; set; }

    public ObservableCollection<FileHosterLoginDto> Accounts { get; } = [];

    /// <summary>
    /// The hosters the account dialog may offer: everything except the drop hosts that have no login
    /// at all (see <see cref="Upload.Pipeline.IFileHosterPipeline.SupportsAccounts"/>). Offering
    /// GigaFile or temp.sh here offers to add an account that cannot exist — the only reachable
    /// outcome was a check failing with "this host has no accounts".
    /// </summary>
    public string[] AvailableHosters => _fileHosterRegistry is null
        ? [.. FileHosterClient.NamesAlphabetical]
        : [.. FileHosterClient.NamesAlphabetical.Where(HasAccounts)];

    /// <summary>True when the hoster has accounts, or when no pipeline is registered for it (an
    /// unknown name is left in rather than silently dropped).</summary>
    private bool HasAccounts(string hosterName)
        => _fileHosterRegistry?.Find(hosterName) is not { } pipeline || pipeline.SupportsAccounts;

    /// <summary>
    /// The list for EDITING an existing account, which must always contain that account's own hoster —
    /// otherwise an account saved before its host was reclassified (or before this filter existed)
    /// opens a combo that can't display its own value.
    /// </summary>
    /// <remarks><see cref="FileHosterLoginDto.FileHosterName"/> is nullable, and an account with no
    /// hoster name has nothing to preserve — appending the null would put a blank row in the combo.
    /// </remarks>
    private string[] HostersForEditing(FileHosterLoginDto account)
        => account.FileHosterName is not { Length: > 0 } own
           || AvailableHosters.Contains(own, StringComparer.Ordinal)
            ? AvailableHosters
            : [.. AvailableHosters.Append(own).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Re-reads the accounts grid from the repository. Fire-and-forget by design — the caller is a
    /// property-changed handler on the UI thread (the Settings tab becoming visible), which can't
    /// await. Exceptions are logged rather than thrown, because a failed refresh must never take the
    /// app down over what is only a staleness fix.
    /// <para>
    /// This exists because accounts can be created OUTSIDE this view — the upload wizard's
    /// "Add account…" writes straight to the repository — and this VM is a singleton that would
    /// otherwise keep the list it loaded at startup.
    /// </para>
    /// </summary>
    public void ReloadAccountsAsync()
    {
        _ = ReloadAccountsCoreAsync();

        async Task ReloadAccountsCoreAsync()
        {
            try
            {
                await LoadAccountsAsync();
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to refresh the accounts list: {ex.Message}");
            }
        }
    }

    internal async Task LoadAccountsAsync(CancellationToken cancellationToken = default)
    {
        // Preserve the selected row across the rebuild. SelectedItem binds to SelectedAccount, and
        // Clear()+Add() replaces every DTO instance with a fresh one, so without re-selecting by Id
        // the highlighted row is lost — e.g. after a right-click → Refresh reloads the grid.
        int? selectedId = SelectedAccount?.Id;

        Accounts.Clear();
        FileHosterLoginDto[] accounts = await _accountRepository.GetAllAsync(cancellationToken);
        List<string> expired = [];
        foreach (FileHosterLoginDto account in accounts)
        {
            // Say so the moment the list is read — this runs from MainViewModel.InitializeAsync, so
            // it IS the check at startup, and it costs nothing: the expiry is already on the row.
            //
            // It also switches the account OFF, keeping it out of the upload wizard's pickers
            // entirely rather than merely locking its row. Re-enabling is then the deliberate act
            // that re-verifies it (see ApplyEnabledStateAsync) — which is what stops this becoming a
            // merry-go-round: enabling used to be a plain flag flip, so an expired account would
            // sail straight back into the picker carrying the same dead session.
            if (account.HasExpiredSession && !account.Disabled)
            {
                account.SetCheckStatus(AccountCheckStatus.Failed, Localizer.Instance["Accounts_SessionExpired"]);
                account.Disabled = true;
                await _accountRepository.UpdateAsync(account, cancellationToken);

                // FileHosterName is nullable on the DTO (a half-built row can exist), and the log
                // line is for a human — DisplayName is what they would recognise anyway.
                expired.Add(account.FileHosterName ?? account.DisplayName);
            }

            Accounts.Add(account);
        }

        if (expired.Count > 0)
        {
            _logger.Log(
                this,
                LogType.Status,
                $"Stored sign-in has expired for: {string.Join(", ", expired)}. "
                + "Those accounts were switched off; enabling one in Settings → Accounts signs it in again.");
        }

        if (selectedId is int id)
        {
            FileHosterLoginDto? restored = null;
            foreach (FileHosterLoginDto account in Accounts)
            {
                if (account.Id == id)
                {
                    restored = account;
                    break;
                }
            }

            // Null when the account no longer exists (e.g. removed) — clearing the selection
            // is then the correct outcome.
            SelectedAccount = restored;
        }
    }

    [RelayCommand]
    private async Task AddAccountDialogAsync()
    {
        // Open EditAccountWindow in "add" mode with empty fields
        FileHosterLoginDto newAccount = new()
        {
            FileHosterName = AvailableHosters.FirstOrDefault() ?? string.Empty,
            AccountType = AccountType.Free,
        };

        FileHosterLoginDto? result = await _dialogService.ShowEditAccountDialogAsync(
            newAccount, AvailableHosters, InteractiveLoginAsync, Loc("EditAccount_AddTitle"));

        if (result is { } addResult)
        {
            await AddAccountFromDialogAsync(addResult);
        }
    }

    /// <summary>
    /// Routes credential verification through the injected <see cref="IAccountVerifier"/>.
    /// </summary>
    private Task<AccountCheckResult> VerifyCredentialsAsync(string hosterName, string username, string password, string? apiKey = null, string? sessionCookie = null, CancellationToken cancellationToken = default)
        => _accountVerifier.CheckAsync(hosterName, username, password, apiKey, sessionCookie, cancellationToken);

    /// <summary>Wall-clock that <see cref="FileHosterLoginDto.MarkRefreshed"/> sites stamp
    /// onto each DTO after a CheckAsync completes. Centralised so tests can compare against
    /// the same primitive the production code uses.</summary>
    private static DateTime NowLocal() => DateTime.Now;

    /// <summary>
    /// Drives the interactive (WebView) sign-in for an XFileSharing-API hoster from the
    /// EditAccountWindow's "Sign in" button. Runs the same verify flow as a no-API-key
    /// account check: pops the captcha WebView, scrapes my_account, derives the API key.
    /// Returned to the dialog so it can store the key + show the result.
    /// </summary>
    private Task<AccountCheckResult> InteractiveLoginAsync(string hosterName)
        => VerifyCredentialsAsync(hosterName, username: string.Empty, password: string.Empty, apiKey: null);

    /// <summary>
    /// Copies any session cookie returned by the verifier onto the credentials DTO so the
    /// next persist round-trip carries it. Currently only Ex-Load populates these fields —
    /// the WebView captures a cookie at credential-check time and we hand it forward so
    /// the first real upload doesn't have to re-pop the WebView. No-op for hosters whose
    /// verifier doesn't supply a cookie.
    /// </summary>
    /// <summary>Delegates to <see cref="Upload.AccountCheckOutcome.Apply"/> — shared with the upload
    /// wizard's add-account path, which needs the identical treatment (for several hosters the check
    /// is what produces the upload credential).</summary>
    private static void ApplySessionCookieIfPresent(FileHosterLoginDto target, AccountCheckResult result)
        => Upload.AccountCheckOutcome.Apply(target, result);

    /// <summary>
    /// Called by <see cref="AddAccountDialogAsync"/> after the dialog returns Save. Exposed as
    /// internal (not private) so the unit test can drive it without a real WPF window —
    /// the dialog wiring is the only WPF dependency in this whole flow.
    /// </summary>
    internal async Task AddAccountFromDialogAsync(FileHosterLoginDto dto)
    {
        // Two-phase add so the grid isn't blank for the ~3s the verifier takes:
        //   1. Insert the row up front with CheckStatus = Checking and reload the
        //      Accounts collection so it shows in the DataGrid immediately.
        //   2. Run the verifier, then UPDATE the row with the real result.
        // Hosters we don't have a pipeline for skip phase 2 entirely — the inserted
        // row gets CheckStatus = Unsupported so the colour converter paints it grey.
        var client = FileHosterClient.FindByHost(dto.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        bool willCheck = client is not null;

        if (willCheck)
        {
            dto.SetCheckStatus(AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
        }
        else
        {
            dto.SetCheckStatus(AccountCheckStatus.Unsupported, Loc("Settings_Accounts_Status_NoImpl"));
        }

        // Stamp the "Added at" time once, at creation. ??= so a value the dialog carried over
        // (it never does on an add) is respected, but a fresh account gets now.
        dto.CreatedDateTime ??= NowLocal();

        // Snapshot existing in-memory (status, message) pairs, insert, reload, restore.
        // Same dance RefreshAllAccountsAsync uses so other accounts' transient verify
        // state survives the round-trip through LoadAccountsAsync (both fields are
        // UI-only — reloading from the DB would otherwise reset them).
        Dictionary<int, RowStatus> statuses = BuildStatusMap();
        await _accountRepository.InsertAsync(dto);
        await LoadAccountsAsync();
        ApplyStatusMap(statuses);

        // The freshly-reloaded row for the new account picked up the DB defaults
        // (NotChecked / "Not checked") since neither field is persisted — stamp our
        // intended (Checking | Unsupported, message) onto it so the colour matches.
        UpdateAccountStatus(dto.Id, dto.CheckStatus, dto.StatusMessage);

        if (!willCheck)
        {
            CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", dto.FileHosterName);
            return;
        }

        IsCheckingAccount = true;
        CheckAccountStatus = Loc("Settings_Accounts_Status_Verifying");

        AccountCheckStatus finalStatus;
        string finalMessage;
        try
        {
            AccountCheckResult result = await VerifyCredentialsAsync(
                dto.FileHosterName ?? string.Empty,
                dto.Username ?? string.Empty,
                dto.Password ?? string.Empty,
                dto.ApiKey,
                dto.SessionCookie);

            if (result.IsValid)
            {
                dto.AccountType = result.AccountType;
                ApplySessionCookieIfPresent(dto, result);
                finalStatus = AccountCheckStatus.Valid;
                finalMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK");
            }
            else
            {
                // No "Failed: " prefix — CheckStatus drives the cell colour now, so the
                // row text is just the verifier's message (e.g. "Wrong password",
                // "The SSL connection could not be established...").
                finalStatus = AccountCheckStatus.Failed;
                finalMessage = result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed");
            }
        }
        catch (Exception ex)
        {
            // Transport/exception failures land in the same Failed bucket as verifier
            // IsValid=false — both are red cells to the user, and the message text
            // explains which.
            finalStatus = AccountCheckStatus.Failed;
            finalMessage = ex.Message;
        }
        finally
        {
            IsCheckingAccount = false;
        }

        // A failed check auto-disables the new account (persisted by UpdateAsync below and shown
        // when Accounts[i] = dto replaces the row); Valid/Unsupported leave it enabled.
        AutoDisableIfFailed(dto, finalStatus);

        // Real verifier outcome (success OR failure) → MarkRefreshed stamps CheckStatus,
        // StatusMessage AND LastRefreshedDateTime atomically; the row's grid column for
        // "Refreshed at" picks this up after Accounts[i] = dto below.
        dto.MarkRefreshed(finalStatus, finalMessage, NowLocal());
        await _accountRepository.UpdateAsync(dto);

        // Replace the in-memory row with the verified DTO so AccountType (which a
        // successful Premium check may have flipped from Free), CheckStatus and
        // StatusMessage all reflect the verifier's result. UpdateAccountStatus alone
        // would leave AccountType stuck at whatever LoadAccountsAsync saw before the
        // verify completed.
        for (int i = 0; i < Accounts.Count; i++)
        {
            if (Accounts[i].Id == dto.Id)
            {
                Accounts[i] = dto;
                break;
            }
        }

        CheckAccountStatus = LocF("Settings_Accounts_Status_AccountAdded_Format", dto.FileHosterName);
    }

    /// <summary>
    /// How many account checks may be in flight at once. Each is one HTTP round-trip to a different
    /// host, so the wall-clock saving is close to linear — twenty-five accounts one at a time is a
    /// long wait for something the user pressed once.
    /// <para>
    /// Ten rather than "all of them": every check builds an HttpHandler and some hosters answer
    /// slowly, and a burst of twenty-five simultaneous sign-in requests from one IP is exactly the
    /// shape of traffic that earns a rate-limit.
    /// </para>
    /// </summary>
    private const int MaxParallelAccountChecks = 10;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (Accounts.Count == 0)
        {
            CheckAccountStatus = Loc("Settings_Accounts_Status_NoAccountsToRefresh");
            return;
        }

        IsCheckingAccount = true;
        int checked_ = 0;
        int updated = 0;
        int needSignIn = 0;

        Dictionary<int, RowStatus> statuses = BuildStatusMap();
        List<FileHosterLoginDto> toCheck = [];

        foreach (FileHosterLoginDto account in Accounts.ToArray())
        {
            // Refresh-all runs unattended by nature: one press, every account. An account whose only
            // way back is a browser sign-in would open one — and over 25 accounts that is a queue of
            // popups, which is a worse bulk action than none. Report it instead; enabling that row
            // signs it in one at a time, with the user right there (ApplyEnabledStateAsync).
            if (NeedsInteractiveSignIn(account))
            {
                needSignIn++;
                statuses[account.Id] = new RowStatus(
                    AccountCheckStatus.Failed,
                    Loc("Accounts_SessionExpired"),
                    RefreshedAt: null);   // nothing was tried, so no timestamp is earned
                UpdateAccountStatus(account.Id, AccountCheckStatus.Failed, Loc("Accounts_SessionExpired"));
                continue;
            }

            toCheck.Add(account);
            UpdateAccountStatus(account.Id, AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
        }

        // Fan out, then apply each result as it lands. The verifier calls overlap; everything that
        // touches a DTO, the status map or the database happens HERE, in the awaiting context, one
        // result at a time — so none of it needs a lock, and the grid is never mutated from a worker
        // thread (this ViewModel has no dispatcher to marshal with).
        using SemaphoreSlim gate = new(MaxParallelAccountChecks);
        ConcurrentDictionary<string, SemaphoreSlim> perHosterGates = new(StringComparer.OrdinalIgnoreCase);
        List<Task<AccountCheckOutcomeForRow>> running =
            [.. toCheck.Select(a => CheckOneForRefreshAllAsync(a, gate, perHosterGates, cancellationToken))];

        await foreach (Task<AccountCheckOutcomeForRow> finished in Task.WhenEach(running).WithCancellation(cancellationToken))
        {
            AccountCheckOutcomeForRow outcome = await finished;
            FileHosterLoginDto account = outcome.Account;

            CheckAccountStatus = LocF(
                "Settings_Accounts_Status_CheckingProgress_Format",
                account.Username,
                account.FileHosterName,
                ++checked_,
                toCheck.Count);

            if (outcome.Result is null)
            {
                // No verifier ran (no implementation for this hoster, or the call threw). Either way
                // the row keeps whatever the outcome carried.
                statuses[account.Id] = outcome.Status;
                if (outcome.Status.RefreshedAt is { } failedAt)
                {
                    account.LastRefreshedDateTime = failedAt;
                    AutoDisableIfFailed(account, outcome.Status.Status);
                    try
                    { await _accountRepository.UpdateAsync(account, cancellationToken); }
                    catch { /* keep the primary failure visible */ }
                }

                UpdateAccountStatus(account.Id, outcome.Status.Status, outcome.Status.Message);
                continue;
            }

            AccountCheckResult result = outcome.Result;
            DateTime refreshedAt = outcome.Status.RefreshedAt ?? NowLocal();

            if (result.IsValid)
            {
                statuses[account.Id] = new RowStatus(
                    AccountCheckStatus.Valid,
                    result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK"),
                    refreshedAt);
                if (account.AccountType != result.AccountType)
                {
                    account.AccountType = result.AccountType;
                    updated++;
                }

                ApplySessionCookieIfPresent(account, result);
            }
            else
            {
                // CheckStatus drives the cell colour now — row text is just the verifier's message,
                // no "Failed: " prefix needed.
                statuses[account.Id] = new RowStatus(
                    AccountCheckStatus.Failed,
                    result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed"),
                    refreshedAt);
            }

            // Auto-disable on a failed check (no-op for Valid); persisted by the UpdateAsync below.
            AutoDisableIfFailed(account, statuses[account.Id].Status);
            account.LastRefreshedDateTime = refreshedAt;
            await _accountRepository.UpdateAsync(account, cancellationToken);

            RowStatus settled = statuses[account.Id];
            UpdateAccountStatus(account.Id, settled.Status, settled.Message);
        }

        IsCheckingAccount = false;
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        CheckAccountStatus = needSignIn > 0
            ? LocF("Settings_Accounts_Status_RefreshSummaryWithSignIn_Format", checked_, updated, needSignIn)
            : LocF("Settings_Accounts_Status_RefreshSummary_Format", checked_, updated);
    }

    /// <summary>
    /// One account's verifier round-trip for <see cref="RefreshAllAccountsAsync"/>, run under two
    /// gates and touching nothing shared.
    /// <para>
    /// The second gate is per HOSTER, width one. Two accounts on the same host must not be checked at
    /// the same moment: several of these sign-ins are rate-limited per account or per IP — UploadGIG
    /// answers a second login within the minute with "you can't login a few minutes" — and the point
    /// of the fan-out is to overlap DIFFERENT hosts, which it still does.
    /// </para>
    /// </summary>
    private async Task<AccountCheckOutcomeForRow> CheckOneForRefreshAllAsync(
        FileHosterLoginDto account,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, SemaphoreSlim> perHosterGates,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim hosterGate = perHosterGates.GetOrAdd(account.FileHosterName ?? string.Empty, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            await hosterGate.WaitAsync(cancellationToken);
            try
            {
                var client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
                if (client is null)
                {
                    // No FileHosterClient implementation → no verifier round-trip happened →
                    // RefreshedAt stays null (we didn't actually try anything).
                    return new AccountCheckOutcomeForRow(
                        account,
                        null,
                        new RowStatus(AccountCheckStatus.Unsupported, Loc("Settings_Accounts_Status_NoImpl"), RefreshedAt: null));
                }

                AccountCheckResult result = await VerifyCredentialsAsync(
                    account.FileHosterName ?? string.Empty,
                    account.Username ?? string.Empty,
                    account.Password ?? string.Empty,
                    account.ApiKey,
                    account.SessionCookie,
                    cancellationToken);

                // Single stamp covers both Valid and !Valid branches — we tried, so the timestamp
                // reflects the attempt regardless of outcome.
                return new AccountCheckOutcomeForRow(account, result, new RowStatus(AccountCheckStatus.Checking, string.Empty, NowLocal()));
            }
            catch (Exception ex)
            {
                // Transport exceptions and verifier IsValid=false both bucket as Failed (red cell).
                // The user sees the message text to distinguish; no separate "Error" colour.
                return new AccountCheckOutcomeForRow(
                    account,
                    null,
                    new RowStatus(AccountCheckStatus.Failed, ex.Message, NowLocal()));
            }
            finally
            {
                hosterGate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>What one parallel check produced: the row it belongs to, the verifier's answer when
    /// there was one, and the status to fall back on when there wasn't.</summary>
    private sealed record AccountCheckOutcomeForRow(FileHosterLoginDto Account, AccountCheckResult? Result, RowStatus Status);

    /// <summary>
    /// True when checking this account could only proceed by opening a sign-in browser — because its
    /// stored session has run out, or because it is a browser-sign-in hoster holding no credential at
    /// all yet.
    /// <para>
    /// Keyed on the CREDENTIAL rather than a list of hoster names: BowFile signs in through the
    /// browser and is not in <c>HosterCredentialModes</c>'s session-cookie list, which describes what
    /// the Add Account dialog shows, not what a check would do.
    /// </para>
    /// <para>
    /// It cannot catch a session that the host dropped EARLY — the stored expiry still looks fine, so
    /// the check runs and the pipeline may open the window anyway. Knowing that needs a request, and
    /// this is the path that must not make them.
    /// </para>
    /// </summary>
    private static bool NeedsInteractiveSignIn(FileHosterLoginDto account)
        => account.HasExpiredSession
           || (HosterCredentialModes.IsWebViewSignInHoster(account.FileHosterName)
               && string.IsNullOrEmpty(account.SessionCookie)
               && string.IsNullOrEmpty(account.ApiKey));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        string message = targets.Length == 1
            ? LocF("Settings_Accounts_Remove_Message_Format", targets[0].Username, targets[0].FileHosterName)
            : LocF("Settings_Accounts_Remove_MessageBulk_Format", targets.Length);

        if (!await _dialogService.ShowOptOutConfirmationAsync(
                ConfirmationKeys.RemoveFileHosterAccount,
                message,
                Loc("Settings_Accounts_Remove_Title")))
        {
            return;
        }

        foreach (FileHosterLoginDto account in targets)
        {
            await _accountRepository.DeleteAsync(account.Id, cancellationToken);
        }
        await LoadAccountsAsync(cancellationToken);
    }

    /// <summary>
    /// Coerces a XAML-bound <see cref="IList"/> CommandParameter (DataGrid.SelectedItems
    /// is non-generic) into a typed array snapshot. Returning an empty array on no
    /// selection lets RelayCommand callers do a simple length check rather than handle
    /// null.
    /// </summary>
    private static FileHosterLoginDto[] ResolveAccountTargets(IList? selectedItems)
        => selectedItems is null
            ? []
            : [.. selectedItems.OfType<FileHosterLoginDto>()];

    [RelayCommand]
    private async Task EditAccountAsync()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        // Open edit dialog. No title override → the window keeps its XAML default title.
        FileHosterLoginDto? result = await _dialogService.ShowEditAccountDialogAsync(
            SelectedAccount, HostersForEditing(SelectedAccount), InteractiveLoginAsync);

        if (result is { } editResult)
        {
            // Awaited, not fire-and-forget: the save now re-verifies, and a discarded task would both
            // swallow any failure and let the caller carry on while the credentials are still in flight.
            await SaveEditedAccountAsync(editResult);
        }
    }

    /// <summary>
    /// Persists an edited account and then <b>re-checks it</b>, exactly as Add does.
    /// <para>
    /// Editing is nearly always a correction — a fixed username or password — so leaving the row on its
    /// old verdict shows a stale (often red) status for credentials that are now right, and the user has
    /// no way to tell the difference without hunting for Refresh. The re-check also re-derives whatever
    /// the verifier owns: for hosters whose real upload credential is an API key obtained at sign-in
    /// (FileMirage's token, Pixeldrain's auth_key) it is what makes the corrected account able to upload
    /// at all.
    /// </para>
    /// </summary>
    /// <remarks>Internal, like <see cref="AddAccountFromDialogAsync"/>, so the unit tests can drive the
    /// post-dialog half without a real window.</remarks>
    internal async Task SaveEditedAccountAsync(FileHosterLoginDto updated)
    {
        await _accountRepository.UpdateAsync(updated);
        await LoadAccountsAsync();

        // Re-check against the reloaded row, so the status/stamp land on the instance the grid is
        // actually showing. Falling back to the saved DTO keeps this working headlessly.
        FileHosterLoginDto target = Accounts.FirstOrDefault(a => a.Id == updated.Id) ?? updated;

        IsCheckingAccount = true;
        try
        {
            RowStatus settled = await RefreshSingleAccountAsync(target, 1, 1, CancellationToken.None);
            if (settled.RefreshedAt is { } stamp)
            {
                target.MarkRefreshed(settled.Status, settled.Message, stamp);
            }
            else
            {
                target.SetCheckStatus(settled.Status, settled.Message);
            }
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    // AllowConcurrentExecutions on every async-but-context-menu command on this VM —
    // CommunityToolkit's default AsyncRelayCommand makes CanExecute=!IsRunning, so a
    // single hung Rapidgator API call would leave the context-menu entries permanently
    // greyed out for the rest of the session. Save/Add stay non-concurrent because
    // they'd otherwise double-insert.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        IsCheckingAccount = true;
        try
        {
            for (int i = 0; i < targets.Length; i++)
            {
                // targets come from ResolveAccountTargets → the grid's SelectedItems, which
                // are the live Accounts instances. RefreshSingleAccountAsync already mutates
                // this same instance in place (LastRefreshedDateTime, AccountType, session,
                // Storage*) and persists it.
                FileHosterLoginDto account = targets[i];
                RowStatus settled = await RefreshSingleAccountAsync(account, i + 1, targets.Length, cancellationToken);

                // Apply the final outcome to the SAME live row instance — no reload, so the
                // grid updates in place (DTO is now observable) and the selection/highlight is
                // preserved naturally.
                if (settled.RefreshedAt is { } stamp)
                {
                    account.MarkRefreshed(settled.Status, settled.Message, stamp);
                }
                else
                {
                    account.SetCheckStatus(settled.Status, settled.Message);
                }
            }
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    /// <summary>
    /// Runs one row's verification round-trip, updating the grid's status cell and the
    /// global progress text. Returns the <see cref="RowStatus"/> the caller should drop
    /// into its status map before reloading.
    /// </summary>
    private async Task<RowStatus> RefreshSingleAccountAsync(FileHosterLoginDto account, int oneBasedIndex, int total, CancellationToken cancellationToken)
    {
        var client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is null)
        {
            string noImpl = LocF("Settings_Accounts_Status_NoImpl_Format", account.FileHosterName);
            CheckAccountStatus = noImpl;
            // No verifier ran → no "Refreshed at" stamp.
            return new RowStatus(AccountCheckStatus.Unsupported, noImpl, RefreshedAt: null);
        }

        int accountId = account.Id;
        string username = account.Username ?? string.Empty;
        string password = account.Password ?? string.Empty;

        CheckAccountStatus = LocF("Settings_Accounts_Status_CheckingProgress_Format", username, account.FileHosterName, oneBasedIndex, total);
        UpdateAccountStatus(accountId, AccountCheckStatus.Checking, Loc("Settings_Accounts_Status_CheckingShort"));
        await Task.Yield();

        try
        {
            AccountCheckResult result = await VerifyCredentialsAsync(account.FileHosterName ?? string.Empty, username, password, account.ApiKey, account.SessionCookie, cancellationToken);

            // Single stamp covers Valid / !Valid / catch — we did call the verifier, so
            // the timestamp reflects the attempt regardless of outcome.
            DateTime refreshedAt = NowLocal();
            account.LastRefreshedDateTime = refreshedAt;

            if (result.IsValid)
            {
                account.AccountType = result.AccountType;
                ApplySessionCookieIfPresent(account, result);
                CheckAccountStatus = LocF("Settings_Accounts_Status_Valid_Format", result.Message);
                await _accountRepository.UpdateAsync(account, cancellationToken);
                return new RowStatus(
                    AccountCheckStatus.Valid,
                    result.Message ?? Loc("Settings_Accounts_DefaultStatus_OK"),
                    refreshedAt);
            }

            // CheckStatus drives the cell colour now — the row text is just the
            // verifier's message. The global status bar (CheckAccountStatus) keeps
            // its "Failed: " prefix because it has no colour and needs the prefix
            // to convey outcome.
            CheckAccountStatus = LocF("Settings_Accounts_Status_Failed_Format", result.Message);
            // Auto-disable on a failed check; the live row unticks/dims via INPC (no reload here).
            AutoDisableIfFailed(account, AccountCheckStatus.Failed);
            await _accountRepository.UpdateAsync(account, cancellationToken);
            return new RowStatus(
                AccountCheckStatus.Failed,
                result.Message ?? Loc("Settings_Accounts_DefaultStatus_Failed"),
                refreshedAt);
        }
        catch (Exception ex)
        {
            // Pre-fix this only updated the global status bar; the row was left stuck on
            // "Checking..." with no indication of failure. Now the row also turns red
            // via CheckStatus = Failed.
            DateTime refreshedAt = NowLocal();
            account.LastRefreshedDateTime = refreshedAt;
            AutoDisableIfFailed(account, AccountCheckStatus.Failed);
            // Persist the timestamp even on transport failure. Swallow secondary DB
            // errors so they don't mask the verifier exception that's about to surface
            // in the row's status cell.
            try
            { await _accountRepository.UpdateAsync(account, cancellationToken); }
            catch { /* keep the primary failure visible */ }
            CheckAccountStatus = LocF("Settings_Accounts_Status_Error_Format", ex.Message);
            return new RowStatus(AccountCheckStatus.Failed, ex.Message, refreshedAt);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task EnableSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
        => ApplyEnabledStateAsync(selectedItems, disable: false, cancellationToken);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DisableSelectedAccountsAsync(IList? selectedItems, CancellationToken cancellationToken = default)
        => ApplyEnabledStateAsync(selectedItems, disable: true, cancellationToken);

    private async Task ApplyEnabledStateAsync(IList? selectedItems, bool disable, CancellationToken cancellationToken)
    {
        FileHosterLoginDto[] targets = ResolveAccountTargets(selectedItems);
        if (targets.Length == 0)
        {
            return;
        }

        // Enabling an account whose stored session has run out RE-VERIFIES it rather than just
        // flipping the flag. Without this the switch would be a lie: the account returns to the
        // wizard's picker carrying the same dead session, fails every file at upload time, and gets
        // switched off again at the next start. For a hoster that signs in through the browser this
        // is where that window opens — which is the point, because the user is here.
        List<FileHosterLoginDto> reverify = disable
            ? []
            : [.. targets.Where(a => a.HasExpiredSession)];

        foreach (FileHosterLoginDto account in targets)
        {
            account.Disabled = disable;
            await _accountRepository.UpdateAsync(account, cancellationToken);
        }

        if (reverify.Count > 0)
        {
            IsCheckingAccount = true;
            try
            {
                for (int i = 0; i < reverify.Count; i++)
                {
                    FileHosterLoginDto account = reverify[i];
                    RowStatus settled = await RefreshSingleAccountAsync(account, i + 1, reverify.Count, cancellationToken);

                    if (settled.RefreshedAt is { } stamp)
                    {
                        account.MarkRefreshed(settled.Status, settled.Message, stamp);
                    }
                    else
                    {
                        account.SetCheckStatus(settled.Status, settled.Message);
                    }

                    // Nothing switches it back off here on purpose. A failed check never applies a
                    // returned session (that copy is guarded by IsValid), so the account is still
                    // expired when the reload below runs — and the reload's own rule is what puts it
                    // off, persisted. A second disable here would be a rule no test could tell apart
                    // from that one.
                }
            }
            finally
            {
                IsCheckingAccount = false;
            }
        }

        Dictionary<int, RowStatus> statuses = BuildStatusMap();
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        // A re-verify that failed switched the account back off, so "enabled" would be a false
        // report — the row's own status already says what went wrong; leave it on screen.
        if (reverify.Any(a => a.Disabled))
        {
            return;
        }

        if (targets.Length == 1)
        {
            string username = targets[0].Username ?? string.Empty;
            CheckAccountStatus = disable
                ? LocF("Settings_Accounts_Status_AccountDisabled_Format", username)
                : LocF("Settings_Accounts_Status_AccountEnabled_Format", username);
        }
        else
        {
            CheckAccountStatus = LocF(
                disable ? "Settings_Accounts_Status_AccountsBulkDisabled_Format" : "Settings_Accounts_Status_AccountsBulkEnabled_Format",
                targets.Length);
        }
    }

    /// <summary>
    /// A check that settles <see cref="AccountCheckStatus.Failed"/> auto-disables the account, so a
    /// broken account (bad credentials, dead host) is excluded from uploads until it's fixed and
    /// re-enabled. Only Failed disables: <see cref="AccountCheckStatus.Valid"/> leaves the flag
    /// untouched — a passing re-check does NOT auto-re-enable an account a failure (or the user)
    /// disabled, which stays a deliberate choice via the Enable context-menu action — and
    /// <see cref="AccountCheckStatus.Unsupported"/> means "no verifier for this hoster", not "broken".
    /// Mutates only the in-memory flag; the caller's own UpdateAsync persists it, and
    /// <see cref="FileHosterLoginDto.Disabled"/> raises PropertyChanged so the Accounts grid unticks
    /// (and dims) the row live even on the no-reload Refresh-selected path.
    /// </summary>
    private static void AutoDisableIfFailed(FileHosterLoginDto account, AccountCheckStatus status)
        => Upload.AccountCheckOutcome.AutoDisableIfFailed(account, status);

    // ── Helpers for preserving check status across reloads ──

    /// <summary>(CheckStatus, StatusMessage, RefreshedAt) triple preserved across a
    /// LoadAccountsAsync round-trip. CheckStatus + StatusMessage are UI-only so they'd
    /// otherwise reset to (NotChecked, "Not checked") on every reload; RefreshedAt is
    /// persisted but the snapshot path lets the in-flight RefreshAll loop replay a
    /// freshly-stamped value without re-reading the DB row first.</summary>
    private readonly record struct RowStatus(AccountCheckStatus Status, string Message, DateTime? RefreshedAt);

    private Dictionary<int, RowStatus> BuildStatusMap()
        => Accounts.ToDictionary(a => a.Id, a => new RowStatus(a.CheckStatus, a.StatusMessage, a.LastRefreshedDateTime));

    private static void ApplyStatusMap(Dictionary<int, RowStatus> statuses, IEnumerable<FileHosterLoginDto> accounts)
    {
        foreach (FileHosterLoginDto a in accounts)
        {
            if (statuses.TryGetValue(a.Id, out RowStatus row))
            {
                if (row.RefreshedAt is { } stamp)
                {
                    // Snapshot came from a real verifier round-trip — replay the timestamp too.
                    a.MarkRefreshed(row.Status, row.Message, stamp);
                }
                else
                {
                    // Snapshot came from a non-verification path (Enable/Disable, RemoveSelected,
                    // or a hoster with no implementation) — don't synthesize a refresh stamp.
                    a.SetCheckStatus(row.Status, row.Message);
                }
            }
        }
    }

    private void ApplyStatusMap(Dictionary<int, RowStatus> statuses) => ApplyStatusMap(statuses, Accounts);

    /// <summary>
    /// Updates an account's <see cref="FileHosterLoginDto.CheckStatus"/> and
    /// <see cref="FileHosterLoginDto.StatusMessage"/> together, IN PLACE on the live
    /// <see cref="Accounts"/> instance. <see cref="FileHosterLoginDto"/> now raises
    /// PropertyChanged for those fields, so mutating the existing item re-renders its row
    /// — no need to replace the item (the old copy-every-field workaround), which also
    /// dropped the selection highlight.
    /// </summary>
    private void UpdateAccountStatus(int accountId, AccountCheckStatus status, string message)
    {
        foreach (FileHosterLoginDto account in Accounts)
        {
            if (account.Id == accountId)
            {
                account.SetCheckStatus(status, message);
                return;
            }
        }
    }
}
