// <copyright file="ExLoadAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for Ex-Load. Same shape as
/// <see cref="BRuploadAuthState"/> — Ex-Load is the same XFileSharing family, just with
/// hCaptcha-gated login that we satisfy via the WebView2 flow rather than a credential
/// POST.
/// </summary>
/// <param name="XfssCookie">Value of the <c>xfss</c> session cookie captured from the
/// WebView. Sent back as <c>Cookie: xfss=...</c> on every subsequent request to the
/// <c>ex-load.com</c> origin.</param>
/// <param name="SessionId">The <c>sess_id</c> hidden field harvested from the
/// <c>?op=upload_form</c> HTML. Always use the form-provided value verbatim — on the
/// real backend it can differ from <see cref="XfssCookie"/>.</param>
/// <param name="UploadActionUrl">The <c>action</c> attribute of the upload form — a
/// per-user upload subdomain on the real backend. The main host doesn't accept large
/// multipart bodies, so this must be scraped per session and used verbatim for every
/// upload.</param>
/// <param name="ExpiresUtc">Wall-clock UTC time after which the cookie should be
/// treated as expired and a fresh WebView sign-in triggered. Set at capture from a
/// conservative default (e.g. 7 days from now) since XFileSharing rarely supplies a
/// real Max-Age.</param>
internal sealed record ExLoadAuthState(string XfssCookie, string SessionId, string UploadActionUrl, DateTime ExpiresUtc);
