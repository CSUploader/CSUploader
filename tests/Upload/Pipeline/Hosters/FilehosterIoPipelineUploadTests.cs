// <copyright file="FilehosterIoPipelineUploadTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Orchestration tests for <see cref="FilehosterIoPipeline"/> — the anonymous XFileSharing "xfspro"
/// flow (start_upload → put_chunk.cgi ×N → import_file). The network is stubbed via the internal test
/// ctor, so these lock in the event sequence, the multi-chunk split, the single-SID invariant, the
/// share link, and the failure branches. Verified against a live anonymous round-trip 2026-06-29.
/// </summary>
public class FilehosterIoPipelineUploadTests
{
    private const string StartJson = """{"url":"https://filehoster.io/cgi-bin","plugin":"xfspro"}""";
    private const string ChunkOk = """{"status":"OK"}""";
    private const string ImportJson = """{"file_code":"5t9zsw3wnl0h","links":{"download_link":"https://filehoster.io/5t9zsw3wnl0h/x.bin.html","delete_link":"https://filehoster.io/5t9zsw3wnl0h/x.bin.html?killcode=zz"},"status":"OK"}""";

    [Fact]
    public void Properties_DeclareFilehosterIoConfig()
    {
        FilehosterIoPipeline pipeline = new();
        Assert.Equal("Filehoster.io", pipeline.Name);
        Assert.Equal(10L * 1000 * 1000 * 1000, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Filehoster.io"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_SingleChunk_UploadsAndReturnsLink()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(calls, new(200, StartJson, []), new(200, ImportJson, []), _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferProgress);
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://filehoster.io/5t9zsw3wnl0h/x.bin.html", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // One chunk for a 1 MiB file, PUT to the CGI base from start_upload.
        (string chunkUrl, string chunkSid, long basePos, long len, long total) = Assert.Single(calls.Chunks);
        Assert.Equal("https://filehoster.io/cgi-bin/put_chunk.cgi", chunkUrl);
        Assert.Equal(0, basePos);
        Assert.Equal(1_048_576L, len);
        Assert.Equal(1_048_576L, total);

        // start_upload form metadata.
        Assert.Equal("start_upload", calls.Forms[0].Form["op"]);
        Assert.Equal("x.bin", calls.Forms[0].Form["file_name"]);
        Assert.Equal("1048576", calls.Forms[0].Form["file_size"]);

        // import_file: empty sess_id (anonymous) and the SAME SID the chunk used.
        Assert.Equal("import_file", calls.Forms[1].Form["op"]);
        Assert.Equal("x.bin", calls.Forms[1].Form["fname"]);
        Assert.Equal(string.Empty, calls.Forms[1].Form["sess_id"]);
        Assert.Equal(chunkSid, calls.Forms[1].Form["sid"]);
    }

    [Fact]
    public async Task RunAsync_LargeFile_SplitsIntoOrderedChunksUnderOneSid()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(calls, new(200, StartJson, []), new(200, ImportJson, []), _ => new(200, ChunkOk, []));

        // This proves the offset/length MATH the pipeline computes. The actual in-order byte streaming
        // through the shared FileStream is covered by ChunkSliceStreamTests (slices at 0/100/200).
        const long Mib = 1024 * 1024;
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(fileSize: 250 * Mib), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());

        // 250 MiB → 100 + 100 + 50, in order, contiguous offsets.
        Assert.Equal(3, calls.Chunks.Count);
        Assert.Equal((0L, 100 * Mib), (calls.Chunks[0].BasePos, calls.Chunks[0].Len));
        Assert.Equal((100 * Mib, 100 * Mib), (calls.Chunks[1].BasePos, calls.Chunks[1].Len));
        Assert.Equal((200 * Mib, 50 * Mib), (calls.Chunks[2].BasePos, calls.Chunks[2].Len));

