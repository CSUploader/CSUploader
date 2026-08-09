// <copyright file="FileCatPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
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
/// FileCat — account-only, on a JSON API that lives on its own <c>api.filecat.net</c> host. Fixtures
/// are the real replies (2026-08-09), verified by signing in and uploading. Two of these pin mistakes
/// that were actually made against the live service: the storage node needs the session cookie even
/// though its request carries no auth header, and a refusal arrives inside a 200.
/// </summary>
public class FileCatPipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string Satisfied =
        """{"link":"s7.filecat.net/upload/5158933","state":"satisfied","reject_reason":null,"reject_msg":null}""";

    private const string Uploaded =
        """{"id":6995941,"uid":"ADngcU","link":"https://filecat.net/f/ADngcU/probe.rar","sha1sum":"ae4d8c","filesize":4096,"cd_uid":"ZJoLeRlN"}""";

    public FileCatPipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_SendsTheSessionCookieToTheStorageNodeToo()
    {
        // THE one that was got wrong live. The node's request carries no Authorization header, so a
        // capture reads as credential-free — but SESS is issued for domain=.filecat.net, so a browser
        // sends it to sNN.filecat.net as well. Omitting it earns a 403 AFTER the whole file is up.
        IReadOnlyDictionary<string, string>? nodeHeaders = null;
        FileCatPipeline pipeline = new(
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, Satisfied, Array.Empty<string>())),
            (_, _, headers, _) =>
            {
                nodeHeaders = headers;
                return Task.FromResult(new HttpResponseSnapshot(200, Uploaded, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096, "sess-value"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.NotNull(nodeHeaders);
        Assert.Equal("SESS=sess-value", nodeHeaders!["Cookie"]);
    }

    [Fact]
    public async Task RunAsync_LinksWhatTheNodeReturns()
    {
        FileCatPipeline pipeline = MakePipeline([]);
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096, "s"), CancellationToken.None));

        Assert.Equal("https://filecat.net/f/ADngcU/probe.rar", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_PostsToTheNodeItWasGiven_MadeAbsolute()
    {
        // The API returns "s7.filecat.net/upload/…" with no scheme; posting that as-is would resolve
        // against api.filecat.net.
        List<string> endpoints = [];
        FileCatPipeline pipeline = MakePipeline(endpoints);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096, "s"), CancellationToken.None));

        Assert.Equal("https://s7.filecat.net/upload/5158933", Assert.Single(endpoints));
    }

    [Theory]
    // The load-bearing case: a refusal inside a 200. Asserted on the wording only the state branch
    // produces — an earlier version of this test checked for "File is too big", which the error's
    // body snippet echoes either way, so it passed even with the state check deleted.
    [InlineData("""{"link":null,"state":"rejected","reject_reason":"mfs","reject_msg":"File is too big"}""")]
    [InlineData("""{"link":null,"state":"rejected","reject_reason":"quota","reject_msg":null}""")]
    [InlineData("""{"link":"s7.filecat.net/upload/1","state":"rejected","reject_msg":"Nope"}""")]
    public void ParseUploadRequest_ReadsTheStateNotTheStatusCode(string body)
    {
        (string? node, string? error) =
            FileCatPipeline.ParseUploadRequest(new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Null(node);
        Assert.Contains("refused the upload", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("named no node", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUploadRequest_TheRejectionMessageIsPassedOn()
        => Assert.Contains(
            "File is too big",
            FileCatPipeline.ParseUploadRequest(new HttpResponseSnapshot(
                200,
                """{"link":null,"state":"rejected","reject_reason":"mfs","reject_msg":"File is too big"}""",
                Array.Empty<string>())).Error!,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("""{"state":"satisfied","link":null}""", "named no node")]
    [InlineData("not json", "wasn't JSON")]
    public void ParseUploadRequest_ASuccessThatNamesNoNode_IsAFailure(string body, string expected)
    {
        (string? node, string? error) =
            FileCatPipeline.ParseUploadRequest(new HttpResponseSnapshot(200, body, Array.Empty<string>()));

        Assert.Null(node);
        Assert.Contains(expected, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUploadRequest_MakesTheSchemelessLinkAbsolute()
    {
        (string? node, string? error) =
            FileCatPipeline.ParseUploadRequest(new HttpResponseSnapshot(200, Satisfied, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("https://s7.filecat.net/upload/5158933", node);
    }

    [Fact]
    public void ParseUploadResponse_TakesTheLinkAndTheDeleteCode()
    {
        (string? link, string? del, string? error) =
            FileCatPipeline.ParseUploadResponse(new HttpResponseSnapshot(200, Uploaded, Array.Empty<string>()));

        Assert.Null(error);
        Assert.Equal("https://filecat.net/f/ADngcU/probe.rar", link);

        // The only handle on the file besides the account's own list, so it is logged rather than lost.
        Assert.Equal("ZJoLeRlN", del);
    }

    [Fact]
    public void ReadSessionCookie_TakesSessAndNothingElse()
    {
        Assert.Equal("abc123", FileCatPipeline.ReadSessionCookie(
            new HttpResponseSnapshot(200, string.Empty, ["_ga=x; path=/", "SESS=abc123; domain=.filecat.net; HttpOnly"])));

        Assert.Null(FileCatPipeline.ReadSessionCookie(
            new HttpResponseSnapshot(200, string.Empty, ["_ga=x; path=/"])));
    }

    [Fact]
    public void ParseStorage_ReadsTheAccountsOwnFigures()
    {
        (long? used, long? total) = FileCatPipeline.ParseStorage("""{"used":10450284,"files":3,"total":2147483648,"trashed":0}""");
        Assert.Equal(10_450_284, used);
        Assert.Equal(2_147_483_648, total);
    }

    [Fact]
    public async Task RunAsync_Anonymous_RefusesWithoutTouchingTheNetwork()
    {
        // Its API answers 403 "Access denied" to a session-less upldreq, so there is nothing to try.
        List<string> endpoints = [];
        FileCatPipeline pipeline = MakePipeline(endpoints);

        AttemptContext ctx = MakeContext(4096, "s") with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "FileCat", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(endpoints);
        Assert.Contains("no anonymous upload", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RefusesAFileOverTheCap_BeforeAskingForANode()
    {
        List<string> endpoints = [];
        FileCatPipeline pipeline = MakePipeline(endpoints);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(2_097_152_001, "s"), CancellationToken.None));

        Assert.Empty(endpoints);
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public void FileCat_IsAccountOnly_AtTheCapItsApiEnforces()
    {
        FileCatPipeline pipeline = new();
        Assert.Equal("FileCat", pipeline.Name);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);

        // 2000 MiB — the largest file_size upldreq accepts; no page states it.
        Assert.Equal(2_097_152_000, pipeline.MaxFileSize);
        Assert.Equal("filecat.net", FileHosterClient.FileHosters["FileCat"]);

        // A plain JSON sign-in with no captcha, so no browser window opens.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("FileCat"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("FileCat"));
    }

    private static FileCatPipeline MakePipeline(List<string> endpoints) => new(
        (_, _, _) => Task.FromResult(new HttpResponseSnapshot(200, Satisfied, Array.Empty<string>())),
        (_, endpoint, _, _) =>
        {
            endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseSnapshot(200, Uploaded, Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size, string session) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "FileCat",
        Credentials = new FileHosterLoginDto
        {
            Id = 2,
            FileHosterName = "FileCat",
            IsAnonymous = false,
            Username = "me@example.com",
            Password = "pw",
            SessionCookie = session,
        },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }
}
