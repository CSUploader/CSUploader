// <copyright file="BuzzheavierPipelineTests.cs" company="CSUploader">
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
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Orchestration tests for <see cref="BuzzheavierPipeline"/> — the single raw PUT to
/// <c>w.buzzheavier.com/&lt;name&gt;</c> (anonymous or Bearer-account) → <c>{"data":{"id":…}}</c> →
/// <c>buzzheavier.com/&lt;id&gt;</c>. The PUT is stubbed via the test ctor (wire shapes come from the
/// 2026-07-08 live capture + the developer-API docs), so these lock the anon/account branch, the
/// auth-header wiring, the URL encoding, the link parse, the sign-in probe, and the failure branches.
/// </summary>
public class BuzzheavierPipelineTests
{
    private const string AccountId = "acctid012345"; // fake stand-in for the durable Bearer account id
    private const string OkBody = """{"code":201,"data":{"id":"bzfileid0001","name":"clip.avi"}}""";

    [Fact]
    public void Properties_DeclareBuzzheavierConfig()
    {
        BuzzheavierPipeline pipeline = new();
        Assert.Equal("Buzzheavier", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
        Assert.True(FileHosterClient.FileHosters.ContainsKey("Buzzheavier"));
        Assert.True(FileHosterClient.HasUnlimitedStorage("Buzzheavier"));
    }

    [Fact]
    public async Task RunAsync_Anonymous_PutsRawBodyWithNoAuthAndReturnsLink()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        Assert.Contains(events, e => e is TransferStarted);
        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://buzzheavier.com/bzfileid0001", done.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        Assert.Equal(1, up.Uploads);
        Assert.Equal("https://w.buzzheavier.com/clip.avi", up.LastUrl);
        Assert.False(up.LastHeaders!.ContainsKey("Authorization")); // anonymous — no bearer
    }

