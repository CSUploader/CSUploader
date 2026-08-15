// <copyright file="XFileSharingApiPipeline.WebForm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// The signed-in WEB-FORM upload path and its xfss-cookie session management, as a partial: one
/// class across five files, one concern each (see the main file's class doc).
/// </summary>
public abstract partial class XFileSharingApiPipeline
{
    /// <summary>
    /// Returns the stored session cookie, signing in through the WebView only when there isn't a
    /// usable one. Sign-in is serialised per account behind <see cref="_bootstrapGates"/> — the same
    /// gate the API-key bootstrap uses, and for the same reason: without it, N parallel uploads that
    /// all start without a cookie each open their own sign-in window. The re-check after taking the
    /// gate is what actually collapses them — whoever gets in first signs in and writes the cookie to
    /// the shared credentials, and everyone queued behind then finds it already there.
    /// </summary>
    private async Task<string?> GetOrAcquireXfssCookieAsync(AttemptContext ctx, CancellationToken ct)
    {
        if (HasValidStoredSessionCookie(ctx))
        {
            return ctx.Credentials.SessionCookie;
        }

        // No browser needed for a hoster whose login we can post; without that, no auth service means
        // no way to sign in at all.
        if (_authService is null && !SupportsDirectLogin)
        {
            return null;
        }

        SemaphoreSlim gate = _bootstrapGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A sibling attempt may have signed in while this one waited.
            if (HasValidStoredSessionCookie(ctx))
            {
                return ctx.Credentials.SessionCookie;
            }

            return SupportsDirectLogin
                ? await DirectLoginForUploadAsync(ctx, ct).ConfigureAwait(false)
                : await AcquireXfssCookieAsync(ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The sign-in itself, WITHOUT taking <see cref="_bootstrapGates"/> — callers must already hold
    /// the gate for this account. <see cref="SemaphoreSlim"/> is not reentrant, so the API-key
    /// bootstrap (which signs in from inside the gate it took in <see cref="EnsureApiKeyAsync"/>)
    /// must come here rather than through <see cref="GetOrAcquireXfssCookieAsync"/>, or it deadlocks
    /// against itself.
    /// </summary>
    /// <summary>
    /// Signs in for an upload without a browser, then persists the session exactly as the interactive
    /// path does — so a batch signs in once rather than once per file, and the stored cookie survives
    /// a restart.
    /// </summary>
    private async Task<string?> DirectLoginForUploadAsync(AttemptContext ctx, CancellationToken ct)
    {
        (string? xfss, string? _) = await DirectLoginAsync(
            ctx.Handler, ctx.Credentials.Username, ctx.Credentials.Password, ct).ConfigureAwait(false);
        if (xfss is null)
        {
            return null;
        }

        ctx.Credentials.SessionCookie = xfss;
        ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + SignInSessionLifetime;
        ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

        if (_loginRepository is not null)
        {
            await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
        }

        return xfss;
    }

    private async Task<string?> AcquireXfssCookieAsync(AttemptContext ctx, CancellationToken ct)
    {
        if (_authService is null)
        {
            return null;
        }

        // UsernameCookieName: null — XFileSharing-family hosters don't put the identity
        // in the cookie jar; their /api/account/info endpoint returns the email instead.
        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(
                BuildSignInSpec(),
                ctx.Credentials.Username ?? string.Empty,
                ctx.Proxy,
                ct);
        }
        catch
        {
            return null;
        }

        if (captured is not InteractiveAuthResult result)
        {
            return null;
        }

        string stored = ComposeStoredSession(result);
        ctx.Credentials.SessionCookie = stored;
        ctx.Credentials.SessionCookieExpiresUtc = DateTime.UtcNow + SignInSessionLifetime;
        ctx.Credentials.PinnedProxyId = ctx.Proxy.Id;

        if (_loginRepository is not null)
        {
            await _loginRepository.UpdateAsync(ctx.Credentials, ct).ConfigureAwait(false);
        }

        return stored;
    }

    // ======== Web-form (no-API) path ========

    /// <summary>
    /// Web-form (no-API) logged-in upload. Mirrors <see cref="RunAsync"/>'s upload/progress/parse
    /// machinery but resolves the upload server from the logged-in <c>?op=upload_form</c> page
    /// (scraping the form <c>action</c> + hidden <c>sess_id</c>) instead of <c>/api/upload/server</c>,
    /// and authenticates with the <c>xfss</c> session cookie (no API key). Auth-expiry — the upload
    /// form bounced us to the login page, or the upload itself returned Unauthorized — clears the
    /// stored cookie so the next attempt re-signs-in.
    /// </summary>
    private async IAsyncEnumerable<UploadEvent> RunWebFormAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Ensure we have a session cookie (sign in via WebView only if we don't) ===
        bool needSignIn = !HasValidStoredSessionCookie(ctx);
        if (needSignIn)
        {
            yield return new AuthStarted();
        }

        string? xfss = await GetOrAcquireXfssCookieAsync(ctx, ct);
        if (xfss is null)
        {
            if (needSignIn)
            {
                yield return new AuthFailed("sign-in cancelled or no usable proxy available");
            }
            yield return new AttemptFailed("not signed in — open Settings → Accounts and sign in", null);
            yield break;
        }

        if (needSignIn)
        {
            yield return new AuthSucceeded();
        }

        // === Resolve the upload server from the logged-in upload form ===
        (string? uploadUrl, string? sessId, string? serverError, bool serverAuthExpired) =
            await GetWebFormUploadServerAsync(ctx, xfss, ct);

        if (serverAuthExpired)
        {
            await ClearSessionCookieAsync(ctx.Credentials, ct).ConfigureAwait(false);
            yield return new AuthFailed("session expired — sign in again from Settings → Accounts");
            yield return new AttemptFailed("session expired — retry will re-authenticate", null);
            yield break;
        }

        if (uploadUrl is null || sessId is null)
        {
            yield return new AttemptFailed(serverError ?? "could not resolve the upload server", null);
            yield break;
        }

        // === Upload (identical machinery to the API path) ===
        // Looped so a node that breaks AFTER taking the bytes can be retried once against a freshly
        // resolved server — see IsTransientNodeFailure. One TransferStarted covers the whole thing:
        // the retry is our business, not something the user asked for or should see twice.
        string currentUploadUrl = uploadUrl;
        string currentSessId = sessId;
        bool retriedNodeFailure = false;

        yield return new TransferStarted(ctx.FileSize);

        while (true)
        {
            bool authExpiredDuringUpload = false;
            string? attemptFailure = null;
            bool attemptCancelled = false;
            Exception? attemptException = null;
            string? finalUrl = null;

            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, currentUploadUrl, currentSessId);

            _ = uploadTask.ContinueWith(
                _ => progressChannel.Writer.Complete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return progressEv;
            }

            ctx.Handler.UploadProgress -= onProgress;

            HttpResponseSnapshot? uploadResponse = null;
            try
            {
                uploadResponse = await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                attemptCancelled = true;
            }
            catch (Exception ex)
            {
                attemptException = ex;
            }

            if (uploadResponse is not null)
            {
                (string? Url, string? Error, bool AuthExpired) = ParseUploadResponse(NormalizeUploadResponse(uploadResponse));
                if (AuthExpired)
                {
                    await ClearSessionCookieAsync(ctx.Credentials, ct).ConfigureAwait(false);
                    authExpiredDuringUpload = true;
                }
                else if (Error is not null)
                {
                    attemptFailure = Error;
                }
                else
                {
                    finalUrl = Url;
                }
            }

            if (authExpiredDuringUpload)
            {
                yield return new AuthFailed("session expired mid-upload");
                yield return new AttemptFailed("session expired — retry will re-authenticate", null);
                yield break;
            }

            if (attemptCancelled)
            {
                yield return new AttemptCancelled();
                yield break;
            }

            if (attemptException is not null)
            {
                yield return new AttemptFailed(attemptException.Message, attemptException);
                yield break;
            }

            if (attemptFailure is not null)
            {
                if (retriedNodeFailure || !IsTransientNodeFailure(attemptFailure))
                {
                    yield return new AttemptFailed(attemptFailure, null);
                    yield break;
                }

                retriedNodeFailure = true;
                ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node reported a backend failure ({attemptFailure}); retrying once with a fresh server.");

                // Re-read the upload form: it hands out the node, so this is what makes the retry land
                // somewhere else rather than repeat itself against the server that just failed.
                (string? retryUrl, string? retrySessId, string? retryError, bool retryAuthExpired) =
                    await GetWebFormUploadServerAsync(ctx, xfss, ct);

                if (retryAuthExpired)
                {
                    await ClearSessionCookieAsync(ctx.Credentials, ct).ConfigureAwait(false);
                    yield return new AuthFailed("session expired — sign in again from Settings → Accounts");
                    yield return new AttemptFailed("session expired — retry will re-authenticate", null);
                    yield break;
                }

                if (retryUrl is null || retrySessId is null)
                {
                    // Report the node's own failure — the reason we were retrying at all — rather than
                    // the re-resolve's, which is a symptom of it.
                    yield return new AttemptFailed(attemptFailure, null);
                    yield break;
                }

                currentUploadUrl = retryUrl;
                currentSessId = retrySessId;
                continue;
            }

            if (finalUrl is not null)
            {
                yield return new TransferCompleted(finalUrl);
            }

            yield break;
        }
    }

