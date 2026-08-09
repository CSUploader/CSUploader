// <copyright file="AccountCheckOutcome.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

namespace CSUploader.Upload;

/// <summary>
/// Writes a verifier's result onto the account it was run for.
/// <para>
/// This lives outside the view-models because <b>two</b> places add accounts — Settings and the
/// upload wizard's "Add account…" — and for several hosters the check is not a formality but the
/// step that <i>produces</i> the upload credential: FileMirage's <c>api_token</c>, DropMB's
/// <c>access_token</c>, FileCat's <c>SESS</c>, Pixeldrain's <c>auth_key</c> all arrive on the
/// <see cref="AccountCheckResult"/> and nowhere else. An add path that skips this saves an account
/// that looks complete and cannot upload.
/// </para>
/// </summary>
public static class AccountCheckOutcome
{
    /// <summary>
    /// Copies everything a successful check produced onto <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Each field is applied only when the verifier actually returned one, because a hoster that
    /// can't re-read a value must not blank the one already stored — HitFile's refresh, for example,
    /// authenticates with an upload token that can't reach the storage API, so it reports null
    /// storage on an otherwise good account.
    /// </remarks>
    public static void Apply(FileHosterLoginDto target, AccountCheckResult result)
    {
        if (result.SessionCookie is not null)
        {
            target.SessionCookie = result.SessionCookie;
            target.SessionCookieExpiresUtc = result.SessionCookieExpiresUtc;
            target.PinnedProxyId = result.PinnedProxyId;
        }

        // Applied separately from the cookie: Ex-Load's verify returns the API key without a cookie
        // or pin (it clears them once the key is in hand), and the username/password → API-key
        // upgrade has to land on the DTO the moment the verifier hands it over.
        if (result.ApiKey is not null)
        {
            target.ApiKey = result.ApiKey;
        }

        // The verifier is the canonical source of identity for hosters where the user never typed
        // one (an API-key or captured-cookie account). A hoster whose Username IS the login
        // identifier returns null here on purpose — see YetiSharePipeline, where returning the
        // page's screen name replaced a working login with a name that can't authenticate.
        if (!string.IsNullOrEmpty(result.DerivedUsername))
        {
            target.Username = result.DerivedUsername;
        }

        if (result.StorageQuotaBytes is { } quota)
        {
            target.StorageQuotaBytes = quota;
        }

        if (result.StorageUsedBytes is { } used)
        {
            target.StorageUsedBytes = used;
        }
    }

    /// <summary>
    /// A failed check disables the account, which is what keeps it out of the upload wizard's
    /// pickers — those list only enabled accounts, so this is the mechanism that stops a set of
    /// credentials that doesn't work from being chosen for an upload.
    /// </summary>
    public static void AutoDisableIfFailed(FileHosterLoginDto account, AccountCheckStatus status)
    {
        if (status == AccountCheckStatus.Failed)
        {
            account.Disabled = true;
        }
    }
}
