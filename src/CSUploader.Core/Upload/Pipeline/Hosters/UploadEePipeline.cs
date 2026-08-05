// <copyright file="UploadEePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// upload.ee — anonymous, and the first host in the tree running <b>Uber-Uploader</b>, a Perl CGI
/// uploader that is neither XFileSharing nor a drop host. Three steps, from a browser capture
/// 2026-08-05 and verified end to end with real bytes:
/// <list type="number">
///   <item><b>Get an id.</b> <c>GET /ubr_link_upload.php?rnd_id=&lt;epoch-ms&gt;</c> answers a line of
///   JavaScript — <c>if(typeof startUpload==='function'){startUpload("&lt;32 hex&gt;",0);}</c> — and the
///   id inside it is <b>minted by the server</b>.</item>
///   <item><b>Upload.</b> Multipart POST to
///   <c>/cgi-bin/ubr_upload.pl?X-Progress-ID=&lt;id&gt;&amp;upload_id=&lt;id&gt;</c> carrying
///   <b>only</b> <c>upfile_0</c> — no other fields at all.</item>
///   <item><b>Collect the link.</b> The reply points at
///   <c>/?page=finished&amp;upload_id=&lt;id&gt;</c>, which renders "View file" (the share link) and a
///   <c>?killcode=</c> delete link.</item>
/// </list>
/// <para>
/// <b>Step 1 is not optional and cannot be faked.</b> Posting a self-invented id — the obvious first
/// guess — reaches the script and dies inside it with
/// <c>Ei suutnud avada link faili …/&lt;id&gt;.link</c> ("could not open link file") at
/// <c>ubr_upload.pl</c> line 124: the server writes that file when it hands the id out, so an id it
/// never issued has nothing behind it. That failure is what the capture resolved.
/// </para>
/// <para>
/// <b>⚠ The finish step answers differently for us than for a browser.</b> The capture shows a
/// <c>302</c> with a <c>Location</c>; this client gets <c>200</c> whose body is
/// <c>parent.location.href='…?page=finished&amp;upload_id=…'</c> — the iframe-era redirect. Both are
/// handled, because relying on either alone would work in testing and fail in the field.
/// </para>
/// <para>
/// Anonymous uploads are capped at <b>100 MB</b> and kept until <b>50 days after the last download</b>
/// (an account raises those to 200 MB and 120 days; not implemented — the anonymous path needs no
/// credential and this app has no account to offer it). No captcha, no cookie: the flow issues no
/// session, and the only <c>Set-Cookie</c> anywhere in the capture is a language preference.
/// </para>
/// <para>
/// <b>⚠ It inspects inside archives</b>, which no other host here does: per its FAQ a single unpacked
/// file may not exceed 200 MB, the total extracted may not exceed 400 MB, and an archive over 50 MB
/// whose contents expand more than fivefold is refused. Ordinary release parts pass; this cannot be
/// checked locally, so it surfaces as the host's own refusal.
/// </para>
/// </summary>
public sealed class UploadEePipeline : IFileHosterPipeline
{
    private const string Host = "https://www.upload.ee";
    private const string LinkUploadUrl = Host + "/ubr_link_upload.php";
    private const string UploadScriptUrl = Host + "/cgi-bin/ubr_upload.pl";

    /// <summary>Anonymous cap, from its FAQ ("Unregistered users can upload up to 100MB files").
    /// Binary, matching how the other 100 MB host in the tree (tmpfiles.org) reads its own figure.</summary>
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    // if(typeof startUpload==='function'){startUpload("c93cc90ca1aeac83b3586aad022b9b62",0);}
    private static readonly Regex _uploadIdRegex = new(
        """startUpload\(\s*["']([0-9a-fA-F]{8,})["']""",
        RegexOptions.Compiled);

