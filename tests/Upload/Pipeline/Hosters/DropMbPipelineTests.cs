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
/// DropMB — a Pingvin Share instance, account-only since the host switched anonymous upload off
/// (2026-10-24: an anonymous <c>POST /api/shares</c> answers 403 "Forbidden resource"). Fixtures are the
/// real replies its API gave (2026-08-09), verified by uploading at one chunk, across the 10 MB chunk
/// boundary, and signed in — the last of which came back with <c>"creator":{"username":…}</c>, which is
/// how attribution is checked here. Every upload in this class is therefore signed in, and anonymous
/// ones are pinned as refused before any request. The chunk-id rule is pinned first: it is the one thing
/// a single-chunk capture of a real upload could not reveal.
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
    public async Task RunAsync_Anonymous_IsRefusedBeforeAnyRequestIsMade()
    {
        // The host answers an anonymous create-share with a bare 403 "Forbidden resource" (its
        // security.allowUnauthenticatedShares is false), so a job still queued as Anonymous can only
        // fail. Sending it would cost a request to learn nothing; the message has to say why instead.
        List<string> posts = [];
        List<string> endpoints = [];
        DropMbPipeline pipeline = new(
            (url, _, _) => { posts.Add(url); return Task.FromResult(new HttpResponseSnapshot(201, ShareCreated, Array.Empty<string>())); },
            (endpoint, _, _) => { endpoints.Add(endpoint); return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())); });

        AttemptContext ctx = MakeContext(4096) with
        {
            Credentials = new FileHosterLoginDto { FileHosterName = "DropMB", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(posts);
        Assert.Empty(endpoints);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Contains("anonymous", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_AHostThatRefusesTheShare_ReportsItsStatusAndBody()
    {
        // The exact reply from the field report (2026-10-21). A signed-in attempt can meet it too — the
        // host's guard answers any unusable token with this same body — so it has to reach the user whole.
        List<string> endpoints = [];
        DropMbPipeline pipeline = new(
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                403, """{"message":"Forbidden resource","error":"Forbidden","statusCode":403}""", Array.Empty<string>())),
            (endpoint, _, _) => { endpoints.Add(endpoint); return Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())); });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(endpoints);
        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("HTTP 403", reason, StringComparison.Ordinal);
        Assert.Contains("Forbidden resource", reason, StringComparison.Ordinal);
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

    // ── The token is a 15-minute JWT: a refused call signs in again ──

    private const string StaleCookie = "access_token=jwt-token-value";
    private const string FreshCookie = "access_token=fresh-jwt";

    private sealed record Call(string Url, string? Cookie, string? Json = null);

    private static string? CookieOf(IReadOnlyDictionary<string, string>? headers)
        => headers is not null && headers.TryGetValue("Cookie", out string? cookie) ? cookie : null;

    private static HttpResponseSnapshot Forbidden() => new(
        403, """{"message":"Forbidden resource","error":"Forbidden","statusCode":403}""", Array.Empty<string>());

    private static HttpResponseSnapshot SignedIn(string token) => new(
        200, $$"""{"accessToken":"{{token}}"}""", [$"access_token={token}; Path=/; HttpOnly"]);

    /// <summary>A host whose only valid token is <see cref="FreshCookie"/>: every other cookie is refused
    /// with the field report's 403, and signing in hands the valid one out.</summary>
    private static DropMbPipeline LapsedTokenPipeline(List<Call> posts, List<Call> chunks) => new(
        (url, json, headers) =>
        {
            string? cookie = CookieOf(headers);
            lock (posts)
            {
                posts.Add(new Call(url, cookie, json));
            }

            if (url.EndsWith("/signIn", StringComparison.Ordinal))
            {
                return Task.FromResult(SignedIn("fresh-jwt"));
            }

            return Task.FromResult(cookie == FreshCookie
                ? new HttpResponseSnapshot(
                    url.EndsWith("/complete", StringComparison.Ordinal) ? 202 : 201, ShareCreated, Array.Empty<string>())
                : Forbidden());
        },
        (endpoint, headers, _) =>
        {
            string? cookie = CookieOf(headers);
            lock (chunks)
            {
                chunks.Add(new Call(endpoint, cookie));
            }

            return Task.FromResult(cookie == FreshCookie
                ? new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())
                : Forbidden());
        });

    [Fact]
    public async Task RunAsync_AStoredTokenThatHasLapsed_SignsInAgainAndCarriesOn()
    {
        // THE field report, signed-in edition. The cookie the account stored is a 15-minute JWT, so by the
        // time a queued file runs it is dead — and a dead token is refused exactly like none at all.
        List<Call> posts = [];
        List<Call> chunks = [];
        DropMbPipeline pipeline = LapsedTokenPipeline(posts, chunks);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Single(posts, p => p.Url.EndsWith("/signIn", StringComparison.Ordinal));

        // Create-share was tried with the stored token, refused, and repeated with the fresh one; the
        // chunk and the completion then used the fresh one straight away.
        Assert.Equal(
            [StaleCookie, FreshCookie],
            posts.Where(p => p.Url.EndsWith("/api/shares", StringComparison.Ordinal)).Select(p => p.Cookie));
        Assert.Equal(FreshCookie, Assert.Single(chunks).Cookie);
        Assert.Equal(FreshCookie, Assert.Single(posts, p => p.Url.EndsWith("/complete", StringComparison.Ordinal)).Cookie);
    }

    [Fact]
    public async Task RunAsync_ASecondFile_ReusesTheTokenTheFirstSignedInFor()
    {
        // Sign-in is throttled (20 per 5 minutes) and a batch is many files, so one sign-in has to serve
        // every attempt that follows rather than one per file.
        List<Call> posts = [];
        DropMbPipeline pipeline = LapsedTokenPipeline(posts, []);

        await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));
        List<UploadEvent> second = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Empty(second.OfType<AttemptFailed>());
        Assert.Single(posts, p => p.Url.EndsWith("/signIn", StringComparison.Ordinal));

        // The second file's first create-share already carried the fresh token: no refusal to recover from.
        Call[] creates = [.. posts.Where(p => p.Url.EndsWith("/api/shares", StringComparison.Ordinal))];
        Assert.Equal(FreshCookie, creates[^1].Cookie);
        Assert.Equal(3, creates.Length);
    }

    [Fact]
    public async Task RunAsync_TwoFilesAtOnce_ShareASingleSignIn()
    {
        List<Call> posts = [];
        DropMbPipeline pipeline = LapsedTokenPipeline(posts, []);

        List<UploadEvent>[] both = await Task.WhenAll(
            DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None)),
            DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None)));

        Assert.All(both, events => Assert.Single(events.OfType<TransferCompleted>()));
        Assert.Single(posts, p => p.Url.EndsWith("/signIn", StringComparison.Ordinal));
    }


    [Fact]
    public async Task RunAsync_ATokenThatLapsesPartWayThrough_ResendsThatChunkWithTheSameFileId()
    {
        // A 15-minute token outlasts neither a big file nor a slow link. The refused chunk has to go again
        // as the SAME slice (same index, same file id) under the new token, and the rest follow on it.
        List<Call> chunks = [];
        int signIns = 0;
        DropMbPipeline pipeline = new(
            (url, _, _) =>
            {
                if (url.EndsWith("/signIn", StringComparison.Ordinal))
                {
                    signIns++;
                    return Task.FromResult(SignedIn("tok-2"));
                }

                return Task.FromResult(new HttpResponseSnapshot(
                    url.EndsWith("/complete", StringComparison.Ordinal) ? 202 : 201, ShareCreated, Array.Empty<string>()));
            },
            (endpoint, headers, _) =>
            {
                string? cookie = CookieOf(headers);
                chunks.Add(new Call(endpoint, cookie));
                bool lapsed = Between(endpoint, "chunkIndex=", "&") == "1" && cookie == "access_token=tok-1";
                return Task.FromResult(lapsed ? Forbidden() : new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext((2L * 10_000_000) + 5_000_000) with
        {
            Credentials = new FileHosterLoginDto
            {
                Id = 4, FileHosterName = "DropMB", IsAnonymous = false,
                Username = "someone", Password = "pw", SessionCookie = "tok-1",
            },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal(1, signIns);
        Assert.Equal(
            [("0", "access_token=tok-1"), ("1", "access_token=tok-1"), ("1", "access_token=tok-2"), ("2", "access_token=tok-2")],
            chunks.Select(c => (Between(c.Url, "chunkIndex=", "&"), c.Cookie!)));

        // Both sends of chunk 1 name the file chunk 0 was given.
        Assert.All(
            chunks.Where(c => Between(c.Url, "chunkIndex=", "&") == "1"),
            c => Assert.Contains("&id=90360eff-ebad-497b-848a-19dd6e6afa67", c.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ARefusalThatSurvivesASignIn_IsNotRetriedForever()
    {
        // Signing in again is the cure for a lapsed token, not for a request the host simply won't take
        // (an account switched off, a share quota). Once is enough to find out.
        List<Call> posts = [];
        DropMbPipeline pipeline = new(
            (url, json, headers) =>
            {
                posts.Add(new Call(url, CookieOf(headers), json));
                return Task.FromResult(url.EndsWith("/signIn", StringComparison.Ordinal) ? SignedIn("fresh-jwt") : Forbidden());
            },
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        Assert.Single(posts, p => p.Url.EndsWith("/signIn", StringComparison.Ordinal));
        Assert.Equal(2, posts.Count(p => p.Url.EndsWith("/api/shares", StringComparison.Ordinal)));
        Assert.Contains("HTTP 403", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheSignInItselfIsRefused_ReportsTheOriginalRefusal()
    {
        // The request that failed is the one worth describing — not a sign-in the user never asked for.
        DropMbPipeline pipeline = new(
            (url, _, _) => Task.FromResult(url.EndsWith("/signIn", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(401, """{"message":"Wrong password"}""", Array.Empty<string>())
                : Forbidden()),
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(4096), CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("HTTP 403", reason, StringComparison.Ordinal);
        Assert.Contains("Forbidden resource", reason, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("someone", "username")]
    [InlineData("someone@example.com", "email")]
    [InlineData("  someone@example.com ", "email")]
    public async Task CheckAccountAsync_SignsInByEmailWhenTheNameHasAnAt_AsTheSitesOwnFormDoes(string typed, string field)
    {
        // Its sign-in form posts {email} for anything containing "@" and {username} otherwise, and the host
        // matches the account by the field it is given — an email sent as a username matches nobody.
        List<string> bodies = [];
        DropMbPipeline pipeline = new(
            (_, json, _) =>
            {
                bodies.Add(json!);
                return Task.FromResult(SignedIn("fresh-jwt"));
            },
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            typed, "pw", null, MakeContext(1).Handler, ProxyChoice.Direct, CancellationToken.None);

        JsonElement posted = JsonDocument.Parse(Assert.Single(bodies)).RootElement;
        Assert.Equal(typed.Trim(), posted.GetProperty(field).GetString());
        Assert.Equal("pw", posted.GetProperty("password").GetString());
        Assert.Equal(2, posted.EnumerateObject().Count());

        Assert.True(result.IsValid);
        Assert.Equal("fresh-jwt", result.SessionCookie);
    }

    [Fact]
    public async Task CheckAccountAsync_AnAccountWithTwoFactorOn_SaysSoInsteadOfBlamingTheShare()
    {
        // With TOTP enabled the sign-in answers 200 with a one-time {loginToken} and no access token.
        DropMbPipeline pipeline = new(
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(
                200, """{"loginToken":"one-time"}""", Array.Empty<string>())),
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "someone", "pw", null, MakeContext(1).Handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("two-factor", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_StatesNoExpiry_SoTheAppNeverSwitchesAGoodAccountOffForIt()
    {
        // The token really lives 15 minutes, but an expiry on an account makes the app disable it at the
        // next start (HasExpiredSession) — over a token the upload re-mints on demand anyway.
        DropMbPipeline pipeline = new(
            (_, _, _) => Task.FromResult(SignedIn("fresh-jwt")),
            (_, _, _) => Task.FromResult(new HttpResponseSnapshot(201, ChunkAccepted, Array.Empty<string>())));

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "someone", "pw", null, MakeContext(1).Handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.SessionCookieExpiresUtc);
        Assert.False(new FileHosterLoginDto
        {
            FileHosterName = "DropMB",
            SessionCookie = result.SessionCookie,
            SessionCookieExpiresUtc = result.SessionCookieExpiresUtc,
        }.HasExpiredSession);
    }

    [Fact]
    public void DropMB_IsNotSessionRefreshable_BecauseTheStoredTokenIsDeadWithinAQuarterHour()
    {
        // AccountVerifier routes a check with a stored cookie to ISessionRefreshablePipeline when there is
        // one, and hands it NO password. A 15-minute token would fail that check every time and the app
        // would switch a working account off; without the interface the check signs in with the password.
        object pipeline = new DropMbPipeline();

        Assert.False(pipeline is ISessionRefreshablePipeline);
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
    public void DropMB_IsAccountOnly_AtTheCapItPublishes()
    {
        DropMbPipeline pipeline = new();
        Assert.Equal("DropMB", pipeline.Name);

        // Off since 2026-10-24. If the host ever re-opens it (security.allowUnauthenticatedShares in its
        // /api/configs), this is the line to flip — after an anonymous POST /api/shares answers 201.
        Assert.False(pipeline.SupportsAnonymousUpload);
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

        // Signed in, with a stored token so no sign-in call is made: anonymous is refused outright.
        Credentials = new FileHosterLoginDto
        {
            Id = 4, FileHosterName = "DropMB", IsAnonymous = false,
            Username = "someone", Password = "pw", SessionCookie = "jwt-token-value",
        },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
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
