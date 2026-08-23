// <copyright file="EmloadPipelineTests.cs" company="CSUploader">
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
/// Emload — an account-only SPA API. Every fixture is a real response, from a browser capture and
/// from live probes; the session values are faked.
/// <para>
/// Two behaviours carry most of these: the four-cookie jar (send three and every call answers
/// "oauth", which reads like a broken sign-in) and the node call's size pre-flight, which is what
/// stops an upload the account has no room for before a byte moves.
/// </para>
/// </summary>
public class EmloadPipelineTests : IDisposable
{
    private const string SignInOkJson = """
        {"kind":"userSigned","uid":"JlwXEYRgVY","ut":"eyJhbGciOiJIUzI1NiJ9.FAKE-JWT-PAYLOAD.FAKE-SIGNATURE","ud":"FAKEdWQtYmxvYg","si":"v3a8xqgype"}
        """;

    private const string NodeOkJson = """
        {"kind":true,"server":{"ID":"K9xw0n5pz5","uri":"https:\/\/s4b.emload.com\/upload","token":"FAKE-server-token","remoteFile":null}}
        """;

    private const string UploadOkJson = """
        {"kind":"fileSaved","file":{"ID":"23eqJ2lE3x","token":"QkNnV3BzZFNxcnAxbkVVWTB3R1RBdz09","yid":7198164,"uid":"JlwXEYRgVY","isDir":false,"name":"release.r00","type":"unknown","size":2097152,"mood":2,"passw":false,"stamp":1786284922000,"price":"0.00","status":"active","folder":0,"downs":0},"disk":7322294}
        """;

    private const string OauthErrorJson = """{"error":true,"reason":"oauth"}""";
    private const string DiskErrorJson = """{"error":true,"reason":"disk"}""";

