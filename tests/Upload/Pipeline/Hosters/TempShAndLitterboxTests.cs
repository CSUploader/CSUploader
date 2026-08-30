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
/// temp.sh, Litterbox, tmpfiles.org and qu.ax — anonymous drop hosts whose entire protocol is one
/// multipart POST. Both were verified with real bytes on 2026-08-03 before these were
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

        // Omitting this takes the 6-character default: a browser capture sends 16 and gets
        // litter.catbox.moe/62yc2gn59rzqgeyk.avi, while our first uploads returned .../nrvct3.rar.
        // Six lowercase-alphanumerics is a walkable keyspace for a link that gets posted publicly.
        Assert.Equal("16", fields["fileNameLength"]);
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

        // Litterbox's parser must behave identically — same response shape, same rules. What the
        // two do with a failure now differs (Litterbox retries the transient ones — see
        // IsTransientHostFailure below), but that is a decision made ON the parser's output, not a
        // change to it.
        (string? litterUrl, string? litterError) = LitterboxPipeline.ParseUploadResponse(snap);
        Assert.Equal(ok, litterUrl is not null);
        Assert.Equal(ok, litterError is null);
    }

    [Theory]
    // The server describing ITS OWN failure — safe to re-run, because it kept nothing.
    [InlineData(500, "<html>500 | Internal Server Error</html>", true)]
    [InlineData(502, "", true)]
    [InlineData(503, "", true)]
    // "No file!" is the host stating outright that it stored no file. Seen live as a 412 on an
    // upload whose body was sent in full.
    [InlineData(412, "No file!", true)]
    // A verdict on the file we sent: re-uploading would only earn it again, at the cost of the
    // whole file.
    [InlineData(413, "File too large", false)]
    [InlineData(403, "Extension not allowed", false)]
    [InlineData(412, "Precondition failed for some other reason", false)]
    [InlineData(200, "not a url at all", false)]
    public void IsTransientHostFailure_RetriesTheHostsOwnFailures_NotItsVerdicts(int status, string body, bool transient)
    {
        HttpResponseSnapshot snap = new(status, body, Array.Empty<string>());

        Assert.Equal(transient, LitterboxPipeline.IsTransientHostFailure(snap));
    }

    [Fact]
    public async Task Litterbox_TransientHostFailure_IsThrownSoTheSharedRetryLayerReRunsIt()
    {
        // AttemptRunner only re-runs an attempt that THREW one of its two safe-to-retry faults; a
        // yielded AttemptFailed is a server verdict and terminal by design. So the difference
        // between "retried" and "not retried" is entirely which of those two this produces.
        LitterboxPipeline pipeline = new((_, _, _, _, _) =>
            Task.FromResult(new HttpResponseSnapshot(412, "No file!", Array.Empty<string>())));

        UploadProcessingFailedException ex = await Assert.ThrowsAsync<UploadProcessingFailedException>(
            () => DrainAsync(pipeline.RunAsync(MakeContext("Litterbox"), CancellationToken.None)));

        // The server's own words survive into the message, so an exhausted retry is diagnosable.
        Assert.Contains("No file!", ex.Message, StringComparison.Ordinal);
        Assert.Contains("412", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Litterbox_VerdictOnTheFile_StaysTerminal()
    {
        LitterboxPipeline pipeline = new((_, _, _, _, _) =>
            Task.FromResult(new HttpResponseSnapshot(413, "File too large", Array.Empty<string>())));

        List<UploadEvent> events = await DrainAsync(
            pipeline.RunAsync(MakeContext("Litterbox"), CancellationToken.None));

        AttemptFailed failed = Assert.IsType<AttemptFailed>(events[^1]);
        Assert.Contains("File too large", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TmpFiles_AlwaysSendsTheMaximumExpiry_BecauseTheDefaultIsOneHour()
    {
        // Measured against the live API: expire=172800 gives "File expires in 47 hours and 59 minutes",
        // omitting the field gives "59 minutes". 48x the retention for one optional field — the same
        // shape of trap Litterbox sets with fileNameLength.
        List<(string Url, IReadOnlyDictionary<string, string> Fields)> calls = [];
        TmpFilesPipeline pipeline = new((_, url, fields, headers, _) =>
        {
            calls.Add((url, new Dictionary<string, string>(fields)));
            Assert.Equal("application/json", headers!["Accept"]);
            return Task.FromResult(new HttpResponseSnapshot(
                200, """{"status":"success","data":{"url":"https://tmpfiles.org/wzwYIClXc4TY/x.bin"}}""", Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("TmpFiles"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://tmpfiles.org/wzwYIClXc4TY/x.bin", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string url, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://tmpfiles.org/api/v1/upload", url);
        Assert.Equal("172800", fields["expire"]); // the documented maximum, never the 3600 default
    }

    [Theory]
    // The documented success envelope.
    [InlineData(200, """{"status":"success","data":{"url":"https://tmpfiles.org/abc/x.bin"}}""", "https://tmpfiles.org/abc/x.bin")]
    // A url present under a NON-success status must not be read as a link.
    [InlineData(200, """{"status":"error","data":{"url":"https://tmpfiles.org/abc/x.bin"}}""", null)]
    [InlineData(200, """{"status":"success","data":{}}""", null)]
    [InlineData(200, "not json at all", null)]
    [InlineData(413, """{"status":"error"}""", null)]
    public void TmpFiles_ParseUploadResponse_ChecksTheStatus_NotJustTheUrl(int status, string body, string? expected)
    {
        (string? url, string? error) = TmpFilesPipeline.ParseUploadResponse(new HttpResponseSnapshot(status, body, Array.Empty<string>()));
        Assert.Equal(expected, url);
        Assert.Equal(expected is null, error is not null);
    }

    [Fact]
    public async Task QuAx_AsksForPermanent_BecauseItsFormDefaultsToThirtyDays()
    {
        // Measured live: omitting `expiry` gives expires ~30 days out, expiry=365 gives a year, and
        // expiry=-1 gives expires:null — no expiry at all. Its own form offers Permanent; taking the
        // default would silently give away 30-day links.
        List<(string Url, IReadOnlyDictionary<string, string> Fields)> calls = [];
        QuAxPipeline pipeline = new((_, url, fields, _, _) =>
        {
            calls.Add((url, new Dictionary<string, string>(fields)));
            return Task.FromResult(new HttpResponseSnapshot(
                200, """{"success":true,"files":[{"expires":null,"file_name":"uJq2A","size":262144,"url":"https://qu.ax/uJq2A"}]}""", Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext("Qu.ax") with { FileName = "release.rar" }, CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://qu.ax/uJq2A", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        (string url, IReadOnlyDictionary<string, string> fields) = Assert.Single(calls);
        Assert.Equal("https://qu.ax/upload.php", url);
        Assert.Equal("-1", fields["expiry"]); // Permanent — never the 30-day default
    }

    [Theory]
    // What a release set is actually made of, against what this host allows.
    [InlineData("release.rar", true)]
    [InlineData("release.part1.rar", true)]
    [InlineData("release.zip", true)]
    [InlineData("release.7z", true)]
    // …and the half of a classic set it refuses — probed live, each answering
    // {"message":"file type is not allowed"}.
    [InlineData("release.r00", false)]
    [InlineData("release.001", false)]
    [InlineData("release.sfv", false)]
    [InlineData("release.nfo", false)]
    [InlineData("noextension", false)]
    public void QuAx_RejectionReason_MirrorsTheHostsAllowlist(string fileName, bool accepted)
    {
        string? reason = QuAxPipeline.RejectionReason(fileName, 1024);
        Assert.Equal(accepted, reason is null);
        if (!accepted)
        {
            // The message has to say what WOULD work, or the user is left guessing.
            Assert.Contains(".rar", reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QuAx_RefusedFile_IsNotUploadedAtAll()
    {
        bool uploaded = false;
        QuAxPipeline pipeline = new((_, _, _, _, _) =>
        {
            uploaded = true;
            return Task.FromResult(new HttpResponseSnapshot(400, """{"message":"file type is not allowed","success":false}""", Array.Empty<string>()));
        });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(
            MakeContext("Qu.ax") with { FileName = "release.r00" }, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<TransferStarted>());
        Assert.False(uploaded); // the host refuses only AFTER the bytes arrive, so we must not send them
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

        QuAxPipeline qu = new();
        Assert.True(qu.SupportsAnonymousUpload);
        Assert.Equal(256L * 1024 * 1024, qu.MaxFileSize);
        Assert.Equal("qu.ax", FileHosterClient.FileHosters["Qu.ax"]);

        TmpFilesPipeline tmp = new();
        Assert.True(tmp.SupportsAnonymousUpload);
        Assert.Equal(100L * 1024 * 1024, tmp.MaxFileSize);
        Assert.Equal("tmpfiles.org", FileHosterClient.FileHosters["TmpFiles"]);
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
