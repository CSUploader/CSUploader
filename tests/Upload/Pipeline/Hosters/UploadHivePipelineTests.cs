// <copyright file="UploadHivePipelineTests.cs" company="CSUploader">
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
/// UploadHive — an anonymous shim on <see cref="XFileSharingApiPipeline"/> with two deviations, both
/// of which would post files somewhere useless if missed. Fixture is the real <c>/upload</c> page
/// (2026-08-08, verified by uploading).
/// </summary>
public class UploadHivePipelineTests
{
    /// <summary>
    /// The live page, trimmed. Note what is NOT here: the file form carries no <c>action</c>, and the
    /// only <c>upload.cgi</c> action on the page belongs to the remote-URL form.
    /// </summary>
    private const string UploadPageHtml = """
        <!DOCTYPE html><html><body>
        <form id="uploadfile" enctype="multipart/form-data" method="post">
          <input type="hidden" name="sess_id" value="">
          <input type="hidden" name="utype" value="anon">
          <input type="file" name="file_0">
        </form>
        <form method="post" id="uploadurl" action="https://fs430.uploadhive.com/cgi-bin/upload.cgi?upload_type=url">
          <textarea name="url_mass"></textarea>
        </form>
        <script>var uploader = { ext_allowed: '', ext_not_allowed: '7z|001', max_upload_files: '5', max_upload_filesize: '0' };</script>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_PostsToTheFileEndpoint_DerivedFromTheUrlFormsNode()
    {
        // The base scrapes the page's only upload.cgi action, which here is the REMOTE-URL form's.
        // Posting a file there would hit the URL-import endpoint — a wrong destination that answers
        // plausibly rather than erroring. The node is kept, the query is rewritten.
        List<string> getUrls = [];
        string? endpoint = null;
        UploadHivePipeline pipeline = new(
            authService: null,
            loginRepository: null,
            getOverride: (url, _) => { getUrls.Add(url); return Task.FromResult(UploadPageHtml); },
            uploadOverride: (_, url, extra, _, _) =>
            {
                endpoint = url;
                Assert.Equal(string.Empty, extra["sess_id"]);
                Assert.Equal("anon", extra["utype"]);
                return Task.FromResult(new HttpResponseSnapshot(
                    200, """[{"file_code":"888rv70d6hum","file_status":"OK"}]""", Array.Empty<string>()));
            });

        List<UploadEvent> events = await DrainAsync(pipeline.RunAsync(MakeContext(), CancellationToken.None));

        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Equal("https://fs430.uploadhive.com/cgi-bin/upload.cgi?upload_type=file&utype=anon", endpoint);
        Assert.DoesNotContain("upload_type=url", endpoint!, StringComparison.Ordinal);

        // The link is built from the SITE host even though the bytes went to fs430.
        Assert.Equal("https://uploadhive.com/888rv70d6hum", Assert.Single(events.OfType<TransferCompleted>()).FileUrl);

        // …and the form was looked for on /upload. The homepage carries none, which is also why an
        // api_get_limits sweep concluded this host wasn't XFileSharing at all.
        Assert.Contains("/upload", Assert.Single(getUrls), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rls.part1.rar", null)]
    [InlineData("rls.r00", null)]
    [InlineData("rls.sfv", null)]
    [InlineData("rls.nfo", null)]
    // Declared by the host (ext_not_allowed: '7z|001') and confirmed by uploading one of each: both
    // come back {"file_code":"undef","file_status":"unallowed extension"} AFTER the whole transfer.
    [InlineData("rls.7z", ".7z")]
    [InlineData("rls.001", ".001")]
    public void RejectedFileExtensionReason_MatchesTheHostsOwnBlocklist(string fileName, string? expected)
    {
        string? reason = new UploadHivePipeline().RejectedFileExtensionReason(fileName);

        if (expected is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(expected, reason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UploadHive_IsAnonymous_WithNoDeclaredCap()
    {
        UploadHivePipeline pipeline = new();
        Assert.Equal("UploadHive", pipeline.Name);
        Assert.True(pipeline.SupportsAnonymousUpload);

        // Its uploader config says max_upload_filesize: '0', meaning unlimited here — uploads succeed.
        // Inheriting the base's 1 GiB default would silently skip every larger file at queue time,
        // which is the bug Uploadrar shipped with.
        Assert.Null(pipeline.MaxFileSize);

        Assert.Equal("uploadhive.com", FileHosterClient.FileHosters["UploadHive"]);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\probe.rar",
        FileName = "probe.rar",
        FileSize = 4096,
        HosterName = "UploadHive",
        Credentials = new FileHosterLoginDto { FileHosterName = "UploadHive", IsAnonymous = true },
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