    private const string StoredJar = "__uid=JlwXEYRgVY; __ut=FAKE-JWT; __ud=FAKE-ud; __si=FAKE-si";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "csu-em-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public EmloadPipelineTests() => Directory.CreateDirectory(_tempDir);

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
    public void Emload_IsAccountOnly_WithNoGuessedCap()
    {
        EmloadPipeline pipeline = new();

        Assert.Equal("Emload", pipeline.Name);

        // Measured: the node call without a session token answers reason:"oauth".
        Assert.False(pipeline.SupportsAnonymousUpload);

        // Deliberately null. There is no per-file cap to state — what stops an upload is the
        // account's remaining storage, and the node call is told the size and refuses first.
        Assert.Null(pipeline.MaxFileSize);

        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("Emload"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("Emload"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("Emload"));
        Assert.Equal("emload.com", FileHosterClient.FileHosters["Emload"]);
    }

    // ── The session ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSignIn_TakesAllFourValues_BecauseAllFourAreCookies()
    {
        (EmloadPipeline.EmloadSession? session, string? error) =
            EmloadPipeline.ParseSignIn(new HttpResponseSnapshot(200, SignInOkJson, []));

        Assert.Null(error);
        Assert.Equal("JlwXEYRgVY", session!.Uid);
        Assert.Equal("v3a8xqgype", session.Si);
        Assert.Equal(
            "__uid=JlwXEYRgVY; __ut=eyJhbGciOiJIUzI1NiJ9.FAKE-JWT-PAYLOAD.FAKE-SIGNATURE; __ud=FAKEdWQtYmxvYg; __si=v3a8xqgype",
            session.ToCookieHeader());
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("ut")]
    [InlineData("ud")]
    [InlineData("si")]
    public void ParseSignIn_MissingAnyOneValue_IsNotASession(string drop)
    {
        // Any three of the four earn {"error":true,"reason":"oauth"} on the next call — a refusal that
        // reads like a rejected sign-in rather than a missing cookie, which is exactly the confusion
        // worth failing early to avoid.
        string mutilated = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<Dictionary<string, object>>(SignInOkJson)!
                .Where(kv => kv.Key != drop)
                .ToDictionary(kv => kv.Key, kv => kv.Value));

        (EmloadPipeline.EmloadSession? session, string? error) =
            EmloadPipeline.ParseSignIn(new HttpResponseSnapshot(200, mutilated, []));

        Assert.Null(session);
        Assert.Contains("incomplete session", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSignIn_ARejection_KeepsTheHostsOwnWording()
    {
        // It distinguishes an unknown address from a wrong password, and its words are better than
        // anything invented here.
        (EmloadPipeline.EmloadSession? session, string? error) = EmloadPipeline.ParseSignIn(
            new HttpResponseSnapshot(200, """{"error":true,"reason":"pass","message":"Invalid Email / Password Combination"}""", []));

        Assert.Null(session);
        Assert.Equal("Invalid Email / Password Combination", error);
    }

    [Fact]
    public void ParseSignIn_ARejectionWithNoMessage_StillSaysWhatToCheck()
    {
        (_, string? error) = EmloadPipeline.ParseSignIn(new HttpResponseSnapshot(200, """{"error":true,"reason":"pass"}""", []));

        Assert.Contains("email address and password", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionRoundTrips_ThroughTheStoredCookieHeader()
    {
        EmloadPipeline.EmloadSession? parsed = EmloadPipeline.EmloadSession.TryParse(StoredJar);

        Assert.NotNull(parsed);
        Assert.Equal("JlwXEYRgVY", parsed!.Uid);
        Assert.Equal(StoredJar, parsed.ToCookieHeader());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("__uid=a; __ut=b; __ud=c")]          // three of four
    [InlineData("__uid=a; __ut=b; __si=d")]
    [InlineData("PHPSESSID=x")]
    public void SessionTryParse_AnythingShortOfAllFour_IsNoSession(string? stored)
        => Assert.Null(EmloadPipeline.EmloadSession.TryParse(stored));

    [Fact]
    public void NewNonce_IsTwelveCharacters_AndVaries()
    {
        // Not a credential: the site's own post() sends `__ha || randit(12)` and the server takes
        // whatever arrives. Minting one keeps the requests shaped like the site's.
        HashSet<string> seen = [];
        for (int i = 0; i < 50; i++)
        {
            string nonce = EmloadPipeline.NewNonce();
            Assert.Equal(12, nonce.Length);
            Assert.All(nonce, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
            seen.Add(nonce);
        }

        Assert.True(seen.Count > 40);
    }

    // ── The node call, which is also the size pre-flight ──────────────────────────────────────────

    [Fact]
    public void ParseNode_ReadsTheServer()
    {
        (EmloadPipeline.EmloadNode? node, string? error) =
            EmloadPipeline.ParseNode(new HttpResponseSnapshot(200, NodeOkJson, []), 4096);

        Assert.Null(error);
        Assert.Equal("https://s4b.emload.com/upload", node!.Uri);
        Assert.Equal("K9xw0n5pz5", node.Id);
        Assert.Equal("FAKE-server-token", node.Token);
    }

    [Fact]
    public void ParseNode_TheDiskRefusal_NamesTheSizeAndSaysWhatToDo()
    {
        // The reason this call happens before any bytes: the host checks the DECLARED size against
        // the account's remaining storage, so a file that could never land is stopped for free.
        (EmloadPipeline.EmloadNode? node, string? error) = EmloadPipeline.ParseNode(
            new HttpResponseSnapshot(200, DiskErrorJson, []), 500L * 1024 * 1024 * 1024);

        Assert.Null(node);
        Assert.Contains("storage", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("536.87 GB", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseNode_TheOauthRefusal_MeansTheSessionNotTheCredentials()
    {
        (_, string? error) = EmloadPipeline.ParseNode(new HttpResponseSnapshot(200, OauthErrorJson, []), 4096);

        Assert.Contains("no longer valid", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(503, NodeOkJson, "503")]
    [InlineData(200, """{"error":true,"reason":"maintenance"}""", "maintenance")]
    [InlineData(200, """{"kind":true}""", "no upload server")]
    [InlineData(200, "<html>nope</html>", "wasn't JSON")]
    public void ParseNode_RefusesAnythingElse(int status, string body, string fragment)
    {
        (EmloadPipeline.EmloadNode? node, string? error) =
            EmloadPipeline.ParseNode(new HttpResponseSnapshot(status, body, []), 4096);

        Assert.Null(node);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── The upload reply ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseUploadResponse_BuildsTheLinkFromTheFilesTOKEN()
    {
        // The site's own script does `${base}file/${token}` — the token, not the ID, and the /v2/
        // base. The apex form without /v2/ only redirects.
        (string? link, string? error) = EmloadPipeline.ParseUploadResponse(new HttpResponseSnapshot(200, UploadOkJson, []));

        Assert.Null(error);
        Assert.Equal("https://www.emload.com/v2/file/QkNnV3BzZFNxcnAxbkVVWTB3R1RBdz09", link);
    }

    [Theory]
    [InlineData(500, "<html>500</html>", "rejected the upload")]
    [InlineData(200, DiskErrorJson, "ran out of storage")]
    [InlineData(200, """{"error":true,"reason":"oauth"}""", "oauth")]
    [InlineData(200, """{"kind":"fileSaved","file":{"ID":"23eqJ2lE3x"}}""", "no link")]
    [InlineData(200, "not json", "wasn't JSON")]
    public void ParseUploadResponse_ExplainsEveryFailure(int status, string body, string fragment)
    {
        (string? link, string? error) = EmloadPipeline.ParseUploadResponse(new HttpResponseSnapshot(status, body, []));

        Assert.Null(link);
        Assert.Contains(fragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Retrying a sign-in, but only the right failures ───────────────────────────────────────────

    [Theory]
    [InlineData(520, "error code: 520", true)]                    // what was actually seen live
    [InlineData(500, "<html>oops</html>", true)]
    [InlineData(200, "<html>edge page</html>", true)]             // a 200 that isn't the API's answer
    // A 5xx counts on its own, even when the body happens to parse: an origin that is failing is not
    // delivering a verdict about the password, whatever shape it answers in.
    [InlineData(503, """{"error":true,"reason":"maintenance"}""", true)]
    [InlineData(200, """{"error":true,"reason":"pass"}""", false)] // a verdict, not a hiccup
    [InlineData(200, SignInOkJson, false)]
    public void LooksTransient_SeparatesAHiccupFromAVerdict(int status, string body, bool expected)
        => Assert.Equal(expected, EmloadPipeline.LooksTransient(new HttpResponseSnapshot(status, body, [])));

    [Fact]
    public async Task CheckAccount_A520OnTheFirstTry_IsRetriedOnce()
    {
        // Seen live: an identical request 520'd between ones that worked. A refused sign-in has
        // created nothing, so this is the one call here that is safe to repeat.
        int calls = 0;
        EmloadPipeline pipeline = new((_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseSnapshot(520, "error code: 520", [])
                : new HttpResponseSnapshot(200, SignInOkJson, []));
        });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CheckAccount_AWrongPassword_IsNotRetried()
    {
        // Re-posting a password after the host has said no is how an account gets itself locked.
        int calls = 0;
        EmloadPipeline pipeline = new((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseSnapshot(
                200, """{"error":true,"reason":"pass","message":"Invalid Email / Password Combination"}""", []));
        });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid Email / Password Combination", result.Message);
        Assert.Equal(1, calls);
    }

    // ── Signing in and re-checking ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAccount_StoresTheWholeJar_AndKeepsTheEmailAsTyped()
    {
        List<(string Url, string Json, IReadOnlyDictionary<string, string> Headers)> posts = [];
        EmloadPipeline pipeline = new((url, json, headers) =>
        {
            posts.Add((url, json, headers));
            return Task.FromResult(new HttpResponseSnapshot(200, SignInOkJson, []));
        });

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Contains("__si=v3a8xqgype", result.SessionCookie!, StringComparison.Ordinal);
        Assert.Equal("csuprobe@example.test", result.DerivedUsername);

        // The ut JWT's own exp is seven days out; claiming longer would strand an upload on a dead
        // session it thought was fine.
        Assert.True(result.SessionCookieExpiresUtc < DateTime.UtcNow.AddDays(8));
        Assert.True(result.SessionCookieExpiresUtc > DateTime.UtcNow.AddDays(6));

        (string url, string json, IReadOnlyDictionary<string, string> headers) = Assert.Single(posts);
        Assert.Equal("https://www.emload.com/v2/app/user/signin", url);
        Assert.Contains("\"em\":\"csuprobe@example.test\"", json, StringComparison.Ordinal);
        Assert.Contains("\"robo\":\"__\"", json, StringComparison.Ordinal);   // the page's honeypot, sent as it sends it
        Assert.StartsWith("Bearer ", headers["Authorization"], StringComparison.Ordinal);
        Assert.False(headers.ContainsKey("Cookie"));                          // nothing to send yet
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("csuprobe@example.test", "")]
    public async Task CheckAccount_WithoutBothHalves_AsksForThem_WithoutCallingTheHost(string user, string password)
    {
        EmloadPipeline pipeline = new((_, _, _) => throw new InvalidOperationException("must not post"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            user, password, null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("email address and password", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccount_AsksTheDriveTree_WithTheWholeJar_AndNoPassword()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Headers)> posts = [];
        EmloadPipeline pipeline = new((url, _, headers) =>
        {
            posts.Add((url, headers));
            return Task.FromResult(new HttpResponseSnapshot(200, """{"kind":"tree","list":[]}""", []));
        });

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, StoredJar, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(StoredJar, result.SessionCookie);

        (string url, IReadOnlyDictionary<string, string> headers) = Assert.Single(posts);
        Assert.Equal("https://www.emload.com/v2/app/drive/get_tree", url);
        Assert.Equal(StoredJar, headers["Cookie"]);
    }

    [Theory]
    [InlineData(200, OauthErrorJson)]
    [InlineData(500, "<html>down</html>")]
    public async Task RefreshAccount_ARejectedSession_AsksForAFreshSignIn(int status, string body)
    {
        EmloadPipeline pipeline = new((_, _, _) => Task.FromResult(new HttpResponseSnapshot(status, body, [])));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, StoredJar, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("sign in again", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccount_APartialJar_IsRejectedWithoutCallingTheHost()
    {
        EmloadPipeline pipeline = new((_, _, _) => throw new InvalidOperationException("must not post"));

        AccountCheckResult result = await pipeline.RefreshAccountAsync(
            null, "__uid=a; __ut=b", MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    // ── Uploading ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Anonymously_IsRefusedLocally_WithoutAskingTheHost()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder) with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "Emload", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("no anonymous upload", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Posts);
        Assert.Empty(recorder.Uploads);
    }

    [Fact]
    public async Task Run_WithAStoredJar_GoesStraightToTheNode_AndSendsTheSessionThreeWays()
    {
        Recorder recorder = new();

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder), CancellationToken.None));

        Assert.Equal("https://www.emload.com/v2/file/QkNnV3BzZFNxcnAxbkVVWTB3R1RBdz09", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // A stored jar means no sign-in: the node call is the only API call.
        (string url, _, IReadOnlyDictionary<string, string> headers) = Assert.Single(recorder.Posts);
        Assert.Equal("https://www.emload.com/v2/app/drive/get_available_server", url);
        Assert.Equal(StoredJar, headers["Cookie"]);

        // …and the same three values ride the multipart, which is why they're stored apart rather
        // than as one opaque blob.
        UploadCall call = Assert.Single(recorder.Uploads);
        Assert.Equal("https://s4b.emload.com/upload", call.Endpoint);
        Assert.Equal("JlwXEYRgVY", call.Fields["ui"]);
        Assert.Equal("FAKE-JWT", call.Fields["ut"]);
        Assert.Equal("FAKE-ud", call.Fields["ud"]);
        Assert.Equal("K9xw0n5pz5", call.Fields["server"]);
        Assert.Equal("FAKE-server-token", call.Fields["token"]);
        Assert.Equal("root", call.Fields["dir"]);
    }

    [Fact]
    public async Task Run_TheFileIdIsMinted_AndMatchesTheOneTheNodeWasToldAbout()
    {
        // The id ties the node's reservation to the upload that follows, so it must be the same in
        // both calls — and different for every file, or two uploads would claim one reservation.
        Recorder first = new();
        await DrainAsync(first.Pipeline.RunAsync(MakeContext(first), CancellationToken.None));

        string reserved = JsonDocument.Parse(first.Posts[0].Json).RootElement.GetProperty("file").GetProperty("ID").GetString()!;
        Assert.Equal(reserved, first.Uploads[0].Fields["ID"]);

        Recorder second = new();
        await DrainAsync(second.Pipeline.RunAsync(MakeContext(second), CancellationToken.None));
        Assert.NotEqual(reserved, second.Uploads[0].Fields["ID"]);
    }

    [Fact]
    public async Task Run_TellsTheNodeTheRealSize_SoTheDiskCheckIsMeaningful()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder) with { FileSize = 12345 };

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        JsonElement file = JsonDocument.Parse(recorder.Posts[0].Json).RootElement.GetProperty("file");
        Assert.Equal(12345, file.GetProperty("size").GetInt64());
        Assert.Equal("release.r00", file.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Run_WithNoStoredJar_SignsInFirst()
    {
        Recorder recorder = new();
        AttemptContext ctx = MakeContext(recorder, jar: null);

        await DrainAsync(recorder.Pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Equal("https://www.emload.com/v2/app/user/signin", recorder.Posts[0].Url);
        Assert.Equal("https://www.emload.com/v2/app/drive/get_available_server", recorder.Posts[1].Url);
    }

    [Fact]
    public async Task Run_WhenTheAccountHasNoRoom_NothingIsSent()
    {
        Recorder recorder = new() { Node = new HttpResponseSnapshot(200, DiskErrorJson, []) };

        List<UploadEvent> events = await DrainAsync(recorder.Pipeline.RunAsync(MakeContext(recorder), CancellationToken.None));

        Assert.Contains("storage", Assert.IsType<AttemptFailed>(Assert.Single(events)).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(recorder.Uploads);
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

    private AttemptContext MakeContext(Recorder recorder, string? jar = StoredJar)
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
            HosterName = "Emload",
            Credentials = new FileHosterLoginDto
            {
                Id = 1,
                FileHosterName = "Emload",
                IsAnonymous = false,
                Username = "csuprobe@example.test",
                Password = "hunter2",
                SessionCookie = jar,
            },
            Proxy = ProxyChoice.Direct,
            Handler = MakeHandler(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedBudget = SpeedBudget.Unlimited,
            Cancellation = default,
        };
    }

    /// <summary>Stands in for the host and records what it was sent.</summary>
    private sealed class Recorder
    {
        public Recorder() => Pipeline = new EmloadPipeline(PostJsonAsync, UploadAsync);

        public EmloadPipeline Pipeline { get; }

        public List<(string Url, string Json, IReadOnlyDictionary<string, string> Headers)> Posts { get; } = [];

        public List<UploadCall> Uploads { get; } = [];

        public HttpResponseSnapshot Node { get; init; } = new(200, NodeOkJson, []);

        private Task<HttpResponseSnapshot> PostJsonAsync(string url, string json, IReadOnlyDictionary<string, string> headers)
        {
            Posts.Add((url, json, headers));

            return Task.FromResult(url.Contains("user/signin", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, SignInOkJson, [])
                : Node);
        }

        private Task<HttpResponseSnapshot> UploadAsync(string filePath, string endpoint, IReadOnlyDictionary<string, string> fields)
        {
            Uploads.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
            return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, []));
        }
    }

    private sealed record UploadCall(string FilePath, string Endpoint, IReadOnlyDictionary<string, string> Fields);
}
