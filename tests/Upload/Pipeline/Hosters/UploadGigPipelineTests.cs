// <copyright file="UploadGigPipelineTests.cs" company="CSUploader">
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
/// UploadGIG — the host's own published two-call API. Fixtures are the real replies: the permission
/// call's envelope, its refusals (including the rate limit, which is not a bad password), and the
/// node's <c>{"0":{…}}</c> object that the docs' PHP indexes as if it were an array.
/// </summary>
public class UploadGigPipelineTests
{
    private const string ActionOkJson =
        """{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123","cmbs":10238}}""";

    private const string UploadOkJson =
        """{"0":{"ok":true,"message":"Uploaded was successful, file will be available in a few minutes","url":"https://uploadgig.com/file/download/891eB502750788f2/x.rar","id":"891eB502750788f2","file_name":"x.rar","size":2097152}}""";

    [Fact]
    public void UploadGig_IsAccountOnly_AndSerialised()
    {
        UploadGigPipeline pipeline = new();

        Assert.Equal("UploadGIG", pipeline.Name);

        // Its API authenticates with the username and password on every upload — there is no guest route.
        Assert.False(pipeline.SupportsAnonymousUpload);

        // 10 GB is the FREE tier's total storage, which is also the largest thing an empty account can
        // hold. The live remaining figure is what RunAsync actually enforces.
        Assert.Equal(10240L * 1024 * 1024, pipeline.MaxFileSize);

        // One at a time: every upload needs a 60-second address, and asking for one is a rate-limited
        // login. A package asking in parallel spends its allowance on the queue.
        Assert.Equal(1, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { IsAnonymous = false }));

        // No captcha on the API path, so no browser sign-in — despite the website's login having one.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("UploadGIG"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("UploadGIG"));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("UploadGIG"));

