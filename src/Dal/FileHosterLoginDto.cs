// <copyright file="FileHosterLoginDto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Dal;

public class FileHosterLoginDto
{
    public int Id { get; set; }

    public string? FileHosterName { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool Disabled { get; set; }

    public AccountType AccountType { get; set; }

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

    /// <summary>
    /// Outcome category for the last verification, used by the Account Manager grid to
    /// pick the cell colour. Pairs with <see cref="StatusMessage"/>; always set both
    /// together via <see cref="SetCheckStatus"/> so they can't drift.
    /// </summary>
    public AccountCheckStatus CheckStatus { get; set; } = AccountCheckStatus.NotChecked;

    /// <summary>
    /// Non-persisted display field showing the last check result (e.g. "Premium until
    /// 2099", "Wrong password", "The SSL connection could not be established..."). The
    /// row's cell colour comes from <see cref="CheckStatus"/>, not from sniffing this
    /// text — so the message can be anything the verifier returned without breaking
    /// the colour scheme.
    /// </summary>
    public string StatusMessage { get; set; } = "Not checked";

    /// <summary>
    /// Sets <see cref="CheckStatus"/> and <see cref="StatusMessage"/> together — the only
    /// supported way to update either, so the two fields never drift out of sync (e.g.
    /// red cell with a "Premium until X" message).
    /// </summary>
    public void SetCheckStatus(AccountCheckStatus status, string message)
    {
        CheckStatus = status;
        StatusMessage = message;
    }
}
