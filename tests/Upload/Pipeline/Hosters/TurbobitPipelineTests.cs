// <copyright file="TurbobitPipelineTests.cs" company="CSUploader">
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
/// Turbobit — HitFile's sibling on the same SPA platform. Because the two are near-identical, what's
/// pinned hardest here is the value that MUST differ (<c>apptype=fd1</c>, not HitFile's <c>fd2</c>)
/// and the host each request goes to. Shapes are from a signed-in browser capture (2026-08-01), with
/// the account's appId replaced.
/// </summary>
public class TurbobitPipelineTests
{
    private const string DiscoveryJson = """{"urls":["https:\/\/s347.turbobit.net\/uploadfile"]}""";
    private const string UploadOkJson = """{"result":true,"id":"l6jw3v95nvpf","message":"Everything is ok"}""";
    private const string AppId = "demo_app_id_32chars_placeholder0";

    [Fact]
    public async Task RunAsync_DiscoversANode_ThenPostsTheBrowsersFieldSet()
    {
        List<(string Url, string Body)> posts = [];
        List<UploadCall> calls = [];

        TurbobitPipeline pipeline = new(
            postJsonOverride: (url, body) =>
            {
                posts.Add((url, body));
                return Task.FromResult(new HttpResponseSnapshot(200, DiscoveryJson, Array.Empty<string>()));
            },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(AppId), CancellationToken.None));

        Assert.Equal("https://turbobit.net/l6jw3v95nvpf.html", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // Discovery asks the APP host for exactly one node.
        (string url, string body) = Assert.Single(posts);
        Assert.Equal("https://app.turbobit.net/api/upload/urls", url);
        Assert.Equal("""{"count":1}""", body);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://s347.turbobit.net/uploadfile", call.Endpoint);

        // fd1 — HitFile's fd2 identifies a DIFFERENT application to the storage node.
        Assert.Equal("fd1", call.ExtraFields["apptype"]);
        Assert.NotEqual("fd2", call.ExtraFields["apptype"]);
        Assert.Equal("0", call.ExtraFields["folder_id"]);
        Assert.Equal(AppId, call.ExtraFields["user_id"]);
        Assert.Equal("https://turbobit.net", call.Headers!["Origin"]);
    }

    [Fact]
    public async Task RunAsync_WithoutAnAppId_FailsBeforeTouchingTheNetwork()
    {
        TurbobitPipeline pipeline = new(
            postJsonOverride: (_, _) => throw new InvalidOperationException("must not discover"),
            uploadOverride: (_, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(apiKey: null), CancellationToken.None));

        Assert.Contains("sign in", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_ServerRefuses_SurfacesItsOwnMessage()
    {
        TurbobitPipeline pipeline = new(
            postJsonOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, DiscoveryJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"result":false,"message":"File is too big"}""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(AppId), CancellationToken.None));

        Assert.Contains("File is too big", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.OfType<TransferCompleted>());
    }

    [Fact]
    public async Task RunAsync_DiscoveryReturnsNoUrl_FailsWithoutUploading()
    {
        List<UploadCall> calls = [];
        TurbobitPipeline pipeline = new(
            postJsonOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, """{"urls":[]}""", Array.Empty<string>())),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(AppId), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    [InlineData("""{"appId":"abc123","username":"user@example.invalid"}""", "abc123", "user@example.invalid")]
    [InlineData("""{"appId":"abc123","username":null}""", "abc123", null)]     // signed in, header absent
    [InlineData("""{"appId":null,"username":"x"}""", null, null)]              // not signed in yet
    [InlineData("", null, null)]                                               // probe still polling
    [InlineData("not json", null, null)]
    public void ParseProbeResult_ReadsTheAppIdAndOnlyPairsAUsernameWithOne(string probe, string? appId, string? username)
    {
        (string? gotAppId, string? gotUser) = TurbobitPipeline.ParseProbeResult(probe);
        Assert.Equal(appId, gotAppId);
        Assert.Equal(username, gotUser);
    }

    [Fact]
    public async Task CheckAccount_WithAPastedAppId_IsAcceptedWithoutAWebView()
    {
        // authService is null: if this tried to open a sign-in window it would report that instead.
        TurbobitPipeline pipeline = new();
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "someone", "secret", AppId, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AppId, result.ApiKey);
    }

    [Fact]
    public void Turbobit_IsAccountOnly_AndRegisteredAsAnApiKeyHoster()
    {
        TurbobitPipeline pipeline = new();
        Assert.Equal("Turbobit", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);

        // HitFile also serves anonymous uploads; Turbobit's guest cap (200 MB) is smaller than one
        // part of a typical release, so offering it would mostly produce failures.
        Assert.False(pipeline.SupportsAnonymousUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Turbobit"));
        Assert.Equal("turbobit.net", FileHosterClient.FileHosters["Turbobit"]);

        // The appId lives in the ApiKey slot, so it can be pasted as well as captured.
        Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode("Turbobit"));
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

    private static AttemptContext MakeContext(string? apiKey) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 4096,
        HosterName = "Turbobit",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Turbobit", ApiKey = apiKey },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);
}
