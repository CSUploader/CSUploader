// <copyright file="FileMiragePipelineTests.cs" company="CSUploader">
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
/// FileMirage — chunked, anonymous or signed in. Fixtures are the real bodies its node, its upload
/// endpoint and its login returned (2026-08-08). Verified by uploading: 4 MB (one chunk) and 101 MiB
/// (two) anonymously, both served back at full size; and a signed-in file that appeared in the
/// account's own file list while a deliberately-wrong-token upload of the same shape did not.
/// </summary>
public class FileMiragePipelineTests : IDisposable
{
    /// <summary>A real file on disk: the pipeline opens one before the chunk loop, so the stubbed
    /// chunks still need something to open. Its length is irrelevant — the chunking is driven by
    /// <see cref="AttemptContext.FileSize"/>, which is what lets a two-line test cover a 300 MB split.</summary>
    private readonly string _file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rar");

    private const string ServersJson =
        """{"success":true,"data":{"server":"https://store1.filemirage.com","upload_id":"msk3g645d865"}}""";

    private const string DoneJson =
        """{"success":true,"data":{"url":"https://filemirage.com/file/4pkeqbqw"}}""";

    /// <summary>What a chunk that isn't the last one answers: a success with no url yet.</summary>
    private const string PendingJson = """{"success":true,"data":{"uploaded":true}}""";

