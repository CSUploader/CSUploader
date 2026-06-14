// <copyright file="XFileSharingApiPipelineRedirectFollowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pins the static redirect-follow helper that drives XFileSharingApiPipeline's
/// my_account fetches. Root cause this guards against: ex-load.com responds to
/// <c>GET /?op=my_account</c> with a 302 to a near-empty stub when the captured xfss
/// cookie arrives without companion cookies. The global HttpHandler runs with
/// AllowAutoRedirect=false (BRupload's login branches on 302), so without manual
/// redirect-follow CheckAccountAsync reads the stub instead of the logged-in HTML and
/// reports a confusing "no api-url OR CSRF token" error.
/// </summary>
public class XFileSharingApiPipelineRedirectFollowTests
{
    [Fact]
    public async Task FetchFollowingRedirectsAsync_StraightThrough200_ReturnsBodyHopsZero()
    {
        // No redirect — happy path.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(200, "<html>logged in</html>", Array.Empty<string>(), LocationHeader: null),
        });

        (string body, string finalUrl, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/?op=my_account",
            headers: null,
            get: (_, _, _) => Task.FromResult(responses.Dequeue()),
            ct: CancellationToken.None);

        Assert.Equal("<html>logged in</html>", body);
        Assert.Equal("https://ex-load.com/?op=my_account", finalUrl);
        Assert.Equal(0, hops);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_302WithRelativeLocation_ResolvesAgainstCurrent()
    {
        // The XFS family often emits "Location: /?op=login" (no scheme/host) — must be
        // resolved against the previous absolute URL or the second hop blows up.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, "<html>moved</html>", Array.Empty<string>(), LocationHeader: "/?op=my_account&_=1"),
            new HttpResponseSnapshot(200, "<html>logged in</html>", Array.Empty<string>(), LocationHeader: null),
        });
        List<string> requestedUrls = [];

        (string body, string finalUrl, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/?op=my_account",
            headers: null,
            get: (url, _, _) => { requestedUrls.Add(url); return Task.FromResult(responses.Dequeue()); },
            ct: CancellationToken.None);

        Assert.Equal("<html>logged in</html>", body);
        Assert.Equal("https://ex-load.com/?op=my_account&_=1", finalUrl);
        Assert.Equal(1, hops);
        Assert.Equal(2, requestedUrls.Count);
        Assert.Equal("https://ex-load.com/?op=my_account", requestedUrls[0]);
        Assert.Equal("https://ex-load.com/?op=my_account&_=1", requestedUrls[1]);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_AbsoluteLocation_FollowsToExternalHost()
    {
        // Some XFS forks redirect to an absolute URL (e.g. a CDN edge). We must honour
        // the absolute target verbatim, not concatenate it onto the original origin.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty, Array.Empty<string>(), LocationHeader: "https://edge.example.com/redirected"),
            new HttpResponseSnapshot(200, "<html>edge</html>", Array.Empty<string>(), LocationHeader: null),
        });

        (string body, string finalUrl, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/start",
            headers: null,
            get: (_, _, _) => Task.FromResult(responses.Dequeue()),
            ct: CancellationToken.None);

        Assert.Equal("<html>edge</html>", body);
        Assert.Equal("https://edge.example.com/redirected", finalUrl);
        Assert.Equal(1, hops);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_RedirectWithoutLocation_StopsAndReturnsBody()
    {
        // 302 without a Location header — treat as a real response (don't loop). The
        // caller's extractor will then fail to find api-url and surface the diagnostic.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, "<html>stub</html>", Array.Empty<string>(), LocationHeader: null),
        });

        (string body, _, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/x",
            headers: null,
            get: (_, _, _) => Task.FromResult(responses.Dequeue()),
            ct: CancellationToken.None);

        Assert.Equal("<html>stub</html>", body);
        Assert.Equal(0, hops);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_HopBudgetExhausted_ReturnsLastBodyAndMaxHops()
    {
        // Defensive — if a hoster ends up bouncing us between two URLs the helper must
        // not loop forever. Default budget is 5; we feed 6 redirects to prove the 5th
        // bails out without making the 6th call.
        Queue<HttpResponseSnapshot> responses = new();
        for (int i = 0; i < 6; i++)
        {
            responses.Enqueue(new HttpResponseSnapshot(302, $"<html>hop-{i}</html>", Array.Empty<string>(), LocationHeader: "/again"));
        }

        (string body, _, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/start",
            headers: null,
            get: (_, _, _) => Task.FromResult(responses.Dequeue()),
            ct: CancellationToken.None);

        Assert.Equal(5, hops);
        Assert.Equal("<html>hop-4</html>", body); // last response we received before budget exhausted (5 calls, indices 0..4)
        Assert.Single(responses); // one response left, NEVER consumed (helper stopped on time)
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_302SetsCookie_MergesItIntoFollowUpRequest()
    {
        // The exact ex-load.com behaviour from the Fiddler capture: GET /?op=my_account
        // with only xfss returns 302 + Set-Cookie: lang=english and redirects to the SAME
        // URL. The server then expects `lang` echoed on the follow-up. We must merge the
        // Set-Cookie into the Cookie header for the next hop (browser parity), otherwise
        // the server keeps serving a degraded page with no api-url.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, "<html>setting lang</html>",
                new[] { "lang=english; domain=.ex-load.com; path=/" },
                LocationHeader: "https://ex-load.com/?op=my_account"),
            new HttpResponseSnapshot(200, "<html><input name=\"api-url\" value=\"...key=abc\"></html>",
                Array.Empty<string>(), LocationHeader: null),
        });
        List<IReadOnlyDictionary<string, string>?> sentHeaders = [];

        (string body, _, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/?op=my_account",
            headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["Cookie"] = "xfss=SESSION123" },
            get: (_, headers, _) => { sentHeaders.Add(headers); return Task.FromResult(responses.Dequeue()); },
            ct: CancellationToken.None);

        Assert.Equal(1, hops);
        Assert.Contains("api-url", body, StringComparison.Ordinal);

        // Hop 1 sent only the original xfss cookie.
        Assert.Equal("xfss=SESSION123", sentHeaders[0]!["Cookie"]);
        // Hop 2 (the follow-up) merged the 302's lang cookie alongside xfss — the fix.
        string followUpCookie = sentHeaders[1]!["Cookie"];
        Assert.Contains("xfss=SESSION123", followUpCookie, StringComparison.Ordinal);
        Assert.Contains("lang=english", followUpCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_PreservesNonCookieHeadersAcrossHops()
    {
        // The cookie-merge must NOT drop other headers (Origin, etc.) the caller set.
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, string.Empty,
                new[] { "lang=english; path=/" }, LocationHeader: "https://ex-load.com/?op=my_account"),
            new HttpResponseSnapshot(200, "<html>ok</html>", Array.Empty<string>(), LocationHeader: null),
        });
        List<IReadOnlyDictionary<string, string>?> sentHeaders = [];

        await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/?op=my_account",
            headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Cookie"] = "xfss=S",
                ["Origin"] = "https://ex-load.com",
            },
            get: (_, headers, _) => { sentHeaders.Add(headers); return Task.FromResult(responses.Dequeue()); },
            ct: CancellationToken.None);

        Assert.Equal("https://ex-load.com", sentHeaders[1]!["Origin"]);
        Assert.Contains("lang=english", sentHeaders[1]!["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchFollowingRedirectsAsync_LeakedHeadersStub_IsTreatedAs200()
    {
        // The actual failure mode that triggered this work: ex-load.com's 302 stub body
        // contains literal "Set-Cookie:/Expires:/Date:/Content-Type:" lines leaked from a
        // PHP "headers after output" warning, followed by an empty <!doctype html><html>.
        // When we DO get a Location header back, the helper follows it. When we DON'T,
        // the helper returns the stub body and the diagnostic message points the user at
        // the (correct) "the sign-in may not have worked" reason.
        const string LeakedStub = "Set-Cookie: lang=english; domain=ex-load.com; path=/\nExpires: Mon, 08 Jun 2026 22:32:20 GMT\n<!doctype html><html><body></body></html>";
        Queue<HttpResponseSnapshot> responses = new(new[]
        {
            new HttpResponseSnapshot(302, LeakedStub, Array.Empty<string>(), LocationHeader: "/?op=my_account&_=1"),
            new HttpResponseSnapshot(200, "<html><input name=\"api-url\" value=\"https://ex-load.com/api/account/info?key=abc\"></html>", Array.Empty<string>(), LocationHeader: null),
        });

        (string body, _, int hops) = await XFileSharingApiPipeline.FetchFollowingRedirectsAsync(
            "https://ex-load.com/?op=my_account",
            headers: null,
            get: (_, _, _) => Task.FromResult(responses.Dequeue()),
            ct: CancellationToken.None);

        Assert.Equal(1, hops);
        Assert.Contains("api-url", body, StringComparison.Ordinal); // we actually reached the logged-in page
    }
}
