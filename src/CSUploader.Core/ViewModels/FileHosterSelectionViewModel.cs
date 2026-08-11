// <copyright file="FileHosterSelectionViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;

namespace CSUploader.ViewModels;

public partial class FileHosterSelectionViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool Use { get; set; }

    [ObservableProperty]
    public partial FileHosterLoginDto? SelectedAccount { get; set; }

    // Synthetic, non-persisted "Anonymous" entry shown in the account dropdown for hosters
    // that support anonymous upload. Null when the hoster doesn't. The pipeline routes on its
    // IsAnonymous flag; its Username carries the localized "(anonymous)" label for display.
    private readonly FileHosterLoginDto? _anonymousOption;

    // Resolves the hoster's per-file size cap (bytes, null = no cap) for a given account —
    // the cap can vary by tier (e.g. Hexload's anonymous tier differs from its API tier), so
    // the "Max file size" column re-resolves whenever the account dropdown changes. Null when
    // the wizard has no pipeline registry (tests).
    private readonly Func<FileHosterLoginDto?, long?>? _maxFileSizeResolver;

    // Resolves how many of this hoster's uploads may run at once for a given account (null = no
    // limit). Most hosters declare none; a few cap it, either because they say so (ufile's tiers) or
    // because their anti-abuse does (DropMeFiles). Like the size cap this can vary by tier, so the
    // column re-resolves when the account dropdown changes. Null when the wizard has no pipeline
    // registry (tests).
    private readonly Func<FileHosterLoginDto?, int?>? _maxConcurrentResolver;

    public FileHosterSelectionViewModel(
        string fileHosterName,
        FileHosterLoginDto[] accounts,
        bool supportsAnonymous = false,
        Func<FileHosterLoginDto?, long?>? maxFileSizeResolver = null,
        Func<FileHosterLoginDto?, int?>? maxConcurrentResolver = null)
    {
        FileHosterName = fileHosterName;
        Accounts = accounts;
        SupportsAnonymous = supportsAnonymous;
        _maxFileSizeResolver = maxFileSizeResolver;
        _maxConcurrentResolver = maxConcurrentResolver;

        _anonymousOption = supportsAnonymous
            ? new FileHosterLoginDto
            {
                FileHosterName = fileHosterName,
                IsAnonymous = true,
                Username = Localizer.Instance["Wizard_Step2_AccountAnonymous"],
            }
            : null;

        AccountOptions = BuildAccountOptions();

        // Prefer a real account when one exists; fall back to the Anonymous option.
        SelectedAccount = accounts.Length > 0 ? accounts[0] : _anonymousOption;
    }

    public string FileHosterName { get; }

    public FileHosterLoginDto[] Accounts { get; private set; }

    /// <summary>
    /// What the wizard's account dropdown binds to: the saved <see cref="Accounts"/> plus a
    /// synthetic "Anonymous" entry appended when <see cref="SupportsAnonymous"/>. Lets the user
    /// pick anonymous upload from the same picker as their real accounts.
    /// </summary>
    public IReadOnlyList<FileHosterLoginDto> AccountOptions { get; private set; }

    public bool HasAccounts => Accounts.Length > 0;

    /// <summary>
    /// True when the hoster's pipeline accepts uploads with no login (e.g. GigaPeta). Fixed
    /// at construction from <see cref="Upload.Pipeline.IFileHosterPipeline.SupportsAnonymousUpload"/>.
    /// </summary>
    public bool SupportsAnonymous { get; }

    /// <summary>
    /// Whether this row can be uploaded to at all: it has a saved account to pick, OR the
    /// hoster supports anonymous upload. Drives the wizard's "Use" gate and the row's
    /// enabled/blocked visuals — replacing the old <see cref="HasAccounts"/>-only gate that
    /// treated every account-less hoster as unusable.
    /// </summary>
    public bool CanUse => (HasAccounts || SupportsAnonymous) && !SelectedAccountSessionExpired;

    /// <summary>
    /// True when the account currently chosen for this hoster signs in with a stored session and
    /// that session has run out. The row then locks: ticking it would queue files that can only fail
    /// at upload time, asking for a browser sign-in nobody may be there to answer.
    /// <para>
    /// Per-ACCOUNT rather than per-hoster on purpose — picking a different account from the dropdown
    /// unlocks the row, which is the fix when a second account exists.
    /// </para>
    /// </summary>
    public bool SelectedAccountSessionExpired => SelectedAccount is { HasExpiredSession: true };

    /// <summary>What the padlock says, since the row can be locked for two different reasons and
    /// "add an account" is unhelpful advice when one is already there.</summary>
    public string UseBlockedTooltip => Localizer.Instance[
        SelectedAccountSessionExpired
            ? "Wizard_Step2_SessionExpiredTooltip"
            : "Wizard_Step2_AccountRequiredTooltip"];

    public string AccountDisplayText
    {
        get
        {
            if (!HasAccounts)
            {
                return Localizer.Instance["Wizard_Step2_AccountAnonymous"];
            }

            // DisplayName falls back to a masked API key for key-only accounts (no username);
            // it's empty only when the account has neither, which reads as "pick one".
            string? name = SelectedAccount?.DisplayName;
            return string.IsNullOrEmpty(name) ? Localizer.Instance["Wizard_Step2_AccountSelect"] : name;
        }
    }

    /// <summary>
    /// The hoster's per-file size cap in bytes for the currently selected account, or null when
    /// the hoster has no cap (or no resolver was supplied). Drives the "Max file size" column's
    /// sort order; the cell text comes from <see cref="MaxFileSizeDisplay"/>.
    /// </summary>
    public long? MaxFileSizeBytes => _maxFileSizeResolver?.Invoke(SelectedAccount);

    /// <summary>
    /// Friendly per-file size cap for the "Max file size" column (e.g. "5.12 GiB"), or the
    /// localized "No limit" when the hoster doesn't cap. Empty when no resolver was supplied.
    /// Same binary-unit formatting as the step-2 oversize warning, so the column and the
    /// warning never show two different numbers for the same cap.
    /// </summary>
    public string MaxFileSizeDisplay
    {
        get
        {
            if (_maxFileSizeResolver is null)
            {
                return string.Empty;
            }

            return MaxFileSizeBytes is long maxBytes
                ? ByteUnit.FromBytes(maxBytes, ByteBase.Binary).ToFriendlyString()
                : Localizer.Instance["Wizard_Step2_NoLimit"];
        }
    }

    /// <summary>
    /// How many uploads this hoster will accept at once for the selected account, or null when it
    /// declares no limit (the common case). Drives the "Max parallel" column's sort order; the cell
    /// text comes from <see cref="MaxConcurrentUploadsDisplay"/>.
    /// </summary>
    public int? MaxConcurrentUploads => _maxConcurrentResolver?.Invoke(SelectedAccount);

    /// <summary>
    /// The "Max parallel" column's text: the number, or the same localized "No limit" the size
    /// column uses when the hoster declares none. Empty when no resolver was supplied.
    /// <para>
    /// This is the hoster's OWN ceiling, not the effective one — the scheduler takes the smaller of
    /// this and the user's per-host setting, so a row saying "No limit" is still bounded by
    /// Settings.
    /// </para>
    /// </summary>
    public string MaxConcurrentUploadsDisplay
    {
        get
        {
            if (_maxConcurrentResolver is null)
            {
                return string.Empty;
            }

            return MaxConcurrentUploads is int max
                ? max.ToString(System.Globalization.CultureInfo.CurrentCulture)
                : Localizer.Instance["Wizard_Step2_NoLimit"];
        }
    }

    /// <summary>
    /// Replaces the available accounts (e.g. after the user adds one through the
    /// wizard's "Add account…" link). Preserves the current selection if it survives,
    /// otherwise selects the first account, and fires change notifications so the
    /// row's account picker, "Use" gate, and display text refresh in place.
    /// </summary>
    public void SetAccounts(FileHosterLoginDto[] accounts)
    {
        bool wasAnonymousSelected = SelectedAccount?.IsAnonymous == true;
        int? previousId = wasAnonymousSelected ? null : SelectedAccount?.Id;

        Accounts = accounts;
        AccountOptions = BuildAccountOptions();
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(AccountOptions));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(CanUse));

        FileHosterLoginDto? next = previousId is int id ? Array.Find(accounts, a => a.Id == id) : null;
        SelectedAccount = next
            ?? (wasAnonymousSelected ? _anonymousOption : null)
            ?? (accounts.Length > 0 ? accounts[0] : _anonymousOption);

        if (!CanUse && Use)
        {
            Use = false;
        }

        OnPropertyChanged(nameof(AccountDisplayText));
    }

    private IReadOnlyList<FileHosterLoginDto> BuildAccountOptions()
    {
        if (_anonymousOption is null)
        {
            return Accounts;
        }

        return [.. Accounts, _anonymousOption];
    }

    partial void OnUseChanged(bool value)
    {
        // Defense in depth: a row can be "used" only if it has an account to pick OR the
        // hoster supports anonymous upload (GigaPeta). Without either, a binding glitch or a
        // rehydrated sticky selection must not be able to leave the row checked — that flow
        // falls back to a blank login DTO at upload time and the upload is guaranteed to fail.
        if (value && !CanUse)
        {
            Use = false;
        }
    }

    partial void OnSelectedAccountChanged(FileHosterLoginDto? value)
    {
        OnPropertyChanged(nameof(AccountDisplayText));

        // Both caps can differ per account tier — keep the "Max file size" and "Max parallel"
        // columns in sync with the dropdown.
        OnPropertyChanged(nameof(MaxFileSizeBytes));
        OnPropertyChanged(nameof(MaxFileSizeDisplay));
        OnPropertyChanged(nameof(MaxConcurrentUploads));
        OnPropertyChanged(nameof(MaxConcurrentUploadsDisplay));

        // Switching to an account whose session is still good unlocks the row, and away from one
        // locks it again — the whole reason the gate is per-account.
        OnPropertyChanged(nameof(SelectedAccountSessionExpired));
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(UseBlockedTooltip));

        if (!CanUse)
        {
            // A locked row must not stay ticked from before: Use is what the upload reads.
            Use = false;
        }
    }
}
