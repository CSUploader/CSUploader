// <copyright file="TempShAndLitterboxTests.cs" company="CSUploader">
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
/// temp.sh and Litterbox — two anonymous drop hosts whose entire protocol is one multipart POST
/// answering with a bare URL. Both were verified with real bytes on 2026-08-03 before these were
/// written; the fixtures are the responses those uploads returned.
/// <para>
/// Because the response has no envelope, the parser's job is entirely "is this a link or is it an
/// error?" — which is what most of these pin.
/// </para>
/// </summary>
public class TempShAndLitterboxTests
{
    [Fact]
    public async Task TempSh_PostsTheFileAndReturnsTheBodyAsTheLink()
    {
        List<(string Url, string FileField, IReadOnlyDictionary<string, string> Fields)> calls = [];
        TempShPipeline pipeline = new((filePath, url, fields, headers, _) =>
        {
            calls.Add((url, "file", new Dictionary<string, string>(fields)));
            Assert.Equal("https://temp.sh", headers!["Origin"]);
            return Task.FromResult(new HttpResponseSnapshot(200, "https://temp.sh/Bjbte/x.bin\n", Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("Temp.sh"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://temp.sh/Bjbte/x.bin", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string url, _, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://temp.sh/upload", url);
        Assert.Empty(fields); // the file is the entire request — no other fields at all
    }

    [Fact]
    public async Task Litterbox_SendsTheLongestRetention_AndUsesTheServersOwnLinkHost()
    {
        List<(string Url, IReadOnlyDictionary<string, string> Fields)> calls = [];
        LitterboxPipeline pipeline = new((filePath, url, fields, _, _) =>
        {
            calls.Add((url, new Dictionary<string, string>(fields)));
            // Note the reply names litter.catbox.moe — a different host than the one posted to.
            return Task.FromResult(new HttpResponseSnapshot(200, "https://litter.catbox.moe/v3ecdo.rar", Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("Litterbox"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());

        // Used verbatim: rebuilding it from the upload host would produce a link that doesn't resolve.
        Assert.Equal("https://litter.catbox.moe/v3ecdo.rar", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string url, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://litterbox.catbox.moe/resources/internals/api.php", url);
        Assert.Equal("fileupload", fields["reqtype"]);
        Assert.Equal("72h", fields["time"]); // the longest the host offers — never a shorter default
    }

    [Theory]
    // A bare URL is the success shape for both hosts.
    [InlineData(200, "https://temp.sh/abc/x.bin", true)]
    [InlineData(200, "  https://temp.sh/abc/x.bin \n", true)]
    // …and anything else is the host refusing, since neither has an error envelope.
    [InlineData(200, "File too large", false)]
    [InlineData(200, "", false)]
    [InlineData(500, "https://temp.sh/abc/x.bin", false)]
    // A URL with a space is a sentence that happens to contain one, not a link.
    [InlineData(200, "https://temp.sh is down for maintenance", false)]
    public void ParseUploadResponse_TreatsOnlyABareUrlAsSuccess(int status, string body, bool ok)
    {
        HttpResponseSnapshot snap = new(status, body, Array.Empty<string>());

        (string? tempUrl, string? tempError) = TempShPipeline.ParseUploadResponse(snap);
        Assert.Equal(ok, tempUrl is not null);
        Assert.Equal(ok, tempError is null);

        // Litterbox's parser must behave identically — same response shape, same rules.
        (string? litterUrl, string? litterError) = LitterboxPipeline.ParseUploadResponse(snap);
        Assert.Equal(ok, litterUrl is not null);
        Assert.Equal(ok, litterError is null);
    }

    [Fact]
    public async Task OversizedFiles_AreRejectedBeforeAnyTransfer()
    {
        bool uploaded = false;
        TempShPipeline temp = new((_, _, _, _, _) => { uploaded = true; return Task.FromResult(new HttpResponseSnapshot(200, "x", Array.Empty<string>())); });
        LitterboxPipeline litter = new((_, _, _, _, _) => { uploaded = true; return Task.FromResult(new HttpResponseSnapshot(200, "x", Array.Empty<string>())); });

        List<UploadEvent> tempEvents = await DrainAsync(temp.RunAsync(
            MakeContext("Temp.sh") with { FileSize = (4L * 1000 * 1000 * 1000) + 1 }, CancellationToken.None));
        List<UploadEvent> litterEvents = await DrainAsync(litter.RunAsync(
            MakeContext("Litterbox") with { FileSize = (1L * 1024 * 1024 * 1024) + 1 }, CancellationToken.None));

        Assert.Single(tempEvents.OfType<AttemptFailed>());
        Assert.Single(litterEvents.OfType<AttemptFailed>());
        Assert.False(uploaded);
    }

    [Fact]
    public void Both_AreAnonymousOnly_AndRegistered()
    {
        TempShPipeline temp = new();
        LitterboxPipeline litter = new();

        Assert.True(temp.SupportsAnonymousUpload);
        Assert.True(litter.SupportsAnonymousUpload);
        Assert.Equal(4L * 1000 * 1000 * 1000, temp.MaxFileSize);
        Assert.Equal(1L * 1024 * 1024 * 1024, litter.MaxFileSize);

        Assert.Equal("temp.sh", FileHosterClient.FileHosters["Temp.sh"]);
        Assert.Equal("litterbox.catbox.moe", FileHosterClient.FileHosters["Litterbox"]);
    }

    [Fact]
    public async Task NeitherHasAccounts_SoCheckAccountSaysSoRatherThanFailingObscurely()
    {
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        AccountCheckResult temp = await new TempShPipeline().CheckAccountAsync("u", "p", null, handler, ProxyChoice.Direct, CancellationToken.None);
        AccountCheckResult litter = await new LitterboxPipeline().CheckAccountAsync("u", "p", null, handler, ProxyChoice.Direct, CancellationToken.None);

        Assert.False(temp.IsValid);
        Assert.False(litter.IsValid);
        Assert.Contains("Anonymous", temp.Message, StringComparison.OrdinalIgnoreCase);

        // Litterbox specifically warns that a catbox account doesn't carry over — they look like one
        // service and share nothing but an API shape.
        Assert.Contains("catbox", litter.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AttemptContext MakeContext(string hoster) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.bin",
        FileName = "x.bin",
        FileSize = 1024,
        HosterName = hoster,
        Credentials = new FileHosterLoginDto { Id = 0, FileHosterName = hoster, IsAnonymous = true },
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
