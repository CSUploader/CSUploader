// <copyright file="SharemodsPipelineTests.cs" company="CSUploader">
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
/// ShareMods — the family's anonymous path, which by August 2026 almost nothing else still offers.
/// Fixtures are trimmed from the live homepage. The one thing worth guarding is the endpoint: the
/// page's only <c>upload.cgi</c> action imports LINKS, and posting a file to it would look like a
/// working upload.
/// </summary>
public class SharemodsPipelineTests
{
    // The guest form, as served: an empty sess_id beside utype=anon, and the ONLY action on the page
    // belonging to the remote-URL box. The file form's action is filled in by script, so there is
    // nothing else for a scrape to find.
    private const string HomepageHtml = """
        <!doctype html><html><body>
        <div>Maximum file size <b>200 Mb</b></div>
        <form method="post" id="uploadurl" action="https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="">
          <input type="hidden" name="utype" value="anon">
          <input type="hidden" name="file_public" value="1">
          <input name="link_rcpt" class="form-control" value="" type="text">
        </form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_PostsToTheFileUploader_NotThePagesUrlImporter()
    {
        List<(string Endpoint, IReadOnlyDictionary<string, string> Fields)> calls = [];
        SharemodsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(HomepageHtml),
            uploadOverride: (_, endpoint, extra, _, _) =>
            {
                calls.Add((endpoint, new Dictionary<string, string>(extra)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"j2rbr1dluo0x","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://sharemods.com/j2rbr1dluo0x", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string endpoint, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);

        // The scraped action said upload_type=url. Sending a file THERE hits a link importer: a real
        // endpoint, a plausible reply, nothing stored.
        Assert.Equal("https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=file", endpoint);
        Assert.DoesNotContain("upload_type=url", endpoint, StringComparison.Ordinal);

        // The set proven against the live host, which is the page's own — note file_public=1 and
        // file_descr, which the base's hexload-derived default does not send.
        Assert.Equal(string.Empty, fields["sess_id"]);   // empty sess_id + utype=anon IS the guest signal
        Assert.Equal("anon", fields["utype"]);
        Assert.Equal("1", fields["file_public"]);
        Assert.True(fields.ContainsKey("file_descr"));
        Assert.False(fields.ContainsKey("mode"));
    }

    [Theory]
    [InlineData("https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=url", "https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=file")]
    [InlineData("https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=file", "https://bio7.sharemods.com/cgi-bin/upload.cgi?upload_type=file")]
    [InlineData("https://bio7.sharemods.com/cgi-bin/upload.cgi", "https://bio7.sharemods.com/cgi-bin/upload.cgi")]
    public void ToFileUploadUrl_RewritesOnlyTheImporter(string action, string expected)
        => Assert.Equal(expected, SharemodsPipeline.ToFileUploadUrl(action));

    [Fact]
    public async Task RunAsync_FileOverTheTwoHundredMegabyteCap_RejectedBeforeAnyTransfer()
    {
        List<string> endpoints = [];
        SharemodsPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(HomepageHtml),
            uploadOverride: (_, endpoint, _, _, _) =>
            {
                endpoints.Add(endpoint);
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeContext() with { FileSize = (200L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.Empty(endpoints);
    }

    [Fact]
    public void ShareMods_IsAnonymous_ButDisabled()
    {
        SharemodsPipeline pipeline = new();
        Assert.Equal("ShareMods", pipeline.Name);
        Assert.Equal(200L * 1024 * 1024, pipeline.MaxFileSize);

        // Verified with real bytes: two guest uploads, both link pages served the file.
        Assert.True(pipeline.SupportsAnonymousUpload);

        // It escalates against volume from one address, so this stays low deliberately — the ceiling
        // was never measured cleanly because every attempt to bracket it tripped the escalation.
        Assert.Equal(2, pipeline.MaxConcurrentUploadsFor(new FileHosterLoginDto { FileHosterName = "ShareMods" }));

        // DISABLED 2026-08-02: Cloudflare began challenging this client and the cause was never
        // settled (see SharemodsPipeline.cs). The pipeline is finished; only the wire-up is off.
        // Before flipping this to Assert.True, re-enable the entries in ServiceRegistration.cs and
        // FileHosterClient.cs — and only after an upload completes from an address that hasn't been
        // probing the host.
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\mod.rar",
        FileName = "mod.rar",
        FileSize = 100,
        HosterName = "ShareMods",
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "ShareMods", IsAnonymous = true },
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
