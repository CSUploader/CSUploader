// <copyright file="KsharedPipelineTests.cs" company="CSUploader">
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
/// kshared — Emload's engine in a <c>/v1/</c> dialect. Every fixture is a real response.
/// <para>
/// Most of these guard one thing: the sign-in hands back THREE tokens that each go somewhere
/// different, and the host's error messages name the wrong problem when they are swapped.
/// </para>
/// </summary>
public class KsharedPipelineTests : IDisposable
{
    private const string SignInOkJson = """
        {"kind":"signedin","ID":"JejaDQ5pL8","name":{"first":"csuprobe","last":""},"email":"csuprobe@example.test","dp":"https://www.kshared.com/photo/dp/FAKE/{wxh}/picture.jpg","accesstoken":"FAKE.access.token","ut":"RkFLRS11dC1ibG9i","message":"","redirect":"https://www.kshared.com/drive","hash":"FAKE.hash.jwt","me":null}
        """;

    private const string NodeOkJson = """
        {"kind":true,"ID":"X0PxjmXL7r","uri":"https:\/\/wm765.kshared.com\/upload","token":"FAKE-node-token","remoteFile":null}
        """;

    private const string UploadOkJson = """
        {"kind":"fileSaved","file":{"ID":"oVjo5yy43D","uid":"JejaDQ5pL8","isFolder":false,"name":"release.r00","type":"application\/vnd.rar","size":4096,"privacy":"public","hasPassword":false,"stamp":1786373249000,"yid":1484115,"status":"live","folder":"root","downloads":0}}
        """;

    private static readonly string StoredSession = JsonSerializer.Serialize(new
    {
        uid = "JejaDQ5pL8",
        accesstoken = "FAKE.access.token",
        ut = "RkFLRS11dC1ibG9i",
        hash = "FAKE.hash.jwt",
        php = "PHPSESSID=fake-php",
        email = "csuprobe@example.test",
    });

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "csu-ks-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public KsharedPipelineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }

    // ── Identity and wiring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kshared_IsAccountOnly_WithNoGuessedCap()
    {
        KsharedPipeline pipeline = new();

        Assert.Equal("kshared", pipeline.Name);
        Assert.False(pipeline.SupportsAnonymousUpload);

        // Its own pre-flight took 100 GB without complaint and nothing publishes a figure, so the
        // host's answer is the gate rather than a number invented here.
        Assert.Null(pipeline.MaxFileSize);

        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("kshared"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("kshared"));
        Assert.Equal("kshared.com", FileHosterClient.FileHosters["kshared"]);
    }

    // ── The three tokens ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSignIn_KeepsAllThreeTokensApart()
    {
        (KsharedPipeline.KsharedSession? session, string? error) =
            KsharedPipeline.ParseSignIn(new HttpResponseSnapshot(200, SignInOkJson, []), "PHPSESSID=fake-php");

        Assert.Null(error);
        Assert.Equal("JejaDQ5pL8", session!.Uid);
        Assert.Equal("FAKE.access.token", session.AccessToken);   // -> the body/multipart field `ud`
        Assert.Equal("RkFLRS11dC1ibG9i", session.Ut);              // -> the field `ut`
        Assert.Equal("FAKE.hash.jwt", session.Hash);               // -> the Authorization Bearer
        Assert.Equal("PHPSESSID=fake-php", session.PhpSession);
    }

    [Theory]
    [InlineData("ID")]
    [InlineData("accesstoken")]
    [InlineData("ut")]
    [InlineData("hash")]
    public void ParseSignIn_MissingAnyOneToken_IsNotASession(string drop)
    {
        // A partial set does not fail loudly on this host — it produces "sessionExpired" on the next
        // call, which sends the user looking at their password. Better to refuse the sign-in here.
        Dictionary<string, JsonElement> fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SignInOkJson)!;
        fields.Remove(drop);

        (KsharedPipeline.KsharedSession? session, string? error) = KsharedPipeline.ParseSignIn(
            new HttpResponseSnapshot(200, JsonSerializer.Serialize(fields), []), null);