    /// <summary>
    /// GETs the logged-in <c>?op=upload_form</c> page (with the <c>xfss</c> cookie) and scrapes the
    /// per-session upload server's <c>action</c> URL (<c>fsNN/cgi-bin/upload.cgi?…</c>) + the hidden
    /// <c>sess_id</c>. A page with no upload form means the cookie no longer authenticates us (the
    /// server served a logged-out / login page) → reported as auth-expired so the caller clears the
    /// cookie and re-signs-in. Falls back to the cookie value for <c>sess_id</c> when the form omits
    /// the hidden input (it equals the cookie in the capture).
    /// </summary>
    private async Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> GetWebFormUploadServerAsync(
        AttemptContext ctx, string xfss, CancellationToken ct)
    {
        string html;
        try
        {
            html = await GetAsync(ctx, UploadFormUrl, BuildCookieHeader(xfss), ct);
        }
        catch (Exception ex)
        {
            return (null, null, "upload_form fetch failed: " + ex.Message, false);
        }

        // An edge/origin error is NOT a logged-out page, and the difference matters: every resolver
        // below decides "no upload form → the session expired", which makes the caller throw the
        // stored cookie away and re-pop the WebView. A momentary Cloudflare 520 must not cost the user
        // their sign-in. Fail the attempt instead and leave the session alone.
        if (LooksLikeEdgeFailure(html))
        {
            return (null, null, $"the upload page came back as an infrastructure error, not a page ({Snippet(html)}) — this is usually momentary", false);
        }

        return await ResolveWebFormUploadServerAsync(ctx, html, xfss, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when a fetched body is the CDN or origin failing rather than the site answering.
    /// Deliberately narrow: it decides whether a stored session is thrown away, so it must not match
    /// a real page that happens to mention an error. Cloudflare's own shapes are a bare
    /// <c>error code: 5xx</c> body (what Data Vaults produced) or its "Error 52x" interstitial.
    /// Internal for testing.
    /// </summary>
    internal static bool LooksLikeEdgeFailure(string body)
    {
        string trimmed = body.TrimStart();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.StartsWith("error code:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The HTML interstitial: short, Cloudflare-branded, and naming a 52x. Length-bounded so a real
        // page carrying a support link to Cloudflare can't trip it.
        return trimmed.Length < 8192
               && trimmed.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
               && (trimmed.Contains("Error 520", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Contains("Error 521", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Contains("Error 522", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Contains("Error 523", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Contains("Error 524", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Turns the fetched upload page into the upload URL and <c>sess_id</c>. Split out from
    /// <see cref="GetWebFormUploadServerAsync"/> so a fork can replace the RESOLUTION without
    /// reimplementing the fetch, the cookie handling or the upload loop around it.
    /// <para>
    /// The default is the family's: the page's own form <c>action</c>, plus the hidden
    /// <c>sess_id</c> (falling back to the cookie, which it equals in every capture so far).
    /// Override on forks whose FILE form carries no <c>action</c> because a script fetches the node
    /// separately — filedot.to is one, and the only <c>action</c> on its page belongs to the
    /// URL-uploader (<c>…/upload.cgi?upload_type=url</c>), which would be silently the wrong target.
    /// </para>
    /// <para>
    /// Returning <c>AuthExpired</c> makes the caller drop the stored cookie and sign in again, so
    /// reserve it for "this page says we are logged out" rather than for any failure.
    /// </para>
    /// </summary>
    protected virtual Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> ResolveWebFormUploadServerAsync(
        AttemptContext ctx, string uploadFormHtml, string xfss, CancellationToken ct)
    {
        _ = ctx;
        _ = ct;

        Match action = _anonUploadActionRegex.Match(uploadFormHtml);
        if (!action.Success)
        {
            return Task.FromResult<(string?, string?, string?, bool)>((null, null, "upload form not found — the session may have expired", true));
        }

        return Task.FromResult<(string?, string?, string?, bool)>((action.Groups[1].Value, ScrapeSessId(uploadFormHtml, xfss), null, false));
    }

    /// <summary>
    /// The hidden <c>sess_id</c> from an upload page, falling back to the session cookie when the
    /// form omits it — they are the same value in every capture taken so far, filedot.to included.
    /// Worth getting right: XFileSharing authenticates an upload by <c>sess_id</c> ALONE, and a wrong
    /// one uploads anonymously rather than failing.
    /// </summary>
    protected static string ScrapeSessId(string html, string xfss)
    {
        Match sess = _sessIdInputRegex.Match(html);
        string sessId = sess.Success
            ? (sess.Groups[1].Success && sess.Groups[1].Length > 0 ? sess.Groups[1].Value : sess.Groups[2].Value)
            : string.Empty;

        return string.IsNullOrEmpty(sessId) ? xfss : sessId;
    }

    /// <summary>Mirror of the cookie-validity check inside <see cref="GetOrAcquireXfssCookieAsync"/>:
    /// true when a non-expired session cookie pinned to (or unpinned from) the current proxy is on the
    /// DTO — i.e. when no WebView pop is needed. Lets <see cref="RunWebFormAsync"/> emit the Auth*
    /// events only when a sign-in actually happens.</summary>
    private static bool HasValidStoredSessionCookie(AttemptContext ctx)
    {
        bool pinMatches = ctx.Credentials.PinnedProxyId is null || ctx.Credentials.PinnedProxyId == ctx.Proxy.Id;
        return pinMatches
            && !string.IsNullOrEmpty(ctx.Credentials.SessionCookie)
            && ctx.Credentials.SessionCookieExpiresUtc is DateTime expiresUtc
            && expiresUtc > DateTime.UtcNow;
    }

    private async Task ClearSessionCookieAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

}