    // The iframe-era redirect this client receives in place of the browser's 302.
    private static readonly Regex _jsRedirectRegex = new(
        """(?:parent\.)?location\.href\s*=\s*["']([^"']*page=finished[^"']*)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <h1>File successfully uploaded!!</h1> … View file:<br /><a href="…/files/<id>/<name>.html">
    private static readonly Regex _viewLinkRegex = new(
        """View\s+file:.{0,120}?href=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex _killcodeRegex = new(
        """href=["']([^"']+killcode=[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public UploadEePipeline()
    {
    }

    /// <summary>Test ctor — stubs the two GETs and the multipart POST so the three-step orchestration
    /// runs without the network.</summary>
    internal UploadEePipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getOverride = getOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "Upload.ee";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous is what ships — see the class remarks on the account tier.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds upload.ee's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} anonymous per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: the server mints the upload id ===
        string rndId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        HttpResponseSnapshot? idResponse = null;
        string? idRequestError = null;
        try
        {
            idResponse = await GetAsync(ctx, $"{LinkUploadUrl}?rnd_id={rndId}");
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // C# forbids yielding from a catch, so the failure is carried out of it.
            idRequestError = "upload.ee upload-id request failed: " + ex.Message;
        }

        if (idResponse is null)
        {
            yield return new AttemptFailed(idRequestError ?? "upload.ee upload-id request failed", null);
            yield break;
        }

        (string? uploadId, string? idError) = ParseUploadId(idResponse);
        if (uploadId is null)
        {
            yield return new AttemptFailed(idError!, null);
            yield break;
        }

        // === Step 2: the bytes ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadId);
        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        // A transport fault propagates to the shared retry layer as a safe-to-retry
        // UploadBodyTransferException; re-running mints a fresh id, so nothing is double-created.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        // === Step 3: the result page carries the link ===
        // How that page arrives depends on the CLIENT'S REDIRECT POLICY, which is why all three shapes
        // are handled rather than the one a given run happens to show:
        //   • our HttpHandler follows the 302, so the upload's own body IS the finished page;
        //   • a client that doesn't follow gets 302 + Location (the browser capture);
        //   • or 200 whose body is parent.location.href='…' (the iframe-era redirect).
        // Reading the body we already hold first also saves a request in the common case.
        (string? url, string? deleteLink, string? _) = ParseFinishedPage(uploadResponse);

        if (url is null)
        {
            (string? finishedUrl, string? uploadError) = ParseFinishedUrl(uploadResponse, uploadId);
            if (finishedUrl is null)
            {
                yield return new AttemptFailed(uploadError!, null);
                yield break;
            }

            HttpResponseSnapshot? finished = null;
            string? finishRequestError = null;
            try
            {
                finished = await GetAsync(ctx, finishedUrl);
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                finishRequestError = "upload.ee result page fetch failed: " + ex.Message;
            }

            if (finished is null)
            {
                // The bytes are already on their server by now; only the link is missing. Say that,
                // rather than implying the upload itself failed.
                yield return new AttemptFailed(
                    (finishRequestError ?? "upload.ee result page fetch failed")
                        + $" — the file may well have uploaded; its page is {finishedUrl}",
                    null);
                yield break;
            }

            string? finishError;
            (url, deleteLink, finishError) = ParseFinishedPage(finished);
            if (url is null)
            {
                yield return new AttemptFailed(finishError!, null);
                yield break;
            }
        }

        // The killcode appears once, on this page, and an anonymous upload has no account to manage the
        // file from — so log it rather than drop it, as Sendspace and FILEAXA do.
        if (deleteLink is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: delete link for {ctx.FileName} — {deleteLink}");
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} is kept until 50 days after its last download.");
        yield return new TransferCompleted(url);
    }

    /// <summary>Anonymous only here — an account would raise the caps but needs a login this pipeline
    /// doesn't implement.</summary>
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = username;
        _ = password;
        _ = apiKey;
        _ = handler;
        _ = proxy;
        _ = ct;
        return Task.FromResult(new AccountCheckResult(
            false,
            AccountType.Free,
            "upload.ee accounts aren't supported yet — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>Reads the id out of the JavaScript step 1 answers with. Internal for testing.</summary>
    internal static (string? UploadId, string? Error) ParseUploadId(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"upload.ee wouldn't issue an upload id (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        Match m = _uploadIdRegex.Match(response.Body);
        return m.Success
            ? (m.Groups[1].Value, null)
            : (null, $"upload.ee returned no upload id: {Snippet(response.Body)}");
    }

    /// <summary>
    /// Where to collect the link, from either shape the host answers with: a <c>302</c>'s
    /// <c>Location</c> (what a browser gets) or the <c>parent.location.href='…'</c> in a <c>200</c>
    /// body (what this client gets). Falls back to composing the URL from the id, since we know it.
    /// Internal for testing.
    /// </summary>
    internal static (string? FinishedUrl, string? Error) ParseFinishedUrl(HttpResponseSnapshot response, string uploadId)
    {
        if (response.LocationHeader is { Length: > 0 } location)
        {
            return (Absolute(location), null);
        }

        Match m = _jsRedirectRegex.Match(response.Body);
        if (m.Success)
        {
            return (Absolute(m.Groups[1].Value), null);
        }

        if (response.StatusCode is < 200 or >= 400)
        {
            return (null, $"upload.ee rejected the upload (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        // Both redirect shapes absent but the request was accepted: the id is ours, so the result page
        // is addressable anyway. Better than failing an upload that may well have landed.
        return ($"{Host}/?page=finished&upload_id={uploadId}", null);
    }

    /// <summary>Pulls the share link and the killcode delete link off the result page. Internal for
    /// testing.</summary>
    internal static (string? Url, string? DeleteLink, string? Error) ParseFinishedPage(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"upload.ee result page failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        Match view = _viewLinkRegex.Match(response.Body);
        if (!view.Success)
        {
            return (null, null, $"upload.ee result page carried no file link: {Snippet(response.Body)}");
        }

        Match kill = _killcodeRegex.Match(response.Body);
        return (Absolute(view.Groups[1].Value), kill.Success ? Absolute(kill.Groups[1].Value) : null, null);
    }

    private static string Absolute(string url)
        => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : Host + (url.StartsWith('/') ? url : "/" + url);

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response)";
        }

        string trimmed = body.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    private Task<HttpResponseSnapshot> GetAsync(AttemptContext ctx, string url)
        => _getOverride is not null
            ? _getOverride(url)
            : ctx.Handler.GetSnapshotAsync(url, BrowserHeaders(), ctx.Cancellation);

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Referer"] = Host + "/",
        ["Origin"] = Host,
    };

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadId)
    {
        // Both query parameters carry the same id: X-Progress-ID is what the progress endpoint polls
        // on, upload_id is what the script itself keys on. The capture sends both; so do we.
        string url = $"{UploadScriptUrl}?X-Progress-ID={uploadId}&upload_id={uploadId}";

        // The capture's POST carries the file and nothing else — no category, no token.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal);

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, url, extraFields, BrowserHeaders(), ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            url,
            fileFieldName: "upfile_0",
            extraFields: extraFields,
            headers: BrowserHeaders(),
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }
}
