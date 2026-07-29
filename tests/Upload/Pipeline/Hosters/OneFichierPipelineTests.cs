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
    // The homepage. The JS block is real and matters: it re-assigns the form action for the
    // send_ssl toggle, so the page mentions the same upload.cgi URL twice more (once over plain
    // http) in a form the action scrape must NOT pick up.
    private const string HomeHtml = """
        <!DOCTYPE html><html><body>
        <script>
        $('input[name=send_ssl]').change( function() {
          if( $('input[name=send_ssl]').is(':checked') ) $("#files").prop('action', 'https://up2.1fichier.com/upload.cgi?id=9kGjlUU2CV');
          else $("#files").prop('action', 'http://up2.1fichier.com/upload.cgi?id=9kGjlUU2CV');
          return false; });
        </script>
        <form enctype="multipart/form-data" id="files" action="https://up2.1fichier.com/upload.cgi?id=9kGjlUU2CV" method="post" autocomplete="off">
          <input type="file" name="file[]" title="Select the file to upload" multiple="multiple" />
          <input type="checkbox" name="send_ssl" checked="checked" />
          <input type="password" name="dpass" value="" />
          <input type="text" name="user" value="" />
          <input id="sub" type="submit" name="submit" value="Send" />
        </form>
        </body></html>
        """;

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

    [Fact]
    public async Task RunAsync_HappyPath_ScrapesTheNode_PostsToIt_ThenReadsTheLinkOffTheResultPage()
    {
        List<string> getUrls = [];
        OneFichierPipeline pipeline = MakePipeline(getUrls, out List<UploadCall> calls);

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://1fichier.com/?jxbpw7mo2qfc3ayoz701", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The POST goes to the scraped node, query intact — the ?id= IS the upload session.
        UploadCall call = Assert.Single(calls);
        Assert.Equal(UploadAction, call.Endpoint);
        Assert.Empty(call.ExtraFields); // the live probe proved a bare file[] is accepted
        Assert.Equal("https://1fichier.com", call.Headers!["Origin"]);
        Assert.Equal("https://1fichier.com/", call.Headers["Referer"]);

        // Two GETs: the homepage, then the result page — on the NODE host, not the apex.
        Assert.Equal(2, getUrls.Count);
        Assert.Equal("https://1fichier.com/", getUrls[0]);
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
            getSnapshotOverride: url => { getUrls.Add(url); return new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>()); },
            uploadOverride: (_, _, _, _, _) => Task.FromResult(
                new HttpResponseSnapshot(200, "<html><body>please wait</body></html>", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("result page", fail.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events.OfType<TransferCompleted>());
        Assert.Single(getUrls); // homepage only — nothing to follow
    }

    [Fact]
    public async Task RunAsync_HomepageWithoutTheForm_FailsBeforeSendingAnyBytes()
    {
        List<UploadCall> calls = [];
        OneFichierPipeline pipeline = new(
            getSnapshotOverride: _ => new HttpResponseSnapshot(200, "<html><body>maintenance</body></html>", Array.Empty<string>()),
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
                    : new HttpResponseSnapshot(200, HomeHtml, Array.Empty<string>());
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
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
