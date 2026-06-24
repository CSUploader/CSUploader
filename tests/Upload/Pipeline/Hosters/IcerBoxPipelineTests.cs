// <copyright file="IcerBoxPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
/// Drives <see cref="IcerBoxPipeline"/> against canned responses captured from the live site
/// (2026-06-24): a JSON login → Bearer JWT, a per-request upload node, and a blueimp
/// <c>{"files":[{"id":…}]}</c> upload response.
/// </summary>
public class IcerBoxPipelineTests
{
    private const string LoginOkJson = """{"token":"jwt-abc-123"}""";
    private const string UploadServerOkJson = """{"data":{"domain":"s12.icerbox.com","ip":"109.232.227.22","port":8443,"upload":true,"upload_ftp":true}}""";
    private const string UploadOkJson = """{"files":[{"name":"Free_Test_Data_5MB_AVI.avi","size":5225142,"type":"video/avi","id":"gA8wBeel"}]}""";

    [Fact]
    public async Task RunAsync_HappyPath_LogsInDiscoversNodeUploadsAndReturnsIcerBoxLink()
    {
        List<(string Endpoint, IReadOnlyDictionary<string, string>? Headers)> uploadCalls = [];

        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: url => new HttpResponseSnapshot(200, url.EndsWith("/upload/server", StringComparison.Ordinal) ? UploadServerOkJson : "{}", []),
            uploadOverride: (_, endpoint, _, headers, _) =>
            {
                uploadCalls.Add((endpoint, headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
            });

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        TransferCompleted done = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://icerbox.com/gA8wBeel", done.FileUrl);

        // The bytes went to the discovered node (domain:port from upload/server) with the Bearer token.
        (string endpoint, IReadOnlyDictionary<string, string>? headers) = Assert.Single(uploadCalls);
        Assert.Equal("https://s12.icerbox.com:8443/", endpoint);
        Assert.Equal("Bearer jwt-abc-123", headers!["Authorization"]);

        // First login emits the auth lifecycle.
        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);
    }

    [Fact]
    public async Task RunAsync_LoginReturnsNoToken_FailsBeforeAnyUpload()
    {
        bool uploaded = false;
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(401, """{"message":"Invalid credentials"}""", []),
            getOverride: _ => new HttpResponseSnapshot(200, UploadServerOkJson, []),
            uploadOverride: (_, _, _, _, _) => { uploaded = true; return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, [])); });

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Invalid credentials", failed.Reason, StringComparison.Ordinal);
        Assert.False(uploaded);
        Assert.DoesNotContain(events, e => e is TransferStarted);
    }

    [Fact]
    public async Task RunAsync_UploadServerReturns401_SignalsRetryableSessionExpiry()
    {
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(401, "Unauthorized", []),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, [])));

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("retry", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(events, e => e is AuthFailed);
    }

    [Fact]
    public async Task RunAsync_UploadDisabledForAccount_FailsWithClearMessage()
    {
        const string ServerUploadDisabled = """{"data":{"domain":"s12.icerbox.com","port":8443,"upload":false}}""";
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, ServerUploadDisabled, []),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, [])));

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("isn't available", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UploadResponseHasNoFileId_SurfacesServerBody()
    {
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, UploadServerOkJson, []),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(400, """{"error":"File too large"}""", [])));

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("File too large", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadPostReturns401_SignalsRetryableSessionExpiry()
    {
        // Login + node discovery succeed, but the upload POST itself comes back 401 (token expired
        // between discovery and upload) — invalidate the cached token and bounce to the retry layer.
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, UploadServerOkJson, []),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(401, "Unauthorized", [])));

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        AttemptFailed failed = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("retry", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(events, e => e is AuthFailed);
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task RunAsync_UploadResponseHasTypeMismatchedId_FailsCleanlyWithoutCrashing()
    {
        // A 200 whose files[0].id is a NUMBER, not a string. The ValueKind guards must treat this as
        // "no usable id" and fail cleanly rather than throwing InvalidOperationException past the
        // JsonException catch (which would crash the pipeline with no link).
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, UploadServerOkJson, []),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, """{"files":[{"id":123}]}""", [])));

        List<UploadEvent> events = await DrainAsync(pipeline, MakeContext());

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain(events, e => e is TransferCompleted);
    }

    [Fact]
    public async Task CheckAccount_FreeAccount_ReturnsValidFreeWithEmail()
    {
        const string AccountJson = """{"data":{"id":"8L8EoP","email":"qepmo74208@minitts.net","has_premium":false,"premium":null}}""";
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, AccountJson, []));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "qepmo74208@minitts.net", "pw", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Equal("qepmo74208@minitts.net", result.DerivedUsername);
    }

    [Fact]
    public async Task CheckAccount_PremiumAccount_ReturnsPremiumWithExpiry()
    {
        const string AccountJson = """{"data":{"email":"vip@example.com","has_premium":true,"premium":"2027-01-15"}}""";
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(200, LoginOkJson, []),
            getOverride: _ => new HttpResponseSnapshot(200, AccountJson, []));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "vip@example.com", "pw", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
        Assert.Equal(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc), result.PremiumExpiry);
        Assert.Contains("Premium", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccount_LoginFails_ReturnsInvalidWithoutAccountCall()
    {
        bool accountCalled = false;
        IcerBoxPipeline pipeline = new(
            loginOverride: (_, _) => new HttpResponseSnapshot(401, """{"message":"Wrong password"}""", []),
            getOverride: _ => { accountCalled = true; return new HttpResponseSnapshot(200, "{}", []); });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "x@y.com", "bad", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Wrong password", result.Message, StringComparison.Ordinal);
        Assert.False(accountCalled);
    }

    private static async Task<List<UploadEvent>> DrainAsync(IFileHosterPipeline pipeline, AttemptContext ctx)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        return events;
    }

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.avi",
        FileName = "x.avi",
        FileSize = 5225142,
        HosterName = "IcerBox",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "IcerBox", Username = "qepmo74208@minitts.net", Password = "pw" },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
