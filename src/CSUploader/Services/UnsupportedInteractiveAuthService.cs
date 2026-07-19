// <copyright file="UnsupportedInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#if !WINDOWS
using CSUploader.Lib.Net;

namespace CSUploader.Services;

/// <summary>
/// Non-Windows stand-in for <see cref="IInteractiveAuthService"/>. The real sign-in is a WebView2-hosted
/// captcha login, and WebView2 is Windows-only, so the portable (Linux/macOS) build has no interactive
/// sign-in. Returning <see langword="null"/> matches the interface's "cancelled / refused" contract, which
/// every caller already handles gracefully: anonymous and simple-credential hosters keep working; the
/// captcha-gated account sign-ins are simply unavailable on this platform.
/// </summary>
/// <remarks>
/// TODO(linux): surface a user-facing "interactive sign-in requires Windows" message instead of a silent
/// null (e.g. an <c>IDialogService.ShowErrorAsync</c> from the EditAccount "Sign in" handler), and/or
/// integrate a cross-platform embedded browser to restore the captcha flow.
/// </remarks>
internal sealed class UnsupportedInteractiveAuthService : IInteractiveAuthService
{
    /// <inheritdoc />
    public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
        InteractiveAuthSpec spec,
        string username,
        ProxyChoice? proxy,
        CancellationToken cancellationToken)
        => Task.FromResult<InteractiveAuthResult?>(null);
}
#endif