        // All chunks (and import_file) share exactly one SID.
        Assert.Single(calls.Chunks.Select(c => c.Sid).Distinct());
        Assert.Equal(calls.Chunks[0].Sid, calls.Forms[1].Form["sid"]);
    }

    [Fact]
    public async Task RunAsync_FileExceedsAnonymousCap_YieldsAttemptFailedWithoutAnyHttp()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(calls, new(200, StartJson, []), new(200, ImportJson, []), _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(fileSize: (10L * 1000 * 1000 * 1000) + 1), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("filehoster.io", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls.Forms);
        Assert.Empty(calls.Chunks);
    }

    [Fact]
    public async Task RunAsync_StartUploadReturnsNoUrl_YieldsAttemptFailedWithoutChunks()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new HttpResponseSnapshot(200, """{"plugin":"xfspro"}""", []),
            import: new(200, ImportJson, []),
            chunkResult: _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls.Chunks);
    }

    [Fact]
    public async Task RunAsync_ChunkNotAccepted_YieldsAttemptFailedWithoutImport()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new(200, StartJson, []),
            import: new(200, ImportJson, []),
            chunkResult: _ => new HttpResponseSnapshot(200, """{"status":"error"}""", []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Single(calls.Forms); // only start_upload ran; import_file never reached
    }

    [Fact]
    public async Task RunAsync_ChunkErrorWithOkInAnotherField_StillRejected()
    {
        // The success check is scoped to status=="OK". An error body that carries a quoted "OK" in some
        // OTHER field must NOT be read as success (a plain substring check would have false-positived).
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new(200, StartJson, []),
            import: new(200, ImportJson, []),
            chunkResult: _ => new HttpResponseSnapshot(200, """{"status":"error","note":"OK"}""", []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Single(calls.Forms); // import_file never reached
    }

    [Fact]
    public async Task RunAsync_LaterChunkTransportFault_PropagatesAndImportNeverRuns()
    {
        // The point of the reclassify decision is the MULTI-chunk case: chunk 2 of 3 faults after 0/1
        // were accepted under the SID. It must propagate (retryable) and import_file must not have run.
        FhCalls calls = new();
        const long Mib = 1024 * 1024;
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new(200, StartJson, []),
            import: new(200, ImportJson, []),
            chunkResult: i => i < 2
                ? new HttpResponseSnapshot(200, ChunkOk, [])
                : throw new HttpRequestException(
                    "reset",
                    new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054)))));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(fileSize: 250 * Mib), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex));
        Assert.Equal(3, calls.Chunks.Count); // 0/1 accepted, 2 attempted then faulted
        Assert.Single(calls.Forms); // only start_upload; import_file never ran
    }

    [Fact]
    public async Task RunAsync_StartUploadRequestThrows_YieldsAttemptFailedWithoutChunks()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                calls.Forms.Add((url, new Dictionary<string, string>(form)));
                throw new HttpRequestException("network down");
            },
            chunkPutOverride: (url, sid, basePos, len, total, progress) =>
            {
                calls.Chunks.Add((url, sid, basePos, len, total));
                progress(basePos + len, total);
                return new HttpResponseSnapshot(200, ChunkOk, []);
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("start_upload", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Empty(calls.Chunks);
    }

    [Fact]
    public async Task RunAsync_ImportRequestThrows_YieldsAttemptFailedAfterChunks()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                calls.Forms.Add((url, new Dictionary<string, string>(form)));
                if (form["op"] == "import_file")
                {
                    throw new HttpRequestException("import boom");
                }

                return new HttpResponseSnapshot(200, StartJson, []);
            },
            chunkPutOverride: (url, sid, basePos, len, total, progress) =>
            {
                calls.Chunks.Add((url, sid, basePos, len, total));
                progress(basePos + len, total);
                return new HttpResponseSnapshot(200, ChunkOk, []);
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("import_file", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
        Assert.Single(calls.Chunks); // the bytes uploaded before import_file failed
    }

    [Fact]
    public async Task RunAsync_FileExactlyAtCap_ProceedsToUpload()
    {
        // Pins the strict '>' boundary: a file of EXACTLY the cap is allowed (a regression to '>=' fails).
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(calls, new(200, StartJson, []), new(200, ImportJson, []), _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(fileSize: 10L * 1000 * 1000 * 1000), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        Assert.Single(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_ZeroByteFile_SendsOneEmptyChunk()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(calls, new(200, StartJson, []), new(200, ImportJson, []), _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(fileSize: 0), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        (_, _, long basePos, long len, long total) = Assert.Single(calls.Chunks);
        Assert.Equal((0L, 0L, 0L), (basePos, len, total));
    }

    [Fact]
    public async Task RunAsync_ImportReturnsNoLink_FallsBackToFileCode()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new(200, StartJson, []),
            import: new HttpResponseSnapshot(200, """{"file_code":"abc123def456","status":"OK"}""", []), // no links block
            chunkResult: _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://filehoster.io/abc123def456", tc.FileUrl);
    }

    [Fact]
    public async Task RunAsync_ImportReturnsNothingUsable_YieldsAttemptFailed()
    {
        FhCalls calls = new();
        FilehosterIoPipeline pipeline = MakePipeline(
            calls,
            start: new(200, StartJson, []),
            import: new HttpResponseSnapshot(200, """{"status":"OK"}""", []), // no links, no file_code
            chunkResult: _ => new(200, ChunkOk, []));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_ChunkTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send chunk reset must PROPAGATE (retryable) so the shared retry layer re-runs against a
        // fresh SID — import_file never ran, so nothing was committed.
        FilehosterIoPipeline pipeline = new(
            postFormOverride: (_, form) => form["op"] == "start_upload"
                ? new HttpResponseSnapshot(200, StartJson, [])
                : new HttpResponseSnapshot(200, ImportJson, []),
            chunkPutOverride: (_, _, _, _, _, _) =>
                throw new HttpRequestException(
                    "Error while copying content to a stream",
                    new UploadBodyTransferException(
                        new IOException("Unable to write data to the transport connection", new SocketException(10054)))));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None)));

        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    // ---- Account login + storage ----

    private const string LoginPageHtml = """<form name="FL"><input type="hidden" name="token" value="abc123def4560789"><input name="login" value=""></form>""";
    private const string AccountHtml = """<div class="text-dark small">Used space</div> <div class="fs-4 fw-bold text-dark">0.06</div>""";

    [Fact]
    public async Task CheckAccountAsync_ValidCredentials_SignedInWithUsedSpace()
    {
        FilehosterIoPipeline pipeline = new(
            getOverride: (url, _) => url.Contains("/login/", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, LoginPageHtml, [])
                : new HttpResponseSnapshot(200, AccountHtml, []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["xfss=SESSION123; path=/; HttpOnly"], "https://filehoster.io/account/"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException("no chunk during CheckAccount"));

        AccountCheckResult result = await pipeline.CheckAccountAsync("user@x.com", "pw", null, DummyHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("user@x.com", result.DerivedUsername);
        Assert.Equal((long)(0.06 * (1L << 30)), result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes); // no quota shown → Unlimited
    }

    [Fact]
    public async Task CheckAccountAsync_WrongPassword_ReturnsInvalidWithServerReason()
    {
        // A wrong password re-renders the login page as 200 (no xfss) with the reason in an alert box.
        // The failure message must surface that reason, not a bare HTTP code.
        FilehosterIoPipeline pipeline = new(
            getOverride: (_, _) => new HttpResponseSnapshot(200, LoginPageHtml, []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(200, """<form name="FL"><div class="alert alert-danger">Incorrect Login or Password</div></form>""", []),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException());

        AccountCheckResult result = await pipeline.CheckAccountAsync("user", "wrong", null, DummyHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Incorrect Login or Password", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccountAsync_MissingCredentials_ReturnsInvalidWithoutNetwork()
    {
        FilehosterIoPipeline pipeline = new(
            getOverride: (_, _) => throw new InvalidOperationException("no network for empty creds"),
            postFormOverride: (_, _) => throw new InvalidOperationException("no network for empty creds"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException());

        AccountCheckResult result = await pipeline.CheckAccountAsync(string.Empty, string.Empty, null, DummyHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RefreshStorageAsync_ParsesUsedSpaceWithUnlimitedQuota()
    {
        FilehosterIoPipeline pipeline = new(
            getOverride: (url, _) => url.Contains("/login/", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, LoginPageHtml, [])
                : new HttpResponseSnapshot(200, AccountHtml, []),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["xfss=S"], "https://filehoster.io/account/"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException());

        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            new FileHosterLoginDto { FileHosterName = "Filehoster.io", Username = "u", Password = "p" },
            DummyHandler(),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal((long)(0.06 * (1L << 30)), usage!.Value.UsedBytes);
        Assert.Null(usage.Value.QuotaBytes);
    }

    [Theory]
    [InlineData("""<div class="small">Used space</div> <div class="fs-4 fw-bold">2.00</div>""", 2147483648L)] // 2 GiB
    [InlineData("""<div>Used space</div><div>Buy more!</div><div class="small">Used space</div> <div class="fs-4 fw-bold">0.50</div>""", 536870912L)] // decoy "Used space" first → fs-4 anchor lands on the real panel
    [InlineData("""<div class="small">Used space</div> <div class="fs-4"><span>3.00</span></div>""", 3221225472L)] // nested span before the number
    [InlineData("""<div class="small">Used space</div> <div class="fs-4">N/A</div>""", null)] // non-numeric value
    [InlineData("<div>no storage panel here</div>", null)] // panel absent
    public void ParseUsedSpace_ReadsGiBValueOrNull(string html, long? expected)
    {
        Assert.Equal(expected, FilehosterIoPipeline.ParseUsedSpace(html));
    }

    [Fact]
    public async Task CheckAccountAsync_LoginOkButStorageReadFails_StillValidWithNullUsed()
    {
        // Login succeeds (xfss captured) but the /account/ GET throws — a transient storage hiccup must
        // NOT invalidate a good account; it returns IsValid=true with Used=null.
        FilehosterIoPipeline pipeline = new(
            getOverride: (url, _) => url.Contains("/login/", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, LoginPageHtml, [])
                : throw new HttpRequestException("account page down"),
            postFormOverride: (_, _) => new HttpResponseSnapshot(302, string.Empty, ["xfss=SESSION123"], "https://filehoster.io/account/"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException());

        AccountCheckResult result = await pipeline.CheckAccountAsync("user", "pw", null, DummyHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_ForwardsScrapedTokenInLoginForm()
    {
        string? sentToken = null;
        FilehosterIoPipeline pipeline = new(
            getOverride: (_, _) => new HttpResponseSnapshot(200, LoginPageHtml, []),
            postFormOverride: (_, form) =>
            {
                sentToken = form.TryGetValue("token", out string? t) ? t : null;
                return new HttpResponseSnapshot(302, string.Empty, ["xfss=S"], "https://filehoster.io/account/");
            },
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException());

        await pipeline.CheckAccountAsync("user", "pw", null, DummyHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Equal("abc123def4560789", sentToken); // the token scraped from the login page is POSTed
    }

    [Fact]
    public async Task RunAsync_AccountUpload_SendsXfssAsSessId()
    {
        AccountUpCalls calls = new();
        FilehosterIoPipeline pipeline = new(
            getOverride: (_, _) => new HttpResponseSnapshot(200, LoginPageHtml, []),
            postFormOverride: (_, form) =>
            {
                switch (form["op"])
                {
                    case "login": return new HttpResponseSnapshot(302, string.Empty, ["xfss=ACCT_SESS"], "https://filehoster.io/account/");
                    case "import_file": calls.ImportForm = new Dictionary<string, string>(form); return new HttpResponseSnapshot(200, ImportJson, []);
                    default: return new HttpResponseSnapshot(200, StartJson, []); // start_upload
                }
            },
            chunkPutOverride: (_, sid, _, _, _, progress) =>
            {
                calls.ChunkSid = sid;
                progress(1L, 1L);
                return new HttpResponseSnapshot(200, ChunkOk, []);
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: false), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("ACCT_SESS", calls.ImportForm!["sess_id"]); // the account's xfss rides as sess_id
        Assert.Equal(calls.ChunkSid, calls.ImportForm["sid"]);
    }

    [Fact]
    public async Task RunAsync_AccountLoginFails_YieldsAttemptFailedWithoutUpload()
    {
        AccountUpCalls calls = new();
        FilehosterIoPipeline pipeline = new(
            getOverride: (_, _) => new HttpResponseSnapshot(200, LoginPageHtml, []),
            postFormOverride: (_, form) =>
            {
                if (form["op"] == "login")
                {
                    return new HttpResponseSnapshot(200, "login page again", []); // no xfss → login failed
                }

                calls.NonLoginPosts++;
                return new HttpResponseSnapshot(200, StartJson, []);
            },
            chunkPutOverride: (_, _, _, _, _, _) =>
            {
                calls.Chunks++;
                return new HttpResponseSnapshot(200, ChunkOk, []);
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(anonymous: false), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferStarted);
        Assert.Equal(0, calls.NonLoginPosts); // never reached start_upload
        Assert.Equal(0, calls.Chunks);
    }

    private static HttpHandler DummyHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private sealed class AccountUpCalls
    {
        public Dictionary<string, string>? ImportForm { get; set; }

        public string? ChunkSid { get; set; }

        public int NonLoginPosts { get; set; }

        public int Chunks { get; set; }
    }

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static FilehosterIoPipeline MakePipeline(
        FhCalls calls,
        HttpResponseSnapshot start,
        HttpResponseSnapshot import,
        Func<int, HttpResponseSnapshot> chunkResult)
    {
        int chunkIndex = 0;
        return new FilehosterIoPipeline(
            postFormOverride: (url, form) =>
            {
                calls.Forms.Add((url, new Dictionary<string, string>(form)));
                return form["op"] == "start_upload" ? start : import;
            },
            chunkPutOverride: (url, sid, basePos, len, total, progress) =>
            {
                calls.Chunks.Add((url, sid, basePos, len, total));
                progress(basePos + len, total); // drive cumulative progress
                return chunkResult(chunkIndex++);
            });
    }

    private sealed class FhCalls
    {
        public List<(string Url, Dictionary<string, string> Form)> Forms { get; } = [];

        public List<(string Url, string Sid, long BasePos, long Len, long Total)> Chunks { get; } = [];
    }

    private static AttemptContext MakeContext(long fileSize = 1_048_576L, bool anonymous = true) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.bin",
        FileName = "x.bin",
        FileSize = fileSize,
        HosterName = "Filehoster.io",
        Credentials = new FileHosterLoginDto
        {
            Id = 7,
            FileHosterName = "Filehoster.io",
            IsAnonymous = anonymous,
            Username = "acctuser",
            Password = "acctpass",
        },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
