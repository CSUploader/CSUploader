// <copyright file="HttpResponseSnapshot.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Minimal response payload used by hoster pipelines that need more than just a body —
/// e.g. cookie-based auth flows that read <c>Set-Cookie</c> from a login redirect, or
/// multipart uploads whose response body is the upload result itself. The shape is
/// intentionally small; richer header access stays inside <see cref="HttpHandler"/>'s
/// transaction logging.
/// </summary>
/// <param name="StatusCode">HTTP status code as returned by the server. Not normalized
/// — callers can decide whether 3xx counts as success (BRupload login returns 302).</param>
/// <param name="Body">Response body as UTF-8 text.</param>
/// <param name="SetCookies">All <c>Set-Cookie</c> header values, one per cookie, in
/// server order. Empty when none were sent. Values are the raw cookie strings
/// (<c>name=value; Path=/; HttpOnly</c>) — pipelines parse what they need.</param>
/// <param name="LocationHeader">The <c>Location</c> response header, when present.
/// Surfaces the redirect target on 3xx responses so callers can follow them manually
/// (the global handler is configured with <c>AllowAutoRedirect=false</c> because some
/// hosters branch on the 302 itself — BRupload's login). Null when the server didn't
/// send a Location header.</param>
public sealed record HttpResponseSnapshot(int StatusCode, string Body, IReadOnlyList<string> SetCookies, string? LocationHeader = null);
