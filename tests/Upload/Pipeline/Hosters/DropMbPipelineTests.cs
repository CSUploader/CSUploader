// <copyright file="DropMbPipelineTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Text.Json;
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
/// DropMB — a Pingvin Share instance, anonymous or signed in. Fixtures are the real replies its API
/// gave (2026-08-09), verified by uploading at one chunk, across the 10 MB chunk boundary, and signed
/// in — the last of which came back with <c>"creator":{"username":…}</c>, which is how attribution is
/// checked here. The chunk-id rule is pinned first: it is the one thing a single-chunk capture of a
/// real upload could not reveal.
/// </summary>
public class DropMbPipelineTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string ShareCreated =
        """{"id":"abc","name":null,"expiration":"1970-01-01T00:00:00.000Z","description":null}""";

    private const string ChunkAccepted =
        """{"id":"90360eff-ebad-497b-848a-19dd6e6afa67","name":"probe.rar"}""";

    public DropMbPipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_ThreadsTheFileIdThroughEveryChunkAfterTheFirst()
    {
        // THE one that matters. The host does not tie slices together by share + filename: the id it
        // issues for the first chunk has to come back on all the rest, or chunk 2 is refused with
        // `unexpected_chunk_index, expectedChunkIndex: 0`. Measured against the live host.
        List<string> endpoints = [];
        DropMbPipeline pipeline = MakePipeline(endpoints, []);

        const long Size = (2L * 10_000_000) + 5_000_000;   // three chunks
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(Size), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(3, endpoints.Count);

        // The first must NOT carry an id (there is none yet); every later one must carry the issued one.
        Assert.DoesNotContain("&id=", endpoints[0], StringComparison.Ordinal);
        Assert.Contains("&id=90360eff-ebad-497b-848a-19dd6e6afa67", endpoints[1], StringComparison.Ordinal);
        Assert.Contains("&id=90360eff-ebad-497b-848a-19dd6e6afa67", endpoints[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NumbersTheChunksFromZero_AndDeclaresTheTotalOnEachOne()
    {
        List<string> endpoints = [];
        DropMbPipeline pipeline = MakePipeline(endpoints, []);

        await DrainAsync(pipeline.RunAsync(MakeContext((2L * 10_000_000) + 5_000_000), CancellationToken.None));

        Assert.Equal(["0", "1", "2"], endpoints.Select(e => Between(e, "chunkIndex=", "&")));
        Assert.All(endpoints, e => Assert.Contains("totalChunks=3", e, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_AFirstChunkThatNamesNoFile_StopsBeforeSendingMore()
    {
        // Carrying on would put another 10 MB on the wire only to be refused for the same reason.
        List<string> endpoints = [];
        DropMbPipeline pipeline = new(
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())),
            (endpoint, _, _) =>
            {
                endpoints.Add(endpoint);
                return Task.FromResult(new HttpResponseSnapshot(201, """{"name":"probe.rar"}""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(25_000_000), CancellationToken.None));

        Assert.Single(endpoints);
        Assert.Contains("nothing to attach", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_AsksForTheLongestRetention_NotTheOneItsOwnUploaderSends()
    {
        // Its site sends "1-years" and the instance allows up to five; "never" is accepted and was
        // verified to serve. Same default-is-worse shape as Filego, GigaFile and Litterbox.
        List<string> bodies = [];
        DropMbPipeline pipeline = new(
            (_, json, _) =>
            {
                if (json is not null) bodies.Add(json);
                return Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>()));
            },
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("never", JsonDocument.Parse(bodies[0]).RootElement.GetProperty("expiration").GetString());
    }

    [Fact]
    public async Task RunAsync_MintsAnUnguessableShareId_NotTheHostsFourCharacterDefault()
    {
        // The id is the ENTIRE access control on a share — there is no per-file secret — and the
        // instance's own default length is 4.
        List<string> endpoints = [];
        DropMbPipeline pipeline = MakePipeline(endpoints, []);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));
        string first = Between(endpoints[0], "/api/shares/", "/files");

        endpoints.Clear();
        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));
        string second = Between(endpoints[0], "/api/shares/", "/files");

        Assert.NotEqual(first, second);
        Assert.All([first, second], id => Assert.Matches("^[a-z0-9]{16}$", id));
    }

    [Fact]
    public async Task RunAsync_SignedIn_PutsTheAccessTokenCookieOnEveryCall()
    {
        // The cookie is the whole account mechanism; without it the share is filed under nobody and
        // the upload still succeeds, so nothing downstream would notice it missing.
        List<IReadOnlyDictionary<string, string>?> headers = [];
        DropMbPipeline pipeline = new(
            (_, _, h) => { headers.Add(h); return Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())); },
            (_, h, _) => { headers.Add(h); return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext(4096) with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 4, FileHosterName = "DropMB", IsAnonymous = false,
                Username = "someone", Password = "pw", SessionCookie = "jwt-token-value",
            },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.NotEmpty(headers);
        Assert.All(headers, h => Assert.Equal("access_token=jwt-token-value", h!["Cookie"]));
    }

    [Fact]
    public async Task RunAsync_Anonymous_SendsNoCookieAtAll()
    {
        List<IReadOnlyDictionary<string, string>?> headers = [];
        DropMbPipeline pipeline = new(
            (_, _, h) => { headers.Add(h); return Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())); },
            (_, h, _) => { headers.Add(h); return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())); });

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.All(headers, h => Assert.False(h!.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task RunAsync_AnAccountThatCannotSignIn_RefusesInsteadOfPublishingAnonymously()
    {
        // Without a token the share is still created — under nobody — and the user gets a link that
        // looks fine. Refusing is the only outcome that can't mislead.
        List<string> endpoints = [];
        DropMbPipeline pipeline = new(
            (url, _, _) => Task.FromResult(url.EndsWith("/signIn", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(401, """{"message":"Wrong username or password"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())),
            (endpoint, _, _) => { endpoints.Add(endpoint); return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext(4096) with
        {
            Credentials = new FileHosterLoginDto
            {
                FileHosterName = "DropMB", IsAnonymous = false, Username = "someone", Password = "wrong",
            },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(endpoints);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_AShareThatIsNeverCompleted_IsNotAnUpload()
    {
        // The bytes can all be up and the share still be a draft, so a failed complete is a failure.
        DropMbPipeline pipeline = new(
            (url, _, _) => Task.FromResult(url.EndsWith("/complete", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(500, """{"message":"boom"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())),
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains("publish", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("access_token=abc.def.ghi; Path=/; HttpOnly", "abc.def.ghi")]
    [InlineData("refresh_token=zzz; Path=/", null)]
    public void ReadAccessToken_PrefersTheCookieTheSignInSets(string setCookie, string? expected)
    {
        HttpResponseSnapshot response = new(200, "{}", [setCookie]);
        Assert.Equal(expected, DropMbPipeline.ReadAccessToken(response));
    }

    [Fact]
    public void ReadAccessToken_FallsBackToTheBodysAccessToken()
    {
        // The reply carries the same JWT twice; either is a usable credential.
        HttpResponseSnapshot response = new(200, """{"accessToken":"body-jwt"}""", Array.Empty<string>());
        Assert.Equal("body-jwt", DropMbPipeline.ReadAccessToken(response));
    }

    [Fact]
    public async Task RunAsync_RefusesAFileOverTheHostsPublishedCap_BeforeCreatingAShare()
    {
        List<string> endpoints = [];
        DropMbPipeline pipeline = MakePipeline(endpoints, []);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(512_000_001), CancellationToken.None));

        Assert.Empty(endpoints);
        Assert.Single(events.OfType<AttemptFailed>());
    }

    [Fact]
    public void DropMB_TakesAnonymousAndAccounts_AtTheCapItPublishes()
    {
        DropMbPipeline pipeline = new();
        Assert.Equal("DropMB", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);
        Assert.Equal(512_000_000, pipeline.MaxFileSize);
        Assert.Equal("dropmb.com", FileHosterClient.FileHosters["DropMB"]);

        // A plain JSON sign-in with no captcha, so no browser window is ever opened.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("DropMB"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("DropMB"));
    }

    private static string Between(string s, string start, string end)
    {
        int i = s.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int j = s.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }

    private static DropMbPipeline MakePipeline(List<string> endpoints, List<string> bodies) => new(
        (string url, string? json, IReadOnlyDictionary<string, string>? headers) =>
        {
            _ = url;
            _ = headers;
            if (json is not null)
            {
                bodies.Add(json);
            }

            return Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>()));
        },
        (string endpoint, IReadOnlyDictionary<string, string>? headers, long length) =>
        {
            _ = headers;
            _ = length;
            endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "DropMB",
        Credentials = new FileHosterLoginDto { FileHosterName = "DropMB", IsAnonymous = true },
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
