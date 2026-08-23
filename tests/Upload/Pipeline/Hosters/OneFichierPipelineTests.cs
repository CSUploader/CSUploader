// <copyright file="OneFichierPipelineTests.cs" company="CSUploader">
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
/// 1Fichier's anonymous upload: homepage scrape → multipart POST → follow the 302 to the result page
/// for the link. Fixtures are trimmed from the live site (probed 2026-07-29 with a real upload), and
/// both carry the decoys the real pages carry — a JS handler that also assigns an <c>action</c>, and a
/// removal link that is very nearly shaped like the download link.
/// </summary>
public class OneFichierPipelineTests
{
    // What the documented node lookup answers. The node host is bare (no scheme, no path).
    private const string NodeJson = """{"url":"up2.1fichier.com","id":"9kGjlUU2CV"}""";

    // The result page named by the 302. Note the removal link: same host, same id, one character of
    // difference in shape ("/remove/" vs "/?") — picking it would hand the user a link that DELETES
    // their upload.
    private const string EndHtml = """
        <!DOCTYPE html><html><body>
        <h1>Upload finished - Links</h1>
        <table><tr><th>Filename</th><th>Size</th><th>Download link</th><th>Removal link</th></tr>
        <tr><td>1mb.bin</td><td>1 MB</td>
          <td><a href="https://1fichier.com/?jxbpw7mo2qfc3ayoz701">https://1fichier.com/?jxbpw7mo2qfc3ayoz701</a></td>
          <td><a href="https://1fichier.com/remove/jxbpw7mo2qfc3ayoz701/sNSgWdqL">removal</a></td>
        </tr></table>
        <p>Warning ! This is a temporary page. Within some minutes, this page will not be accessible.</p>
        </body></html>
        """;

    private const string UploadAction = "https://up2.1fichier.com/upload.cgi?id=9kGjlUU2CV";

    // A refusal page, trimmed from the live response. The outcome sits in the first "bloc2" div; the
    // second is an empty layout box. This particular message is the NODE failing to open its own
    // storage target — seen once in fourteen parallel uploads.
    private const string DestinationErrorHtml = """
        <!DOCTYPE html><html><head><title>1fichier.com: Cloud Storage</title></head><body>
        <div id="header"><a href="https://1fichier.com" title="1fichier.com"><img id="logo" /></a></div>
        <div class="center-container2">
          <div class="bloc2">
            <span class="spacer spacer-20"></span>
                        Ne peut ouvrir le fichier destination
            <span class="spacer spacer-20"></span>
          </div>
          <div class="bloc2" style="width:750px;height:110px;margin:auto"></div>
        </div>
        </body></html>
        """;

    // The other refusal seen live — and the one that must NEVER be retried, because it meant OUR
    // request was malformed (the part-header order), not that their node had a bad moment.
    private const string NoFileFoundHtml = """
        <!DOCTYPE html><html><body><div class="center-container2">
          <div class="bloc2"><span class="spacer"></span>Pas de fichier trouv&eacute; dans l'envoi<span class="spacer"></span></div>
          <div class="bloc2" style="height:110px"></div>
        </div></body></html>
        """;

