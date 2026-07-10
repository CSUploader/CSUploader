// <copyright file="FileHosterLoginDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Runtime.CompilerServices;
using CSUploader.Upload;

namespace CSUploader.Dal;

public class FileHosterLoginDto : INotifyPropertyChanged
{
    // The Accounts DataGrid only re-renders a row when the bound item raises
    // PropertyChanged (or the item instance is replaced). The display-bound, mutable
    // fields below notify so an in-place refresh/enable/disable updates the grid without
    // replacing the item or reloading the whole collection. Implemented by hand so the
    // Dal project keeps no UI/MVVM (CommunityToolkit) dependency.
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public int Id { get; set; }

    public string? FileHosterName { get; set; }

    private string? _username;

    // Notifies because RefreshSingleAccountAsync → ApplySessionCookieIfPresent can set this in
    // place from the verifier's DerivedUsername (API-key hosters like HitFile), and the grid's
    // {Binding Username} column must re-render without a reload.
    public string? Username { get => _username; set => SetField(ref _username, value); }

    public string? Password { get; set; }

    private bool _disabled;

    public bool Disabled { get => _disabled; set => SetField(ref _disabled, value); }

    private AccountType _accountType;

    public AccountType AccountType { get => _accountType; set => SetField(ref _accountType, value); }

    /// <summary>
    /// Marks a synthetic, non-persisted "Anonymous" selection — the built-in no-login option
    /// the upload wizard offers for hosters whose pipeline sets
    /// <see cref="Upload.Pipeline.IFileHosterPipeline.SupportsAnonymousUpload"/> (GigaPeta,
    /// Hexload). Never written to the DB; the pipeline branches to its anonymous upload path
    /// when this is true instead of using <see cref="Username"/>/<see cref="ApiKey"/>.
    /// </summary>
    public bool IsAnonymous { get; set; }

    /// <summary>
    /// Cached session cookie value for captcha-gated hosters (currently ex-load.com).
    /// Null for hosters whose login is a plain credential POST. Persisted across app
    /// restarts so the user only re-runs the WebView captcha flow once per cookie lifetime.
    /// </summary>
    public string? SessionCookie { get; set; }

    /// <summary>
    /// UTC timestamp the cached <see cref="SessionCookie"/> should be considered expired.
    /// </summary>
    public DateTime? SessionCookieExpiresUtc { get; set; }

    /// <summary>
    /// Proxy pinned to this account for the lifetime of <see cref="SessionCookie"/>. See
    /// <see cref="FileHosterLoginDbm.PinnedProxyId"/> for semantics.
    /// </summary>
    public int? PinnedProxyId { get; set; }

    /// <summary>
    /// API key for key-based REST APIs (currently Ex-Load). See
    /// <see cref="FileHosterLoginDbm.ApiKey"/> for semantics.
    /// </summary>
    public string? ApiKey { get; set; }

    private long? _storageUsedBytes;

    /// <summary>Bytes the account is currently consuming on the hoster (FileBoom's
    /// <c>storageSpace.used</c>). Null when not known.</summary>
    public long? StorageUsedBytes
    {
        get => _storageUsedBytes;
        // StorageAvailableBytes is computed from this, so cascade its notification too.
        set
        {
            if (SetField(ref _storageUsedBytes, value))
            {
                OnPropertyChanged(nameof(StorageAvailableBytes));
            }
        }
    }

    private long? _storageQuotaBytes;

    /// <summary>Total storage quota the account is allowed (FileBoom's
    /// <c>storageSpace.total</c>). Null when not known.</summary>
    public long? StorageQuotaBytes
    {
        get => _storageQuotaBytes;
        // StorageAvailableBytes is computed from this, so cascade its notification too.
        set
        {
            if (SetField(ref _storageQuotaBytes, value))
            {
                OnPropertyChanged(nameof(StorageAvailableBytes));
            }
        }
    }

