// <copyright file="UsersDrivePipelineTests.cs" company="CSUploader">
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
/// UsersDrive — a plain anonymous shim on <see cref="XFileSharingApiPipeline"/>, so what's pinned is
/// the shim's own configuration plus the one thing its homepage can get wrong: it renders TWO
/// upload.cgi forms and only the first is the file uploader. Fixture mirrors the live homepage
/// (2026-08-01).
/// </summary>
public class UsersDrivePipelineTests
{
    // The live homepage: the file uploader first, then the remote-URL form posting to the same
    // upload.cgi path with a different query.
    private const string HomeHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" action="https://d900.userdrive.org/cgi-bin/upload.cgi?upload_type=file&utype=anon">
          <input type="hidden" name="sess_id" value="">
          <input type="hidden" name="utype" value="anon">
          <input type="file" name="file_0">
        </form>
        <form method="post" id="uploadurl" action="https://d900.userdrive.org/cgi-bin/upload.cgi?upload_type=url">
          <input type="hidden" name="sess_id" value="">
          <textarea name="url_mass"></textarea>
        </form>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_Anonymous_PostsToTheFileForm_AndBuildsTheShareLink()
    {
        List<string> getUrls = [];
        List<UploadCall> calls = [];
        UsersDrivePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(HomeHtml); },
            uploadOverride: (filePath, endpoint, extra, headers, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra),
                    headers is null ? null : new Dictionary<string, string>(headers)));
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"talmb9r7isrl","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeAnonymousContext(), CancellationToken.None));

        // Link is built from the SITE host even though the bytes went to userdrive.org.
        Assert.Equal("https://usersdrive.com/talmb9r7isrl", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());

        // The form lives on the homepage, cache-busted by the base.
        Assert.Contains("usersdrive.com", Assert.Single(getUrls), StringComparison.Ordinal);

        UploadCall call = Assert.Single(calls);
        Assert.Equal("https://d900.userdrive.org/cgi-bin/upload.cgi?upload_type=file&utype=anon", call.Endpoint);
        Assert.DoesNotContain("upload_type=url", call.Endpoint, StringComparison.Ordinal); // never the url_mass form
        Assert.Equal(string.Empty, call.ExtraFields["sess_id"]);                            // anonymous
        Assert.Equal("anon", call.ExtraFields["utype"]);
    }

    [Fact]
    public async Task RunAsync_FileOverTheGuestCap_IsRejectedWithoutAnyHttp()
    {
        List<UploadCall> calls = [];
        UsersDrivePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (_, _) => throw new InvalidOperationException("must not fetch"),
            uploadOverride: (filePath, endpoint, extra, _, _) =>
            {
                calls.Add(new UploadCall(filePath, endpoint, new Dictionary<string, string>(extra), null));
                return Task.FromResult(new HttpResponseSnapshot(200, "[]", Array.Empty<string>()));
            });

        AttemptContext ctx = MakeAnonymousContext() with { FileSize = (5250L * 1024 * 1024) + 1 };
        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Single(events.OfType<AttemptFailed>());
        Assert.Empty(calls);
        Assert.Empty(events.OfType<TransferStarted>());
    }

    [Fact]
    public void UsersDrive_IsAnonymous_WithTheStatedGuestCap()
    {
        UsersDrivePipeline pipeline = new();
        Assert.Equal("UsersDrive", pipeline.Name);

        // Anonymous confirmed by an actual upload, not by the form merely existing — the distinction
        // that separated this from DropGalaxy, Uploady and Clicknupload.
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.Equal(5250L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));

        Assert.True(FileHosterClient.FileHosters.ContainsKey("UsersDrive"));
        Assert.Equal("usersdrive.com", FileHosterClient.FileHosters["UsersDrive"]);
    }


    [Fact]
    public void UsersDrive_AnAccountDoublesTheCap_AndTheTrafficFigureIsNotIt()
    {
        // Both numbers are the host's own words on the upload form it serves that session: 5250 Mb to
        // a guest, 10500 Mb signed in.
        UsersDrivePipeline pipeline = new();

        Assert.Equal(5250L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = true }));
        Assert.Equal(10500L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));

        // The account page's prominent figure is "Traffic available today: 75000 Mb" — a daily
        // bandwidth allowance. Clicknupload set the identical trap, so pin that it never becomes a
        // per-file cap.
        Assert.NotEqual(75000L * 1024 * 1024, pipeline.MaxFileSizeFor(new FileHosterLoginDto { IsAnonymous = false }));
    }

    [Fact]
    public void UsersDrive_SignsInWithoutABrowser_OnTheWebFormPath()
    {
        // It HAS a working REST API, but no page anywhere prints a key, so the account can only ship
        // on the web-form path — the same call DDownload needed.
        UsersDrivePipeline pipeline = new();

        Assert.True(pipeline.UsesWebFormUploadForTests);
        Assert.True(pipeline.SupportsDirectLoginForTests);

        // Its LOGIN form has no captcha (its registration form does, but this app never registers),
        // so credentials go in the app's own dialog and no sign-in window opens.
        Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode("UsersDrive"));
        Assert.False(HosterCredentialModes.IsWebViewSignInHoster("UsersDrive"));
    }

    private static async Task<List<UploadEvent>> DrainAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in stream)
        {
            events.Add(ev);
        }

        return events;
    }

    private static AttemptContext MakeAnonymousContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 4096,
        HosterName = "UsersDrive",
        Credentials = new FileHosterLoginDto { FileHosterName = "UsersDrive", IsAnonymous = true },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private sealed record UploadCall(
        string FilePath,
        string Endpoint,
        IReadOnlyDictionary<string, string> ExtraFields,
        IReadOnlyDictionary<string, string>? Headers);
}
