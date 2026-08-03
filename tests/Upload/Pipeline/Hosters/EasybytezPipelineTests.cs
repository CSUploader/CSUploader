// <copyright file="EasybytezPipelineTests.cs" company="CSUploader">
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
/// Easybytez on the shared <see cref="XfsProSessionPipeline"/>. The protocol itself is covered by
/// filehoster.io's suite (same base, unchanged by the extraction), so what's pinned here is only what
/// this host does differently: its own start_upload/import_file field sets, its dashboard markup, and
/// its cap. Fixtures are from a signed-in browser capture (2026-08-03) with session values replaced.
/// </summary>
public class EasybytezPipelineTests
{
    private const string LoginPageHtml = """<form name="FL"><input type="hidden" name="op" value="login"><input type="hidden" name="token" value="abc123def456"></form>""";
    private const string StartJson = """{"url":"https://fs1.easybytez.org/cgi-bin","plugin":"xfspro"}""";
    private const string ChunkOkJson = """{"status":"OK"}""";
    private const string ImportJson = """{"file_code":"gqhc35ihv729","links":{"short_link":"https://easybytez.org/d/4FbV","download_link":"https://easybytez.org/gqhc35ihv729/x.bin","delete_link":"https://easybytez.org/gqhc35ihv729/x.bin?killcode=zz"},"status":"OK"}""";

    [Fact]
    public async Task RunAsync_PostsTheCapturesFieldSets_AndReturnsTheDownloadLink()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Form)> forms = [];
        string? chunkUrl = null;

        EasybytezPipeline pipeline = new(
            getOverride: (_, _) => new HttpResponseSnapshot(200, LoginPageHtml, Array.Empty<string>()),
            postFormOverride: (url, form) =>
            {
                forms.Add((url, new Dictionary<string, string>(form)));
                return form.GetValueOrDefault("op") switch
                {
                    // The login answers with the session cookie on a 302, as the live host does.
                    "login" => new HttpResponseSnapshot(302, string.Empty, ["xfss=sess_demo_16ch; path=/; HttpOnly"]),
                    "start_upload" => new HttpResponseSnapshot(200, StartJson, Array.Empty<string>()),
                    _ => new HttpResponseSnapshot(200, ImportJson, Array.Empty<string>()),
                };
            },
            chunkPutOverride: (url, _, _, _, _, progress) =>
            {
                chunkUrl = url;
                progress(1024, 1024);
                return new HttpResponseSnapshot(200, ChunkOkJson, Array.Empty<string>());
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://easybytez.org/gqhc35ihv729/x.bin", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Equal("https://fs1.easybytez.org/cgi-bin/put_chunk.cgi", chunkUrl);

        // login, then start_upload, then import_file
        (string startUrl, IReadOnlyDictionary<string, string> start) = forms.Single(f => f.Form.GetValueOrDefault("op") == "start_upload");
        Assert.Equal("https://easybytez.org/", startUrl);

        // The capture's set: NO file_size (filehoster.io sends one) and file_public=0, not 1.
        Assert.Equal(
            new[] { "file_descr", "file_name", "file_public", "op" },
            start.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("0", start["file_public"]);

        IReadOnlyDictionary<string, string> import = forms.Single(f => f.Form.GetValueOrDefault("op") == "import_file").Form;
        Assert.Equal("sess_demo_16ch", import["sess_id"]); // the xfss session attributes the upload
        Assert.Equal("0", import["file_public"]);
        Assert.Equal("x.bin", import["fname"]);
    }

    [Fact]
    public async Task RunAsync_Anonymous_IsRefusedLocally_BecauseTheHostRefusesItAnyway()
    {
        // Its upload page renders a utype=anon guest form, but the node answers "uploads are not
        // enabled for your account type" — so offering anonymous would only waste a transfer.
        EasybytezPipeline pipeline = new(
            postFormOverride: (_, _) => throw new InvalidOperationException("must not reach the network"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with
        {
            Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = "Easybytez", IsAnonymous = true },
        };

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains("account", Assert.Single(events.OfType<AttemptFailed>()).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(pipeline.SupportsAnonymousUpload);
    }

    [Fact]
    public async Task RunAsync_FileOverTheRegisteredCap_RejectedBeforeAnyTransfer()
    {
        EasybytezPipeline pipeline = new(
            postFormOverride: (_, _) => throw new InvalidOperationException("must not reach the network"),
            chunkPutOverride: (_, _, _, _, _, _) => throw new InvalidOperationException("must not upload"));

        AttemptContext ctx = MakeContext() with { FileSize = (200L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        string reason = Assert.Single(events.OfType<AttemptFailed>()).Reason;
        Assert.Contains("200", reason, StringComparison.Ordinal); // the site's own "200 Mb", not 209.7 MB
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    // The live dashboard: the label in its own div, the figure in an fs-1 one.
    [InlineData("""<div class="text-muted">Used space</div> <div class="fs-1 fw-bold text-dark">1.50</div>""", 1610612736L)]
    [InlineData("""<div class="text-muted">Used space</div> <div class="fs-1 fw-bold text-dark">0.00</div>""", 0L)]
    // filehoster.io's fs-4 markup must NOT match here — the themes differ, and a cross-match would
    // silently report the wrong host's number if these ever shared a parser.
    [InlineData("""<div class="small">Used space</div> <div class="fs-4">0.06</div>""", null)]
    [InlineData("<div>no storage panel here</div>", null)]
    public void ParseUsedSpace_ReadsThisThemesFigureOnly(string html, long? expected)
        => Assert.Equal(expected, EasybytezPipeline.ParseUsedSpace(html));

    [Fact]
    public void Easybytez_IsAccountOnly_AndRegistered()
    {
        EasybytezPipeline pipeline = new();
        Assert.Equal("Easybytez", pipeline.Name);
        Assert.Equal(200L * 1024 * 1024, pipeline.MaxFileSize);
        Assert.False(pipeline.SupportsAnonymousUpload);
        Assert.Equal("easybytez.org", FileHosterClient.FileHosters["Easybytez"]);

        // Plain username/password — no WebView, no API key, so it stays on the default credential mode.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("Easybytez"));
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.bin",
        FileName = "x.bin",
        FileSize = 1024,
        HosterName = "Easybytez",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Easybytez", Username = "demo", Password = "pw" },
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