    /// <summary>Computed remaining storage = <see cref="StorageQuotaBytes"/> − <see cref="StorageUsedBytes"/>,
    /// floored at 0 (handles the over-quota case some hosters allow transiently). Null
    /// when either operand is null — bound by the Accounts grid's "Available" column,
    /// which renders blank for hosters that don't expose a quota.</summary>
    public long? StorageAvailableBytes => StorageQuotaBytes is { } total && StorageUsedBytes is { } used
        ? Math.Max(0L, total - used)
        : null;

    /// <summary>
    /// Local-time stamp of the last verifier round-trip for this account, regardless of
    /// whether it succeeded. Drives the Account Manager grid's "Refreshed at" column.
    /// Null when the account has never been refreshed. Always set via
    /// <see cref="MarkRefreshed"/> from real verification moments — NOT via
    /// <see cref="SetCheckStatus"/>, which is reserved for in-flight markers ("Checking…")
    /// and snapshot restores that must NOT touch the timestamp.
    /// </summary>
    private DateTime? _lastRefreshedDateTime;

    public DateTime? LastRefreshedDateTime { get => _lastRefreshedDateTime; set => SetField(ref _lastRefreshedDateTime, value); }

    /// <summary>
    /// Local-time stamp of when this account was added (set once at insert, never changed).
    /// Drives the Account Manager grid's "Added at" column. Null on accounts that predate the
    /// column.
    /// </summary>
    public DateTime? CreatedDateTime { get; set; }

    /// <summary>
    /// Outcome category for the last verification, used by the Account Manager grid to
    /// pick the cell colour. Pairs with <see cref="StatusMessage"/>; always set both
    /// together via <see cref="SetCheckStatus"/> so they can't drift.
    /// </summary>
    private AccountCheckStatus _checkStatus = AccountCheckStatus.NotChecked;

    public AccountCheckStatus CheckStatus { get => _checkStatus; set => SetField(ref _checkStatus, value); }

    /// <summary>
    /// Non-persisted display field showing the last check result (e.g. "Premium until
    /// 2099", "Wrong password", "The SSL connection could not be established..."). The
    /// row's cell colour comes from <see cref="CheckStatus"/>, not from sniffing this
    /// text — so the message can be anything the verifier returned without breaking
    /// the colour scheme. Empty by default; the colour-coded cell carries the
    /// NotChecked signal on its own.
    /// </summary>
    private string _statusMessage = string.Empty;

    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    /// <summary>
    /// Sets <see cref="CheckStatus"/> and <see cref="StatusMessage"/> together — the only
    /// supported way to update either, so the two fields never drift out of sync (e.g.
    /// red cell with a "Premium until X" message). Reserved for in-flight markers
    /// ("Checking…") and snapshot restores that must NOT touch
    /// <see cref="LastRefreshedDateTime"/>; for completion of a real verifier round-trip,
    /// call <see cref="MarkRefreshed"/> instead.
    /// </summary>
    public void SetCheckStatus(AccountCheckStatus status, string message)
    {
        CheckStatus = status;
        StatusMessage = message;
    }

    /// <summary>
    /// Records the outcome of a verifier round-trip: stamps <see cref="CheckStatus"/>,
    /// <see cref="StatusMessage"/> AND <see cref="LastRefreshedDateTime"/> in one shot.
    /// Call this — NOT <see cref="SetCheckStatus"/> — from every place that finishes a
    /// real <c>IAccountVerifier.CheckAsync</c> attempt (success OR failure), so the
    /// Accounts grid's "Refreshed at" column updates. See <see cref="SetCheckStatus"/>
    /// for the in-flight / snapshot-restore path.
    /// </summary>
    public void MarkRefreshed(AccountCheckStatus status, string message, DateTime refreshedAt)
    {
        CheckStatus = status;
        StatusMessage = message;
        LastRefreshedDateTime = refreshedAt;
    }
}
