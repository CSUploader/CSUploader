// <copyright file="YetiShareGuestPipelineTests.cs" company="CSUploader">
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
/// The GUEST half of <see cref="YetiSharePipeline"/> — udrop and BowFile. Filestank covers the account
/// half in its own file; what's pinned here is everything the guest path does differently, and the one
/// thing the two guest hosts do differently from each other: where their node lives. Fixtures are the
/// real scripts and replies (2026-08-07, verified by uploading).
/// </summary>
public class YetiShareGuestPipelineTests
{
    /// <summary>The site's own uploader.js, trimmed to what gets read: the node, the ticket, the cap.</summary>
    private static string UploaderJs(string node, long cap) =>
        "var uploaderMaxSize = 0;\n"
        + "uploaderMaxSize = " + cap + ";\n"
        + "var maxChunkSize = 100000000;\n"
        + "$('#fileupload').fileupload({ sequentialUploads: false, limitConcurrentUploads: 1,\n"
        + "  url: '" + node + "/ajax/file_upload_handler?r=x&p=https&csaKey1=aaa&csaKey2=bbb',\n"
        + "  maxFileSize: uploaderMaxSize });\n"
        + "data.formData = {_sessionid: 'sess-abc', cTracker: 'track-123', maxChunkSize: maxChunkSize};\n";

    private const string UploadedJson = """
        [{"name":"probe.rar","size":3072,"type":"application/octet-stream","error":null,"url":"https://www.udrop.com/OY37/probe.rar","delete_url":"https://www.udrop.com/OY37~d?abc","short_url":"OY37"}]
        """;

    [Fact]
    public async Task Guest_ScrapesTheTicketAndUploads_WithoutEverSigningIn()
    {
        // authService is null, so anything reaching for the sign-in window fails this rather than passing.
        List<IReadOnlyDictionary<string, string>?> ticketHeaders = [];
        UdropPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, headers) =>
            {
                ticketHeaders.Add(headers);
                return Task.FromResult(new HttpResponseSnapshot(
                    200, UploaderJs("https://www.udrop.com", 5_368_709_120), ["filehosting=guest-sess; path=/"]));
            },
            uploadOverride: (_, _, fields, _, _) =>
            {
                Assert.Equal("sess-abc", fields["_sessionid"]);
                Assert.Equal("track-123", fields["cTracker"]);
                return Task.FromResult(new HttpResponseSnapshot(200, UploadedJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(GuestContext("Udrop"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(events.OfType<AuthStarted>());   // a guest never signs in
        Assert.Equal("https://www.udrop.com/OY37/probe.rar", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // The ticket request goes out with NO cookie — the guest hasn't got one yet, and the script's
        // own reply is what issues it.
        Assert.Null(Assert.Single(ticketHeaders)?.GetValueOrDefault("Cookie"));
    }

    [Fact]
    public async Task Guest_SendsTheIssuedCookieToASameHostNode()
    {
        // udrop's node IS the site, i.e. an ordinary route behind the session middleware. Without the
        // cookie uploader.js issued it answers a 404 PAGE rather than an error — measured live, and the
        // reason the guest path bothers to read Set-Cookie at all.
        IReadOnlyDictionary<string, string>? uploadHeaders = null;
        UdropPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, UploaderJs("https://www.udrop.com", 5_368_709_120), ["filehosting=guest-sess; path=/"])),
            uploadOverride: (_, _, _, headers, _) =>
            {
                uploadHeaders = headers;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadedJson, Array.Empty<string>()));
            });

        await DrainAsync(pipeline.RunAsync(GuestContext("Udrop"), CancellationToken.None));

        Assert.Equal("filehosting=guest-sess", uploadHeaders?.GetValueOrDefault("Cookie"));
    }