        // The apex, not www. — www.uploadgig.com now answers 404.
        Assert.Equal("uploadgig.com", FileHosterClient.FileHosters["UploadGIG"]);
    }

    [Fact]
    public async Task RunAsync_AsksForAnAddress_ThenPostsTheFileAsTheRawBody()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Form)> posts = [];
        List<(string Path, string Url, IReadOnlyDictionary<string, string>? Headers)> uploads = [];

        UploadGigPipeline pipeline = new(
            postFormOverride: (url, form) =>
            {
                posts.Add((url, new Dictionary<string, string>(form)));
                return Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>()));
            },
            uploadOverride: (path, url, headers, _) =>
            {
                uploads.Add((path, url, headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(
            "https://uploadgig.com/file/download/891eB502750788f2/x.rar",
            Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string postUrl, IReadOnlyDictionary<string, string> form) = Assert.Single(posts);
        Assert.Equal("https://uploadgig.com/api/get_upload_action", postUrl);
        Assert.Equal("csuprobe@example.test", form["user"]);
        Assert.Equal("hunter2", form["pass"]);

        (string path, string url, IReadOnlyDictionary<string, string>? headers) = Assert.Single(uploads);
        Assert.Equal(@"C:\nope\x.rar", path);

        // The bytes go where the permission call said — plain HTTP on a bare IP, which is the host's
        // own design and must not be "corrected" to https.
        Assert.Equal("http://45.133.250.4:81/upload?identity=abc123", url);

        // The filename travels ONLY in this header; the body is the bytes and nothing else.
        Assert.Equal("x.rar", Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(headers)["Slug"]);
    }

    [Fact]
    public async Task RunAsync_TheRateLimitIsReportedInTheHostsWords_NotAsABadPassword()
    {
        // Asking for an address IS a login, and too many in a short window earn this. Reporting it as
        // "check the username and password" would send the user to change a password that is fine.
        bool uploaded = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":"-3","result":"According to security reasons, you can't login a few minutes."}""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed failure = Assert.Single(events.OfType<AttemptFailed>());

        // The host's sentence, passed through as its own — not a bad-password verdict, and not the raw
        // envelope dumped into the grid because the code was never read.
        Assert.Equal("UploadGIG: According to security reasons, you can't login a few minutes.", failure.Reason);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task RunAsync_AWrongPasswordIsReported_AndNothingIsSent()
    {
        bool uploaded = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":"0","result":"Email or password was incorrect!"}""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("UploadGIG: Email or password was incorrect!", Assert.Single(events.OfType<AttemptFailed>()).Reason);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task RunAsync_AFileBiggerThanWhatIsLeft_IsRefusedBeforeAnyBytes()
    {
        // cmbs is what the account has ROOM for, not a per-file rule — so the check is against the live
        // figure, and it happens before the transfer rather than after 10 GB of it.
        bool uploaded = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123","cmbs":100}}""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = 200L * 1024 * 1024 };

        AttemptFailed failure = Assert.Single(await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None)), e => e is AttemptFailed) as AttemptFailed
            ?? throw new InvalidOperationException("expected a failure");

        Assert.Contains("100", failure.Reason, StringComparison.Ordinal);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task RunAsync_AFileThatFitsExactly_IsSent()
    {
        // The boundary the other way: cmbs is a limit to respect, not a margin to pad.
        bool uploaded = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123","cmbs":100}}""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = 100L * 1024 * 1024 };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.True(uploaded);
    }

    [Theory]
    [InlineData("x.rar", "x.rar")]
    [InlineData("My Movie (2026) [1080p].mkv", "My Movie (2026) [1080p].mkv")]
    [InlineData("тест.rar", "%D1%82%D0%B5%D1%81%D1%82.rar")]
    [InlineData("café ünï.rar", "caf%C3%A9 %C3%BCn%C3%AF.rar")]
    [InlineData("100%.rar", "100%25.rar")]
    public void EncodeSlug_LeavesAsciiAlone_AndEncodesWhatAHeaderCannotCarry(string fileName, string expected)
    {
        // Not cosmetic: .NET throws "Request headers must contain only ASCII characters" on a non-ASCII
        // header value, and that throw lands inside the body-send where the retry layer replays the
        // whole upload three times — three more rate-limited logins, and a transport message naming
        // the wrong problem. An ASCII name must stay byte-identical to what the browser sends.
        Assert.Equal(expected, UploadGigPipeline.EncodeSlug(fileName));
    }

    [Fact]
    public async Task RunAsync_ANonAsciiFileName_IsSentEncoded_NotThrown()
    {
        List<IReadOnlyDictionary<string, string>?> headers = [];
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, h, _) =>
            {
                headers.Add(h is null ? null : new Dictionary<string, string>(h));
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileName = "тест.rar" };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("%D1%82%D0%B5%D1%81%D1%82.rar", Assert.Single(headers)!["Slug"]);
    }

    [Theory]
    [InlineData("""{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123"}}""")]
    [InlineData("""{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123","cmbs":true}}""")]
    [InlineData("""{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc123","cmbs":"10240"}}""")]
    public async Task RunAsync_WithNoUsableCmbs_StillUploads(string actionJson)
    {
        // If the field moves, disappears, or arrives quoted (this API already quotes `code`), refusing
        // every upload would be the app inventing a limit the host never stated. Let the node answer.
        // The non-numeric case also guards a throw: GetString() on a JSON number is an exception.
        bool uploaded = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                actionJson,
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = 9L * 1024 * 1024 * 1024 };

        await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.True(uploaded);
    }

    [Theory]
    [InlineData("""{"0":{"ok":false,"message":"Your storage is full"}}""", "storage is full")]
    [InlineData("""[{"ok":false,"message":"Your storage is full"}]""", "storage is full")]
    public async Task RunAsync_ANodeRefusal_IsReported(string body, string expected)
    {
        // The docs' PHP does json_decode(...)[0], which in PHP reads BOTH an object keyed "0" and a
        // real array. .NET does not, and the live reply is the object — so both are accepted here.
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(200, body, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains(expected, Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_AReplyWithNoOkField_IsNotTakenAsSuccess()
    {
        // ok is what the docs say carries the verdict. If it goes missing, the honest reading is "this
        // host said something we don't understand", not "it worked" — treating absence as consent is
        // exactly how a discarded upload gets reported as a link.
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"0":{"message":"queued","url":"https://uploadgig.com/file/download/zzz/x.rar"}}""",
                Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.NotEmpty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_SuccessWithoutALink_IsAFailure_NotASilentWin()
    {
        // The file would be up and unreachable. Reporting success the user can't act on is worse than
        // reporting the truth.
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"0":{"ok":true,"message":"Uploaded was successful"}}""", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.NotEmpty(events.OfType<AttemptFailed>());
    }

    [Fact]
    public async Task RunAsync_WithNoPassword_FailsWithoutTouchingTheNetwork()
    {
        bool touched = false;
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>()));
            },
            uploadOverride: (_, _, _, _) =>
            {
                touched = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadOkJson, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext();
        ctx.Credentials.Password = null;

        Assert.Single(await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None)), e => e is AttemptFailed);
        Assert.False(touched);
    }

    [Fact]
    public async Task CheckAccount_ReportsStorageFromTheSameCallThatProvesThePassword()
    {
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(200, ActionOkJson, Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("no upload during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("csuprobe@example.test", result.DerivedUsername);

        // 10240 total, 10238 left → 2 MB used. The figure came out of the permission call, so the grid
        // gets usage without a second endpoint (there isn't one).
        Assert.Equal(10240L * 1024 * 1024, result.StorageQuotaBytes);
        Assert.Equal(2L * 1024 * 1024, result.StorageUsedBytes);
    }

    [Fact]
    public async Task CheckAccount_MoreRoomThanTheFreeTier_ReportsNoQuota()
    {
        // Free is 10 GB. An account with more than that is paid, and subtracting from 10 GB would
        // report a negative "used" — better to report no figures than invented ones.
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200,
                """{"code":"1","result":{"action":"http://45.133.250.4:81/upload?identity=abc","cmbs":51200}}""",
                Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("no upload during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "hunter2", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
        Assert.Null(result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccount_ARefusalIsNotValid_AndKeepsTheHostsWording()
    {
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"code":"0","result":"Email or password was incorrect!"}""", Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("no upload during a check"));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "csuprobe@example.test", "wrong", null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("incorrect", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ANonJsonReply_IsReportedRatherThanTakenAsSuccess()
    {
        // A DDoS-Guard interstitial or an nginx error page arrives with a 200 as often as not.
        UploadGigPipeline pipeline = new(
            postFormOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, "<html><body>Checking your browser…</body></html>", Array.Empty<string>())),
            uploadOverride: (_, _, _, _) => throw new InvalidOperationException("no upload after a failed sign-in"));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.NotEmpty(events.OfType<AttemptFailed>());
    }

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

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.rar",
        FileName = "x.rar",
        FileSize = 2L * 1024 * 1024,
        HosterName = "UploadGIG",
        Credentials = new FileHosterLoginDto
        {
            Id = 5,
            FileHosterName = "UploadGIG",
            IsAnonymous = false,
            Username = "csuprobe@example.test",
            Password = "hunter2",
            PinnedProxyId = null,
        },
        Proxy = ProxyChoice.Direct,
        Handler = MakeHandler(),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