        Assert.Null(session);
        Assert.Contains("incomplete session", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSignIn_ARejection_KeepsTheHostsOwnWording()
    {
        (KsharedPipeline.KsharedSession? session, string? error) = KsharedPipeline.ParseSignIn(
            new HttpResponseSnapshot(200, """{"error":true,"reason":"badLogin","message":"Invalid Email / Password Combination"}""", []),
            null);

        Assert.Null(session);
        Assert.Equal("Invalid Email / Password Combination", error);
    }

    [Fact]
    public void Session_RoundTripsThroughStorage()
    {
        KsharedPipeline.KsharedSession? parsed = KsharedPipeline.KsharedSession.TryParse(StoredSession);

        Assert.NotNull(parsed);
        Assert.Equal("FAKE.hash.jwt", parsed!.Hash);
        Assert.Equal("PHPSESSID=fake-php", parsed.PhpSession);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"uid":"a","accesstoken":"b","ut":"c"}""")]     // no hash
    [InlineData("""{"uid":"a","accesstoken":"b","hash":"d"}""")]   // no ut
    public void SessionTryParse_AnythingIncomplete_IsNoSession(string? stored)
        => Assert.Null(KsharedPipeline.KsharedSession.TryParse(stored));

    // ── The node and the upload ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseNode_ReadsTheServer()
    {
        (KsharedPipeline.KsharedNode? node, string? error) =
            KsharedPipeline.ParseNode(new HttpResponseSnapshot(200, NodeOkJson, []));

        Assert.Null(error);
        Assert.Equal("https://wm765.kshared.com/upload", node!.Uri);
        Assert.Equal("X0PxjmXL7r", node.Id);
        Assert.Equal("FAKE-node-token", node.Token);
    }

    [Fact]
    public void ParseNode_SessionExpired_IsReworded_BecauseItAlsoMeansAWrongBearer()
    {
        (_, string? error) = KsharedPipeline.ParseNode(
            new HttpResponseSnapshot(200, """{"error":true,"reason":"sessionExpired"}""", []));

        Assert.Contains("no longer valid", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUploadResponse_BuildsTheLinkFromTheFileId()
    {
        (string? link, string? error) = KsharedPipeline.ParseUploadResponse(new HttpResponseSnapshot(200, UploadOkJson, []));

        Assert.Null(error);
        Assert.Equal("https://www.kshared.com/file/oVjo5yy43D", link);
    }

    [Fact]
    public void ParseUploadResponse_TheDiskRefusal_SaysItMayNotBeAboutSpaceAtAll()
    {
        // Measured: its node answers "disk" when the session tokens are wrong, not only when the
        // account is full — the same 2 MB file uploaded fine once the right `ut` was sent.
        (string? link, string? error) = KsharedPipeline.ParseUploadResponse(
            new HttpResponseSnapshot(200, """{"error":true,"reason":"disk","size":2097152}""", []));

        Assert.Null(link);
        Assert.Contains("space", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-check it in Account Manager", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUploadResponse_SurvivesAPhpNoticePrintedBeforeTheJson()
    {
        // Seen live from its upload node when a request arrived without a User-Agent. This app always
        // sends one, but being strict here would report a SUCCESSFUL upload as a failure — and the
        // user's retry would leave a duplicate file on the host.
        const string Noticed = """
            <br /> <b>Notice</b>:  Undefined index: HTTP_USER_AGENT in <b>/home/site/includes/core.php</b> on line <b>12</b><br />
            {"kind":"fileSaved","file":{"ID":"oVjo5yy43D","name":"release.r00"}}
            """;

        (string? link, string? error) = KsharedPipeline.ParseUploadResponse(new HttpResponseSnapshot(200, Noticed, []));

        Assert.Null(error);
        Assert.Equal("https://www.kshared.com/file/oVjo5yy43D", link);
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("Notice: something went wrong and then nothing")]
    public void ParseUploadResponse_JunkWithNoJsonAtAll_IsStillAFailure(string body)
    {
        // The tolerance is for a PREFIX, not a licence to accept anything.
        (string? link, string? error) = KsharedPipeline.ParseUploadResponse(new HttpResponseSnapshot(200, body, []));

        Assert.Null(link);
        Assert.Contains("wasn't JSON", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Uploading ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_SendsEachTokenToItsOwnPlace()
    {
        Recorder recorder = new();

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://www.kshared.com/file/oVjo5yy43D", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The Bearer is the hash JWT — sending the accesstoken there earns "sessionExpired".
        (string url, _, IReadOnlyDictionary<string, string> headers) = Assert.Single(recorder.Posts);
        Assert.Equal("https://www.kshared.com/v1/drive/get_server_for_upload", url);
        Assert.Equal("Bearer FAKE.hash.jwt", headers["Authorization"]);
        Assert.Equal("PHPSESSID=fake-php", headers["Cookie"]);

        UploadCall call = Assert.Single(recorder.Uploads);
        Assert.Equal("https://wm765.kshared.com/upload", call.Endpoint);
        Assert.Equal("JejaDQ5pL8", call.Fields["ui"]);
        Assert.Equal("RkFLRS11dC1ibG9i", call.Fields["ut"]);        // the ACCOUNT's ut…
        Assert.Equal("FAKE-node-token", call.Fields["token"]);      // …not the node's token
        Assert.Equal("FAKE.access.token", call.Fields["ud"]);
        Assert.Equal("X0PxjmXL7r", call.Fields["server"]);
        Assert.Equal("root", call.Fields["dir"]);
    }

    [Fact]
    public async Task Run_TellsTheNodeTheRealSize()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext() with { FileSize = 987654 };

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal(987654, JsonDocument.Parse(recorder.Posts[0].Json).RootElement.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Run_MintsAFreshFileIdPerUpload()
    {
        Recorder first = new();
        await DrainAsync(first.Pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Recorder second = new();
        await DrainAsync(second.Pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.NotEqual(first.Uploads[0].Fields["ID"], second.Uploads[0].Fields["ID"]);
    }

    [Fact]
    public async Task Run_Anonymously_IsRefusedLocally()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "kshared", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no anonymous upload", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Posts);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_WithNoStoredSession_SignsInFirst()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(signedIn: false);

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        // The home page comes first, for the PHP session the API calls are bound to.
        Assert.Contains("https://www.kshared.com/", recorder.Gets);
        Assert.Equal("https://www.kshared.com/v1/account/signin", recorder.Posts[0].Url);
        Assert.Equal("https://www.kshared.com/v1/drive/get_server_for_upload", recorder.Posts[1].Url);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private AttemptContext MakeContext(bool signedIn = true)
    {
        string path = Path.Combine(_tempDir, "release.r00");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, new byte[4096]);
        }

        return new AttemptContext
        {
            AttemptId = Guid.NewGuid(),
            FilePath = path,
            FileName = "release.r00",
            FileSize = 4096,
            HosterName = "kshared",
            Credentials = new FileHosterLoginDto
            {
                Id = 1,
                FileHosterName = "kshared",
                IsAnonymous = false,
                Username = "csuprobe@example.test",
                Password = "hunter2",
                SessionCookie = signedIn ? StoredSession : null,
            },
            Proxy = ProxyChoice.Direct,
            Handler = MakeHandler(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };
    }

    /// <summary>Stands in for the host and records what it was sent.</summary>
    private sealed class Recorder
    {
        public Recorder() => Pipeline = new KsharedPipeline(GetAsync, PostJsonAsync, UploadAsync);

        public KsharedPipeline Pipeline { get; }

        public List<string> Gets { get; } = [];

        public List<(string Url, string Json, IReadOnlyDictionary<string, string> Headers)> Posts { get; } = [];

        public List<UploadCall> Uploads { get; } = [];

        private Task<HttpResponseSnapshot> GetAsync(string url, IReadOnlyDictionary<string, string> headers)
        {
            Gets.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(200, "<html/>", ["PHPSESSID=fake-php; path=/"]));
        }

        private Task<HttpResponseSnapshot> PostJsonAsync(string url, string json, IReadOnlyDictionary<string, string> headers)
        {
            Posts.Add((url, json, headers));

            return Task.FromResult(url.Contains("account/signin", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, SignInOkJson, [])
                : new HttpResponseSnapshot(200, NodeOkJson, []));
        }

        private Task<HttpResponseSnapshot> UploadAsync(string filePath, string endpoint, IReadOnlyDictionary<string, string> fields)
        {
            Uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        }
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
