// <copyright file="DownloadCaptchaRequirement.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Whether a FREE/ANONYMOUS downloader — the person a shared link is for, not the account the
/// upload ran under — must solve a captcha before this hoster hands over the file. What the
/// wizard's "Download captcha?" column reports.
/// <para>
/// <b>Only what was verified.</b> <see cref="Unknown"/> — the default every pipeline gets — means
/// "this host's free download flow has not been verified", NOT "no captcha". A captcha here means a
/// challenge the downloader must actively solve (image/checkbox reCAPTCHA, hCaptcha, an interactive
/// Turnstile widget, a custom puzzle) gating the download itself; countdown timers, plain download
/// buttons, and automatic CDN browser checks are NOT captchas. Verdicts come from the host's own
/// copy (e.g. "no captcha" sold as a premium perk implies the free flow has one) or an inspected
/// live download flow — each pipeline's override cites its source in remarks, and the full
/// per-hoster research matrix lives in <c>docs/hoster-download-captcha.md</c>.
/// </para>
/// </summary>
public enum DownloadCaptchaRequirement
{
    /// <summary>This host's free download flow could not be verified (the default) — covering both
    /// hosts nobody has researched and researched-but-inconclusive ones (see the matrix).</summary>
    Unknown = 0,

    /// <summary>The ORDINARY free download flow was verified to involve no captcha — e.g. the share
    /// link IS the raw file, or the walked flow reached the bytes (or the final direct file URL)
    /// with no challenge. Abuse-triggered exceptions (rate-limit or virus-flag challenges, e.g.
    /// Pixeldrain's) don't count against this.</summary>
    NotRequired,

    /// <summary>The ordinary free download flow gates the download behind a captcha the downloader
    /// must solve. (Whether a paid tier bypasses it is the host's business and not claimed here.)</summary>
    Required,
}