    [Fact]
    public async Task RunAsync_HappyPath_ResolvesTheNode_PostsToIt_ThenReadsTheLinkOffTheResultPage()
    {
        List<string> getUrls = [];
        OneFichierPipeline pipeline = MakePipeline(getUrls, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://1fichier.com/?jxbpw7mo2qfc3ayoz701", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The POST goes to the resolved node, query intact — the ?id= IS the upload session.
        UploadCall call = Assert.Single(calls);
        Assert.Equal(UploadAction, call.Endpoint);
        Assert.Empty(call.ExtraFields); // the live probe proved a bare file[] is accepted
        Assert.Equal("https://1fichier.com", call.Headers!["Origin"]);
        Assert.Equal("https://1fichier.com/", call.Headers["Referer"]);

        // Two calls: the API node lookup, then the result page — on the NODE host, not the apex.
        Assert.Equal(2, getUrls.Count);
        Assert.Equal("https://api.1fichier.com/v1/upload/get_upload_server.cgi", getUrls[0]);
        Assert.Equal("https://up2.1fichier.com/end.pl?xid=9kGjlUU2CV", getUrls[1]);
    }

    [Fact]
    public async Task RunAsync_ResultPage_TakesTheDownloadLink_NeverTheRemovalLink()
    {
        List<string> getUrls = [];
        OneFichierPipeline pipeline = MakePipeline(getUrls, out List<UploadCall> _);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        // Handing back the removal link would give the user a URL that deletes their own upload.
        string url = Assert.Single(events.OfType<TransferCompleted>()).FileUrl;
        Assert.DoesNotContain("/remove/", url, StringComparison.Ordinal);
        Assert.Contains("/?", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NoRedirect_FailsWithoutFetchingAResultPage()
    {
        // The link lives ONLY on the page the 302 names, so no Location means no link to find.
        List<string> getUrls = [];
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: url => { getUrls.Add(url); return new HttpResponseSnapshot(200, NodeJson, Array.Empty<string>()); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, "<html><body>please wait</body></html>", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("result page", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Single(getUrls); // node lookup only — nothing to follow
    }

    [Theory]
    [InlineData("""{"url":"up3.1fichier.com","id":"cprKQv815C"}""", "https://up3.1fichier.com/upload.cgi?id=cprKQv815C")]
    [InlineData("{\n   \"id\" : \"Z2G3wqUd3I\",\n   \"url\" : \"ru-3.1fichier.com\"\n}", "https://ru-3.1fichier.com/upload.cgi?id=Z2G3wqUd3I")] // pretty-printed, keys reversed
    [InlineData("""{"message":"Content-Type not JSON #24","status":"KO"}""", null)]                 // the refusal envelope
    [InlineData("""{"url":"up3.1fichier.com"}""", null)]                                            // no id
    [InlineData("""{"url":"evil.example.com/../x","id":"abc"}""", null)]                            // host must be bare
    [InlineData("<html>not json</html>", null)]
    public void TryReadUploadNode_BuildsThePostTargetOrRefuses(string json, string? expected)
        => Assert.Equal(expected, OneFichierPipeline.TryReadUploadNode(json));

    [Theory]
    [InlineData(DestinationErrorHtml, "Ne peut ouvrir le fichier destination")]
    [InlineData(NoFileFoundHtml, "Pas de fichier trouvé dans l'envoi")]  // HTML entity decoded
    [InlineData("<html><body><div class=\"bloc2\"></div><div class=\"bloc2\">Moved !</div></body></html>", "Moved !")]
    [InlineData("<html><body>no bloc at all</body></html>", null)]
    [InlineData("<html><body><div class=\"bloc2\"> <span></span> </div></body></html>", null)] // whitespace-only
    public void TryReadServerMessage_TakesTheFirstNonEmptyBloc(string html, string? expected)
        => Assert.Equal(expected, OneFichierPipeline.TryReadServerMessage(html));

    [Fact]
    public async Task RunAsync_NodeCannotOpenDestination_RetriesOnceAgainstAFreshNode()
    {
        // The live failure: 1 of 14 parallel uploads came back HTTP 200 with "Ne peut ouvrir le
        // fichier destination". That is the node fumbling its own storage, so a different node gets
        // one more go — and the node comes from the lookup, so the retry re-resolves.
        List<string> getUrls = [];
        List<string> endpoints = [];
        int uploads = 0;

        // Second lookup hands out a different node, proving the retry moves off the broken one.
        Queue<string> nodes = new([NodeJson, """{"url":"up9.1fichier.com","id":"ZZZZZZZZZZ"}"""]);
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: url =>
            {
                getUrls.Add(url);
                return url.Contains("end.pl", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(200, EndHtml, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, nodes.Dequeue(), Array.Empty<string>());
            },
            uploadOverride: (_, endpoint, _, _, _) =>
            {
                endpoints.Add(endpoint);
                return Task.FromResult(++uploads == 1
                    ? new HttpResponseSnapshot(200, DestinationErrorHtml, Array.Empty<string>())
                    : new HttpResponseSnapshot(302, "<html>please wait</html>", Array.Empty<string>(), "/end.pl?xid=ZZZZZZZZZZ"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Equal("https://1fichier.com/?jxbpw7mo2qfc3ayoz701", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal(2, uploads);
        Assert.StartsWith("https://up2.1fichier.com/", endpoints[0], StringComparison.Ordinal);
        Assert.StartsWith("https://up9.1fichier.com/", endpoints[1], StringComparison.Ordinal);

        // Two lookups (one per send) then the result page.
        Assert.Equal(3, getUrls.Count);
        Assert.EndsWith("get_upload_server.cgi", getUrls[0], StringComparison.Ordinal);
        Assert.EndsWith("get_upload_server.cgi", getUrls[1], StringComparison.Ordinal);
        Assert.Contains("end.pl", getUrls[2], StringComparison.Ordinal);

        Assert.Single(events.OfType<TransferStarted>()); // one transfer as far as the UI is concerned
    }

    [Fact]
    public async Task RunAsync_DestinationFailurePersists_StopsAfterExactlyOneRetry_AndQuotesTheNode()
    {
        int uploads = 0;
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, NodeJson, Array.Empty<string>()),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(200, DestinationErrorHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        // The node's own words, not a dump of its HTML.
        Assert.Contains("Ne peut ouvrir le fichier destination", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, uploads); // original + exactly one retry
    }

    [Fact]
    public async Task RunAsync_NoFileFoundInTheUpload_IsNeverRetried()
    {
        // This message means the request WE built was wrong (the part-header order bug), so re-sending
        // spends the whole file again to be told exactly the same thing.
        int uploads = 0;
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, NodeJson, Array.Empty<string>()),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(200, NoFileFoundHtml, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("Pas de fichier trouvé", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.Ordinal);
        Assert.Equal(1, uploads); // sent once, never again
    }

    [Fact]
    public async Task RunAsync_ResultPageMissingItsLink_DoesNotReUploadTheFile()
    {
        // The POST succeeded, so the file IS stored. Retrying here would leave the user a duplicate;
        // what's lost is the link, not the file.
        int uploads = 0;
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: url => url.Contains("end.pl", StringComparison.Ordinal)
                ? new HttpResponseSnapshot(200, "<html><body>temporary page expired</body></html>", Array.Empty<string>())
                : new HttpResponseSnapshot(200, NodeJson, Array.Empty<string>()),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploads++;
                return Task.FromResult(new HttpResponseSnapshot(302, string.Empty, Array.Empty<string>(), "/end.pl?xid=9kGjlUU2CV"));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Contains("no download link", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, uploads); // NOT re-sent — that would duplicate the stored file
    }

    [Fact]
    public async Task RunAsync_NodeLookupRefused_FailsBeforeSendingAnyBytes()
    {
        List<UploadCall> calls = [];
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(400, """{"message":"Content-Type not JSON #24","status":"KO"}""", Array.Empty<string>()),
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields), null));
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public async Task RunAsync_FileOverTheGuestCap_IsRejectedWithoutAnyHttp()
    {
        // 5 GB guest cap, stated by the homepage ("5GB for guests"). Reject before streaming, not
        // after — the whole point of knowing the cap.
        List<UploadCall> calls = [];
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: _ => throw new InvalidOperationException("must not fetch"),
            uploadOverride: (filePath, endpoint, fields, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(fields), null));
                return Task.FromResult(new HttpResponseSnapshot(200, string.Empty, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext((5L * 1000 * 1000 * 1000) + 1), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public void Properties_DeclareAnonymous1FichierWithTheGuestCap_AndItIsRegistered()
    {
        OneFichierPipeline pipeline = new();
        Assert.Equal("1Fichier", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(5L * 1000 * 1000 * 1000, pipeline.MaxFileSize);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);

        Assert.True(FileHosterClient.FileHosters.ContainsKey("1Fichier"));
        Assert.Equal("1fichier.com", FileHosterClient.FileHosters["1Fichier"]);

        // Anonymous-only for now: no account entry, so the editor keeps the plain U/P mode.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("1Fichier"));
    }

    [Fact]
    public async Task CheckAccountAsync_SaysAccountsArentSupportedYet()
    {
        OneFichierPipeline pipeline = new();
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "u", "p", null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Anonymous", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OneFichierPipeline MakePipeline(List<string> getUrls, out List<UploadCall> uploadCalls)
    {
        List<UploadCall> captured = [];
        uploadCalls = captured;

        return new OneFichierPipeline(
            getSnapshotOverride: url =>
            {
                getUrls.Add(url);
                return url.Contains("end.pl", StringComparison.Ordinal)
                    ? new HttpResponseSnapshot(200, EndHtml, Array.Empty<string>())
                    : new HttpResponseSnapshot(200, NodeJson, Array.Empty<string>());
            },
            uploadOverride: (filePath, endpoint, extraFields, headers, _) =>
            {
                captured.Add(new UploadCall(
                    filePath,
                    endpoint,
                    new Dictionary<string, string>(extraFields),
                    headers is null ? null : new Dictionary<string, string>(headers)));

                // Success is a 302 whose Location is RELATIVE to the node host.
                return Task.FromResult(new HttpResponseSnapshot(
                    302, "<html><body>please wait</body></html>", Array.Empty<string>(), "/end.pl?xid=9kGjlUU2CV"));
            });
    }

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    // Anonymous context: a blank login DTO, exactly what the wizard builds for an
    // anonymous-capable hoster with no selected account.
    private static AttemptContext MakeContext(long fileSize = 1_048_576L) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\1mb.bin",
        FileName = "1mb.bin",
        FileSize = fileSize,
        HosterName = "1Fichier",
        Credentials = new FileHosterLoginDto { FileHosterName = "1Fichier" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedBudget = SpeedBudget.Unlimited,
        Cancellation = default,
    };
}
