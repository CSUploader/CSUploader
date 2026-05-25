// <copyright file="FileHosterLoginDbm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CSUploader.Upload;

namespace CSUploader.Dal;

[Table("FileHosterLogin")]
public class FileHosterLoginDbm
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string? FileHosterName { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public bool Disabled { get; set; }

    public AccountType AccountType { get; set; }

    /// <summary>
    /// Cached session cookie value for captcha-gated hosters (currently ex-load.com).
    /// Null for hosters whose login is a plain credential POST. Populated by the
    /// pipeline after a successful WebView sign-in via <see cref="Services.IInteractiveAuthService"/>.
    /// </summary>
    public string? SessionCookie { get; set; }

    /// <summary>
    /// UTC timestamp the cached <see cref="SessionCookie"/> should be considered expired.
    /// XFileSharing-family hosters typically return session cookies without an explicit
    /// Max-Age, so the pipeline sets a conservative default (e.g. 7 days) on capture and
    /// the user is re-prompted via WebView once the timestamp is reached or the server
    /// returns an Unauthorized response (whichever comes first).
    /// </summary>
    public DateTime? SessionCookieExpiresUtc { get; set; }

    /// <summary>
    /// Proxy pinned to this account for the lifetime of <see cref="SessionCookie"/>.
    /// Captcha-gated hosters (Ex-Load) bind their session cookies to the IP that issued
    /// them; rotating proxies per-attempt would invalidate the cookie on the first request
    /// from a different IP. We pin one proxy at sign-in time and route every subsequent
    /// upload (and the WebView2 sign-in itself) through it.
    /// </summary>
    /// <remarks>
    /// Semantics: <c>null</c> = no pin, use the rotation. <c>0</c> = pinned to a direct
    /// connection (typically because Use Proxies was off at sign-in time). Any positive
    /// value = <see cref="ProxySettingDbm.Id"/> of the pinned proxy row.
    /// </remarks>
    public int? PinnedProxyId { get; set; }

    /// <summary>
    /// API key for hosters that expose a key-based REST API (currently Ex-Load).
    /// When non-empty the pipeline uses it directly for account verification and uploads,
    /// skipping any cookie / WebView dance. For Ex-Load specifically the key can either
    /// be supplied directly by the user OR derived from a username/password sign-in plus
    /// a one-time scrape of the my_account page — once we have it, the
    /// <see cref="Username"/>/<see cref="Password"/> remain on the row but are no longer
    /// used.
    /// </summary>
    public string? ApiKey { get; set; }
}