    [Fact]
    public async Task Guest_SendsNoCookieToASeparateStorageNode()
    {
        // BowFile's node is fsNN.bowfile.com. The site cookie is host-only so it could never reach
        // there anyway, and the node doesn't want it — it authenticates on the _sessionid field.
        // Verified by uploading to BowFile with no cookie at all.
        IReadOnlyDictionary<string, string>? uploadHeaders = null;
        BowFilePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, UploaderJs("https://fs20.bowfile.com", 21_474_836_480), ["filehosting=guest-sess; path=/"])),
            uploadOverride: (_, _, _, headers, _) =>
            {
                uploadHeaders = headers;
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"name":"probe.rar","error":null,"url":"https://bowfile.com/1tvbv"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(GuestContext("BowFile"), CancellationToken.None));

        Assert.False(uploadHeaders!.ContainsKey("Cookie"));

        // …and the link comes back on the APEX, not the node it was posted to.
        Assert.Equal("https://bowfile.com/1tvbv", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public async Task Guest_SessionDeclaringAZeroCap_IsRefusedWithoutSendingAByte()
    {
        // This is how the platform says "this session may not upload", and it is the whole reason
        // Filestank is account-only while these two are not. A guest host that started answering 0 must
        // fail loudly here rather than push a file at a node that will refuse it.
        bool uploaded = false;
        UdropPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, UploaderJs("https://www.udrop.com", 0), ["filehosting=guest-sess"])),
            uploadOverride: (_, _, _, _, _) =>
            {
                uploaded = true;
                return Task.FromResult(new HttpResponseSnapshot(200, UploadedJson, Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(GuestContext("Udrop"), CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.False(uploaded);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Theory]
    [InlineData("rls.part1.rar", null)]
    [InlineData("rls.r00", null)]
    [InlineData("rls.sfv", null)]
    [InlineData("rls.nfo", null)]
    // Measured: the node refuses this one with "banned by the site admin".
    [InlineData("dump.bin", ".bin")]
    public void Udrop_RejectsOnlyTheExtensionItWasMeasuredToRefuse(string fileName, string? expected)
    {
        string? reason = new UdropPipeline().RejectedFileExtensionReason(fileName);

        if (expected is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(expected, reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnAccountBuysStorage_NotABiggerFile()
    {
        // From a capture of real signed-in uploads (2026-08-08): the uploader script declares the SAME
        // per-file cap to an account as to a guest on both hosts. So the wizard must not imply an
        // account lifts the limit — what it buys is storage (100 GB on udrop) and the file manager.
        IFileHosterPipeline udrop = new UdropPipeline();
        Assert.Equal(
            udrop.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }),
            udrop.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        IFileHosterPipeline bow = new BowFilePipeline();
        Assert.Equal(
            bow.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }),
            bow.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));
    }

    [Fact]
    public void BothGuestHosts_SignInWithoutABrowser()
    {
        // Both login pages are a plain username/password/submitme form with no captcha of any kind, so
        // the account goes in the app's own dialog. Filestank deliberately does NOT do this: its
        // sign-in has never been shown to work headlessly, and guessing would produce a sign-in that
        // silently never succeeds.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("Udrop"));
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("BowFile"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("Udrop"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("BowFile"));

        Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode("Filestank"));
    }

    [Fact]
    public void BothGuestHosts_AreAnonymousAndRegistered()
    {
        UdropPipeline udrop = new();
        Assert.True(udrop.SupportsAnonymousUpload);
        Assert.Equal(5_368_709_120, udrop.MaxFileSize);
        Assert.Equal("udrop.com", FileHosterClient.FileHosters["Udrop"]);

        BowFilePipeline bow = new();
        Assert.True(bow.SupportsAnonymousUpload);
        Assert.Equal(21_474_836_480, bow.MaxFileSize);
        Assert.Equal("bowfile.com", FileHosterClient.FileHosters["BowFile"]);

        // Filestank is the same platform and the same code — and still account-only, because its script
        // declares a zero cap to a guest. The three together are what the base is for.
        Assert.False(new FilestankPipeline().SupportsAnonymousUpload);
    }


    // -- MegaUp: the node-POOL variant of the same platform ---------------------------------------

    /// <summary>
    /// MegaUp's own shape: <b>no literal <c>url:</c> at all</b> — a JSON pool of nodes and a random
    /// pick, so <c>url:</c> is a call rather than an address. Slashes arrive escaped because the pool
    /// is JSON emitted into JavaScript.
    /// </summary>
    private static string PoolUploaderJs(params string[] nodes)
    {
        string pool = string.Join(
            ",",
            nodes.Select(n => "\"" + n.Replace("/", "\\/", StringComparison.Ordinal) + "\\/ajax\\/file_upload_handler?r=megaup.net&p=https&csaKey1=aaa\""));

        return $$"""
            var uploaderMaxSize = 0;
            var maxChunkSize = 0;
            if (browserXHR2Support() == true) { maxChunkSize = 100000000; var uploaderMaxSize = 5368709120; }
            function getUploadEndpoint() {
                const uploadEndpoints = [{{pool}}];
                var endpoint = "https:\/\/megaup.net";
                if (uploadEndpoints instanceof Array) { endpoint = uploadEndpoints[Math.floor(Math.random()*uploadEndpoints.length)]; }
                return endpoint;
            }
            $('#fileUpload #uploader').fileupload({ url: getUploadEndpoint(), maxFileSize: uploaderMaxSize });
            data.formData = {_sessionid: 'sess-mega', cTracker: 'track-mega', maxChunkSize: maxChunkSize};
            """;
    }

    public void ReadNodeUrl_TakesAPoolMember_WhenThereIsNoLiteralUrl()
    {
        // MegaUp declares `url: getUploadEndpoint()` — a CALL, not an address. A parser that only knows
        // the literal form reads the whole host as "no upload ticket", which is exactly how MegaUp
        // failed before the pool was understood.
        string? node = YetiSharePipeline.ReadNodeUrl(PoolUploaderJs("https://f116.mupload.store"));

        Assert.Equal("https://f116.mupload.store/ajax/file_upload_handler?r=megaup.net&p=https&csaKey1=aaa", node);
    }

    [Fact]
    public void ReadNodeUrl_SpreadsAcrossThePool_RatherThanAlwaysPickingTheFirst()
    {
        // The site picks at random for a reason: a dead pool member stays dead, so always taking the
        // first would send every upload from every user at the same box.
        string js = PoolUploaderJs("https://f1.mupload.store", "https://f2.mupload.store", "https://f3.mupload.store");

        HashSet<string> seen = [];
        for (int i = 0; i < 200; i++)
        {
            seen.Add(YetiSharePipeline.ReadNodeUrl(js)!);
        }

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void ReadNodeUrl_PrefersALiteralUrlWhenBothArePresent()
    {
        // udrop and BowFile still declare the literal form; it must keep winning so adding the pool
        // fallback can't change where the hosts that already work send their bytes.
        string js = UploaderJs("https://www.udrop.com", 5_368_709_120)
                    + PoolUploaderJs("https://f116.mupload.store");

        Assert.StartsWith("https://www.udrop.com/", YetiSharePipeline.ReadNodeUrl(js)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MegaUp_UploadsAsAGuest_ThroughAPoolNode()
    {
        string? endpoint = null;
        MegaUpPipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => Task.FromResult(new HttpResponseSnapshot(
                200, PoolUploaderJs("https://f116.mupload.store"), ["filehosting=guest; path=/"])),
            uploadOverride: (_, url, fields, _, _) =>
            {
                endpoint = url;
                Assert.Equal("sess-mega", fields["_sessionid"]);
                Assert.Equal("track-mega", fields["cTracker"]);
                return Task.FromResult(new HttpResponseSnapshot(
                    200,
                    """[{"name":"probe.rar","size":3072,"error":null,"url":"https://megaup.net/abc123/probe.rar","delete_url":"https://megaup.net/abc123~d?k"}]""",
                    Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(GuestContext("MegaUp"), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.StartsWith("https://f116.mupload.store/", endpoint!, StringComparison.Ordinal);
        Assert.Equal("https://megaup.net/abc123/probe.rar", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
    }

    [Fact]
    public void MegaUp_IsAGuestHost_AtTheCapItsScriptDeclares()
    {
        MegaUpPipeline pipeline = new();
        Assert.Equal("MegaUp", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // 5 GiB - the same figure udrop declares. Its advertised "200 GB premium" is STORAGE.
        Assert.Equal(5_368_709_120, pipeline.MaxFileSize);
        Assert.Equal("megaup.net", FileHosterClient.FileHosters["MegaUp"]);

        // Plain username/password form, no captcha, so no sign-in window ever opens.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("MegaUp"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("MegaUp"));
    }

    private static AttemptContext GuestContext(string hoster) => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\probe.rar",
        FileName = "probe.rar",
        FileSize = 3072,
        HosterName = hoster,
        Credentials = new FileHosterLoginDto { FileHosterName = hoster, IsAnonymous = true },
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