    public FileMiragePipelineTests() => File.WriteAllBytes(_file, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_AsksForANode_ThenSendsOneChunkCarryingItsOwnFieldSet()
    {
        List<string> gets = [];
        List<string> endpoints = [];
        List<Dictionary<string, string>> fields = [];

        FileMiragePipeline pipeline = MakePipeline(gets, endpoints, fields, _ => DoneJson);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://filemirage.com/api/servers", Assert.Single(gets));

        // The upload goes to the node the host named, never to the site itself.
        Assert.Equal("https://store1.filemirage.com/upload.php", Assert.Single(endpoints));

        Dictionary<string, string> sent = Assert.Single(fields);
        Assert.Equal("probe.rar", sent["filename"]);
        Assert.Equal("0", sent["chunk_number"]);
        Assert.Equal("1", sent["total_chunks"]);

        Assert.Equal("https://filemirage.com/file/4pkeqbqw", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task RunAsync_SplitsAtTheHostsOwn99MbChunkSize_AndNumbersFromZero()
    {
        // Its page declares upload_chunk_size: 99 (MB). A 101 MiB file is therefore two chunks — the
        // split that the live 101 MiB upload exercised, and the one a single-chunk test can't reach.
        // chunk_number is 0-based and total_chunks is on EVERY chunk, which is how the host knows when
        // to assemble; getting either wrong leaves the file in pieces on their side.
        List<Dictionary<string, string>> fields = [];
        const long Size = (99L * 1024 * 1024) + (2L * 1024 * 1024);

        FileMiragePipeline pipeline = MakePipeline([], [], fields, i => i == 0 ? PendingJson : DoneJson);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(Size), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(["0", "1"], fields.Select(f => f["chunk_number"]));
        Assert.All(fields, f => Assert.Equal("2", f["total_chunks"]));

        // All chunks of one file share one id, or the host assembles nothing.
        Assert.Single(fields.Select(f => f["upload_id"]).Distinct());
    }

    [Fact]
    public async Task RunAsync_SendsTheUploadIdTheNodeLookupHandedBack()
    {
        // What the published API documents. Its own web uploader ignores the returned id and keys one
        // off Date.now() instead — which two files started in the same millisecond would share, and a
        // shared id means the host assembles them into each other. A server-minted id cannot collide.
        List<Dictionary<string, string>> fields = [];
        FileMiragePipeline pipeline = MakePipeline([], [], fields, _ => DoneJson);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal("msk3g645d865", Assert.Single(fields)["upload_id"]);
    }

    [Fact]
    public async Task RunAsync_WhenTheLookupNamesNoId_MintsAFreshUnguessableOnePerFile()
    {
        // The fallback must not reintroduce the collision: two files that both fall back still need
        // different ids.
        List<Dictionary<string, string>> fields = [];
        const string NoId = """{"success":true,"data":{"server":"https://store1.filemirage.com"}}""";

        FileMiragePipeline pipeline = new(
            _ => Task.FromResult(new HttpResponseSnapshot(200, NoId, Array.Empty<string>())),
            (_, sent, _, _) =>
            {
                fields.Add(new Dictionary<string, string>(sent, StringComparer.Ordinal));
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));
        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal(2, fields.Count);
        Assert.NotEqual(fields[0]["upload_id"], fields[1]["upload_id"]);
        Assert.All(fields, f => Assert.Matches("^[0-9a-f]{16}$", f["upload_id"]));
    }

    [Fact]
    public async Task RunAsync_StopsAtTheFirstBadChunk_WithoutSendingTheRest()
    {
        // A chunk that fails means the assembled file would be corrupt, so the remaining chunks are
        // wasted transfer — on a 50 GiB host that is the difference between a quick error and hours.
        List<Dictionary<string, string>> fields = [];
        const long Size = (99L * 1024 * 1024) * 3;

        FileMiragePipeline pipeline = new(
            _ => Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>())),
            (_, sent, _, _) =>
            {
                fields.Add(new Dictionary<string, string>(sent, StringComparer.Ordinal));
                return Task.FromResult(fields.Count == 2
                    ? new HttpResponseSnapshot(500, "nope", Array.Empty<string>())
                    : new HttpResponseSnapshot(200, PendingJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(Size), CancellationToken.None));

        Assert.Equal(2, fields.Count);
        Assert.Contains("chunk 2/3", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServersJson, "https://store1.filemirage.com")]
    [InlineData("""{"success":true,"data":{"server":"https://store1.filemirage.com/"}}""", "https://store1.filemirage.com")]
    [InlineData("""{"success":true,"data":{"upload_id":"x"}}""", null)]   // named no server
    [InlineData("""{"success":false,"message":"no"}""", null)]
    [InlineData("<html>maintenance</html>", null)]
    public void ReadNode_TakesTheServerOrNothing(string body, string? expected)
    {
        // A missing server used to be the interesting case: without this the endpoint becomes
        // "/upload.php" and the file is POSTed at whatever that resolves to.
        Assert.Equal(expected, FileMiragePipeline.ReadNode(body).Server?.TrimEnd('/'));
    }

    [Fact]
    public void ParseChunkResponse_A200ThatSaysSuccessFalse_IsAFailure()
    {
        // Success-shaped failure: the envelope carries its own flag and a false one rides inside a 200.
        (string? url, string? error) = FileMiragePipeline.ParseChunkResponse(
            new HttpResponseSnapshot(200, """{"success":false,"message":"File type not allowed"}""", Array.Empty<string>()),
            0,
            1);

        Assert.Null(url);
        Assert.Contains("File type not allowed", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseChunkResponse_AnIntermediateChunkHasNoUrl_AndThatIsNotAnError()
    {
        (string? url, string? error) = FileMiragePipeline.ParseChunkResponse(
            new HttpResponseSnapshot(200, PendingJson, Array.Empty<string>()),
            0,
            2);

        Assert.Null(url);
        Assert.Null(error);
    }

    [Fact]
    public async Task RunAsync_RefusesAFileOverTheHostsStatedCap_BeforeSendingAnything()
    {
        List<string> gets = [];
        FileMiragePipeline pipeline = MakePipeline(gets, [], [], _ => DoneJson);

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext(53_687_091_201), CancellationToken.None));

        Assert.Empty(gets);
        Assert.Contains("50", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FileMirage_TakesAnonymousAndAccounts_AtTheCapItsOwnPageDeclares()
    {
        FileMiragePipeline pipeline = new();
        Assert.Equal("FileMirage", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        Assert.True(((IFileHosterPipeline)pipeline).SupportsAccounts);

        // Its API token is durable and printed on /user/api, which makes "let the user paste it"
        // tempting — but nothing on the service can validate one, so the credential is the login.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("FileMirage"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("FileMirage"));

        Assert.Equal(53_687_091_200, pipeline.MaxFileSize);
        Assert.Equal("filemirage.com", FileHosterClient.FileHosters["FileMirage"]);
    }

    [Fact]
    public async Task RunAsync_SignedIn_PutsTheAccountsTokenOnEveryChunk()
    {
        // The bearer is the ENTIRE difference between a signed-in upload and an anonymous one, and
        // the host attributes by it — a file sent without it lands under no account at all.
        List<IReadOnlyDictionary<string, string>> headers = [];
        FileMiragePipeline pipeline = new(
            _ => Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>())),
            (_, sent, sentHeaders, _) =>
            {
                headers.Add(new Dictionary<string, string>(sentHeaders, StringComparer.Ordinal));
                return Task.FromResult(new HttpResponseSnapshot(
                    200,
                    sent["chunk_number"] == "1" ? DoneJson : PendingJson,
                    Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext((99L * 1024 * 1024) + 4096) with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 3,
                FileHosterName = "FileMirage",
                IsAnonymous = false,
                Username = "someone",
                ApiKey = "FAKE-T0KE-N000-DEMO",
            },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, headers.Count);
        Assert.All(headers, h => Assert.Equal("Bearer FAKE-T0KE-N000-DEMO", h["authorization"]));
    }

    [Fact]
    public async Task RunAsync_Anonymous_SendsTheEmptyAuthorizationItsOwnClientSends()
    {
        List<IReadOnlyDictionary<string, string>> headers = [];
        FileMiragePipeline pipeline = new(
            _ => Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>())),
            (_, _, sentHeaders, _) =>
            {
                headers.Add(new Dictionary<string, string>(sentHeaders, StringComparer.Ordinal));
                return Task.FromResult(new HttpResponseSnapshot(200, DoneJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Equal(string.Empty, Assert.Single(headers)["authorization"]);
    }

    [Fact]
    public async Task RunAsync_AnAccountWithNoToken_RefusesInsteadOfUploadingAsAVisitor()
    {
        // The one that matters most. Without the bearer the host still answers 200 with a working
        // link and files it under nobody — so an upload here would look completely successful while
        // silently dropping the account. Nothing downstream can detect it, so it must not happen.
        List<string> gets = [];
        List<Dictionary<string, string>> fields = [];
        FileMiragePipeline pipeline = MakePipeline(gets, [], fields, _ => DoneJson);

        AttemptContext ctx = MakeContext(4096) with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "FileMirage", IsAnonymous = false, ApiKey = null },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(gets);
        Assert.Empty(fields);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains("visitor", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_SignsIn_AndDerivesTheAccountsToken()
    {
        FakeSite site = new();
        FileMiragePipeline pipeline = site.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);

        // The token is what an upload actually needs; the password is never stored for the wire.
        Assert.Equal("FAKE-T0KE-N000-DEMO", result.ApiKey);

        // The display name off /user/settings, not the email that was typed.
        Assert.Equal("csuprobe", result.DerivedUsername);
        Assert.Equal("me@example.com", site.LastLoginForm!["email"]);
        Assert.Equal("csrf-from-the-login-page", site.LastLoginForm["_token"]);
    }

    [Fact]
    public async Task CheckAccountAsync_WrongPassword_IsRejected_EvenThoughTheHostStillRedirects()
    {
        // The trap this covers: a REJECTED sign-in is also a 302, just back to /login. Reading only
        // the status code marks a wrong password as a good account, and the upload that follows then
        // has no token — or worse, an old one.
        FakeSite site = new() { LoginSucceeds = false };
        FileMiragePipeline pipeline = site.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.ApiKey);
        Assert.Contains("check the email and password", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_SignedInButNoToken_IsNotAUsableAccount()
    {
        // An empty api_token is what an ANONYMOUS page carries. Accepting the account here would
        // store a credential that uploads every file as a visitor.
        FakeSite site = new() { HomepageToken = string.Empty };
        FileMiragePipeline pipeline = site.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("visitor", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_SurvivesASettingsPageItCannotRead()
    {
        // The name is a nicety. Losing it must not cost the user a working account.
        FakeSite site = new() { SettingsPageFails = true };
        FileMiragePipeline pipeline = site.Build();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "me@example.com", "pw", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("FAKE-T0KE-N000-DEMO", result.ApiKey);
        Assert.Equal("me@example.com", result.DerivedUsername);
    }

    [Theory]
    [InlineData("""<script> const api_token = "FAKE-T0KE-N000-DEMO"; const maxFileSize = 53687091200;</script>""", "FAKE-T0KE-N000-DEMO")]
    // The anonymous page's value. Reading it as a token would send `Bearer ` and upload as a visitor.
    [InlineData("""<script> const api_token = ""; const maxFileSize = 53687091200;</script>""", null)]
    [InlineData("<html>signed out</html>", null)]
    public void ReadApiToken_TreatsTheEmptyTokenAsNoToken(string body, string? expected)
        => Assert.Equal(expected, FileMiragePipeline.ReadApiToken(body));

    [Theory]
    // Both outcomes of a login POST are a 302; only the Location tells them apart.
    [InlineData("https://filemirage.com/login", true)]
    [InlineData("https://filemirage.com/login?error=1", true)]
    [InlineData("https://filemirage.com", false)]
    [InlineData("https://filemirage.com/", false)]
    [InlineData(null, false)]
    public void LooksLikeLoginPage_SeparatesARejectedSignInFromAnAcceptedOne(string? location, bool expected)
        => Assert.Equal(expected, FileMiragePipeline.LooksLikeLoginPage(location));

    [Fact]
    public void ParseChunkResponse_TheApisDocumentedFailureEnvelopeSaysResultNotSuccess()
    {
        // Its success envelope uses "success" but the documented failure envelope uses "result".
        // Reading only the first spelling turns a stated refusal into a silent success.
        (string? url, string? error) = FileMiragePipeline.ParseChunkResponse(
            new HttpResponseSnapshot(200, """{"result":false,"message":"Storage full"}""", Array.Empty<string>()),
            0,
            1);

        Assert.Null(url);
        Assert.Contains("Storage full", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadNode_AlsoTakesTheServerMintedUploadId()
    {
        (string? server, string? uploadId) = FileMiragePipeline.ReadNode(ServersJson);

        Assert.Equal("https://store1.filemirage.com", server);
        Assert.Equal("msk3g645d865", uploadId);
    }

    private static HttpHandler MakeHandler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    /// <summary>The pages the sign-in walks, in the shapes the live site returns them.</summary>
    private sealed class FakeSite
    {
        public bool LoginSucceeds { get; init; } = true;

        public bool SettingsPageFails { get; init; }

        public string HomepageToken { get; init; } = "FAKE-T0KE-N000-DEMO";

        public IReadOnlyDictionary<string, string>? LastLoginForm { get; private set; }

        public FileMiragePipeline Build() => new(Get, postFormOverride: PostForm);

        private Task<HttpResponseSnapshot> Get(string url)
        {
            if (url.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseSnapshot(
                    200,
                    """<form method="post"><input type="hidden" name="_token" value="csrf-from-the-login-page"><input name="email"></form>""",
                    ["XSRF-TOKEN=abc; path=/", "filemirage_session=def; path=/; httponly"]));
            }

            if (url.EndsWith("/user/settings", StringComparison.Ordinal))
            {
                return Task.FromResult(SettingsPageFails
                    ? new HttpResponseSnapshot(500, "boom", Array.Empty<string>())
                    : new HttpResponseSnapshot(
                        200,
                        """<input type="text" name="name" value="csuprobe"><input name="email" value="me@example.com">""",
                        Array.Empty<string>()));
            }

            // The homepage — every signed-in page carries the token in the same footer script.
            return Task.FromResult(new HttpResponseSnapshot(
                200,
                $$"""<a href="https://filemirage.com/logout">out</a><script> const api_token = "{{HomepageToken}}"; const maxFileSize = 53687091200;</script>""",
                Array.Empty<string>()));
        }

        private Task<HttpResponseSnapshot> PostForm(string url, IReadOnlyDictionary<string, string> form, IReadOnlyDictionary<string, string> headers)
        {
            _ = headers;
            LastLoginForm = new Dictionary<string, string>(form, StringComparer.Ordinal);

            // Both outcomes are a 302 — the Location is the only difference.
            return Task.FromResult(new HttpResponseSnapshot(
                302,
                string.Empty,
                Array.Empty<string>(),
                LocationHeader: LoginSucceeds ? "https://filemirage.com" : "https://filemirage.com/login"));
        }
    }

    private static FileMiragePipeline MakePipeline(
        List<string> gets,
        List<string> endpoints,
        List<Dictionary<string, string>> fields,
        Func<int, string> chunkBody) => new(
        url =>
        {
            gets.Add(url);
            return Task.FromResult(new HttpResponseSnapshot(200, ServersJson, Array.Empty<string>()));
        },
        (endpoint, sent, _, _) =>
        {
            endpoints.Add(endpoint);
            int index = int.Parse(sent["chunk_number"], System.Globalization.CultureInfo.InvariantCulture);
            fields.Add(new Dictionary<string, string>(sent, StringComparer.Ordinal));
            return Task.FromResult(new HttpResponseSnapshot(200, chunkBody(index), Array.Empty<string>()));
        });

    private AttemptContext MakeContext(long size) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = _file,
        FileName = "probe.rar",
        FileSize = size,
        HosterName = "FileMirage",
        Credentials = new FileHosterLoginDto { FileHosterName = "FileMirage", IsAnonymous = true },
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