    [Fact]
    public async Task RunAsync_Account_PutsWithBearerAndReturnsLink()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(apiKey: AccountId), CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(1, up.Uploads);
        Assert.Equal("Bearer " + AccountId, up.LastHeaders!["Authorization"]);
        Assert.False(up.LastHeaders.ContainsKey("Cookie")); // the Bearer is the sole credential
    }

    [Fact]
    public async Task RunAsync_Account_NoStoredId_YieldsAttemptFailedWithoutUpload()
    {
        // A non-anonymous account with no stored id must fail before any bytes — otherwise the authless
        // PUT would silently land the file as an anonymous upload instead of in the account.
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(apiKey: null), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("isn't signed in", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, up.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_NonAsciiFilename_UrlEncodesThePath()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(anonymous: true) with { FileName = "元カレ (30m).mp4" };
        await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.StartsWith("https://w.buzzheavier.com/", up.LastUrl!, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", up.LastUrl!, StringComparison.Ordinal);
        Assert.Contains("%20", up.LastUrl!, StringComparison.Ordinal);
        Assert.Contains("%E5", up.LastUrl!, StringComparison.Ordinal); // UTF-8 lead byte of a Japanese char
    }

    [Fact]
    public async Task RunAsync_FilenameWithHash_FailsFastWithoutUpload()
    {
        // Buzzheavier's server rejects '#' in a name; over the raw PUT that arrives as a mid-stream socket
        // reset the retry layer would replay 3×. Catch it up front — no bytes sent, actionable message.
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(anonymous: true) with { FileName = "再教育 #1 (AVC).mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("'#'", fail.Reason, StringComparison.Ordinal);
        Assert.Contains("Rename", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, up.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_FilenameWithSemicolon_FailsFastWithoutUpload()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(anonymous: true) with { FileName = "Paladin; Agateram (4k).mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("';'", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("'#'", fail.Reason, StringComparison.Ordinal); // only the char actually present
        Assert.Equal(0, up.Uploads);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_FilenameWithBothBadChars_FailsNamingBoth()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(anonymous: true) with { FileName = "#1; take two.mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("'#' and ';'", fail.Reason, StringComparison.Ordinal);
        Assert.Contains("are not allowed", fail.Reason, StringComparison.Ordinal); // plural verb
        Assert.Equal(0, up.Uploads);
    }

    [Fact]
    public async Task RunAsync_AccountFilenameWithHash_FailsFastBeforeAccountUse()
    {
        // The name guard is universal: a bad name fails even with a valid stored account id, before any PUT.
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(apiKey: AccountId) with { FileName = "clip #2.mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("'#'", fail.Reason, StringComparison.Ordinal);
        Assert.Equal(0, up.Uploads);
    }

    [Fact]
    public async Task RunAsync_FilenameWithAcceptedSpecials_StillUploads()
    {
        // Regression guard: Buzzheavier accepts '@', '[', ']', '(', ')', '&', '+' etc. — only '#'/';' reject.
        // A too-broad "URL-reserved chars" rule would wrongly fail this real-world name.
        FakeUploader up = new(_ => new HttpResponseSnapshot(201, OkBody, []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        AttemptContext ctx = MakeContext(anonymous: true) with { FileName = "[BD] Show 05 (4k AV1@M10p DTS 2ch+5.1ch).mkv" };
        List<UploadEvent> events = await Drain(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(1, up.Uploads);
    }

    [Fact]
    public async Task RunAsync_BareIdResponse_IsParsed()
    {
        // The developer-API PUT response wasn't captured directly; tolerate a bare {"id":…} as well.
        FakeUploader up = new(_ => new HttpResponseSnapshot(200, """{"id":"bareid0002"}""", []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://buzzheavier.com/bareid0002", done.FileUrl);
    }

    [Fact]
    public async Task RunAsync_UploadRejected_YieldsAttemptFailedWithoutCompletion()
    {
        FakeUploader up = new(_ => new HttpResponseSnapshot(413, """{"code":413,"message":"file too large"}""", []));
        BuzzheavierPipeline pipeline = new(up.Upload);

        List<UploadEvent> events = await Drain(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("413", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadTransportFault_PropagatesOutOfRunAsync()
    {
        // A mid-send abort must PROPAGATE (retryable) — no file was committed, so the shared retry layer
        // re-uploads cleanly.
        BuzzheavierPipeline pipeline = new((_, _, _, _) =>
            throw new HttpRequestException(
                "reset",
                new UploadBodyTransferException(new IOException("conn reset", new SocketException(10054)))));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Drain(pipeline.RunAsync(MakeContext(anonymous: true), CancellationToken.None)));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
    }

    [Fact]
    public async Task CheckAccountAsync_StoredId_ReturnsValidWithoutWebView()
    {
        // A stored account id is durable — CheckAccount re-validates it offline with no auth service.
        BuzzheavierPipeline pipeline = new(authService: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: AccountId, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal(AccountId, result.ApiKey);
        Assert.Equal(AccountId, result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccountAsync_WebViewProbe_ReturnsAccountIdAsApiKey()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(
                It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractiveAuthResult(SessionCookieValue: string.Empty, CapturedUsername: null, ProbeValue: AccountId));
        BuzzheavierPipeline pipeline = new(auth.Object);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountId, result.ApiKey); // the account id from /api/account is the Bearer credential
        Assert.Equal(AccountId, result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccountAsync_SignInCancelled_ReturnsInvalid()
    {
        Mock<IInteractiveAuthService> auth = new();
        auth.Setup(a => a.AcquireSessionCookieAsync(
                It.IsAny<InteractiveAuthSpec>(), It.IsAny<string>(), It.IsAny<ProxyChoice?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InteractiveAuthResult?)null);
        BuzzheavierPipeline pipeline = new(auth.Object);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task CheckAccountAsync_NoAuthServiceAndNoKey_ReturnsInvalidWithPasteHint()
    {
        BuzzheavierPipeline pipeline = new(authService: null);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            username: string.Empty, password: string.Empty, apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("account id", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Captures the raw-PUT calls and answers each from a supplied responder.</summary>
    private sealed class FakeUploader(Func<string, HttpResponseSnapshot> respond)
    {
        public int Uploads { get; private set; }

        public string? LastUrl { get; private set; }

        public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }

        public Task<HttpResponseSnapshot> Upload(string filePath, string url, IReadOnlyDictionary<string, string>? headers, SpeedBudget? bps)
        {
            Uploads++;
            LastUrl = url;
            LastHeaders = headers;
            return Task.FromResult(respond(url));
        }
    }

    private static async Task<List<UploadEvent>> Drain(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static AttemptContext MakeContext(bool anonymous = false, string? apiKey = null) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\clip.avi",
        FileName = "clip.avi",
        FileSize = 5_225_142,
        HosterName = "Buzzheavier",
        Credentials = anonymous
            ? new FileHosterLoginDto { FileHosterName = "Buzzheavier", IsAnonymous = true }
            : new FileHosterLoginDto { Id = 12, FileHosterName = "Buzzheavier", ApiKey = apiKey },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };
}
