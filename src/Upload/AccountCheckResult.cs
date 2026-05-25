// <copyright file="AccountCheckResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Outcome of an <see cref="IAccountVerifier"/> round-trip.
/// </summary>
/// <param name="IsValid">True when the credentials were accepted by the hoster.</param>
/// <param name="AccountType">Premium/Free, when the hoster exposes it; defaults to Free
/// for hosters that don't.</param>
/// <param name="Message">Human-readable status surfaced in the Settings UI.</param>
/// <param name="PremiumExpiry">When known, the UTC expiry of premium status.</param>
/// <param name="SessionCookie">For captcha-gated hosters (currently Ex-Load), the session
/// cookie value captured during the interactive sign-in. Null for hosters whose login is a
/// plain credential POST — those pipelines don't surface a reusable cookie. When non-null,
/// callers should persist it onto the credentials DTO together with
/// <see cref="SessionCookieExpiresUtc"/> so the upload pipeline can skip the WebView until
/// expiry.</param>
/// <param name="SessionCookieExpiresUtc">Wall-clock expiry of <see cref="SessionCookie"/>.
/// Null when no cookie was captured.</param>
/// <param name="PinnedProxyId">For captcha-gated hosters, the DB id of the proxy the
/// sign-in was routed through. Non-null when <see cref="SessionCookie"/> is set; callers
/// should write it to the credentials DTO so subsequent uploads reuse the same proxy
/// (XFileSharing binds session cookies to the issuing IP, and rotating per-attempt would
/// invalidate the cookie). Use <c>0</c> for "pinned to direct" when Use Proxies was off
/// at sign-in time.</param>
public record AccountCheckResult(
    bool IsValid,
    AccountType AccountType,
    string? Message = null,
    DateTime? PremiumExpiry = null,
    string? SessionCookie = null,
    DateTime? SessionCookieExpiresUtc = null,
    int? PinnedProxyId = null);
