// <copyright file="BRuploadAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for BRupload. Cached inside
/// <see cref="BRuploadPipeline"/> keyed by <see cref="Dal.FileHosterLoginDto.Id"/> so files
/// for the same account skip the CSRF-fetch, login-form POST, and upload_form scrape.
/// </summary>
/// <param name="XfssCookie">Value of the <c>xfss</c> session cookie returned by the login
/// POST. Sent back as <c>Cookie: xfss=...</c> on every subsequent request.</param>
/// <param name="SessionId">The <c>sess_id</c> hidden field harvested from the
/// <c>?op=upload_form</c> HTML. On the mock this equals <see cref="XfssCookie"/>; on the
/// real backend it can differ, so we always use the form-provided value verbatim.</param>
/// <param name="UploadActionUrl">The <c>action</c> attribute of the upload form — a
/// per-user upload subdomain on the real backend (e.g. <c>https://server54.brupload.net/cgi-bin/upload.cgi?upload_type=file&amp;utype=reg</c>).
/// The main <c>www.brupload.net</c> host doesn't accept large multipart bodies, so this
/// must be scraped at login time and used verbatim for every upload in the session.</param>
internal sealed record BRuploadAuthState(string XfssCookie, string SessionId, string UploadActionUrl);
