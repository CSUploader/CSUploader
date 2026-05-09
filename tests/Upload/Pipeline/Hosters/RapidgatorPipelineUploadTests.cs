// <copyright file="RapidgatorPipelineUploadTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorPipelineUploadTests
{
    [Fact]
    public async Task RunAsync_HappyPath_EndsInTransferCompletedWithUrl()
    {
        Queue<string> responses = new(new[]
        {
            // login
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            // folder/create
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"package1","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
            // file/upload — returns the upload_url + upload_id
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.rapidgator/post"}},"status":200,"details":null}""",
            // file/upload_info — confirms upload, returns public file url
            """{"response":{"upload":{"file":{"url":"https://r.net/file/abc123"}}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://r.net/file/abc123", tc.FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenServerHasFileAlready_SkipsBytesAndUsesDedupUrl()
    {
        // Real Rapidgator dedup response: file/upload returns state=2 ("Done") with a
        // populated file.url and a NULL upload.url. The pipeline must short-circuit —
        // no multipart upload, no upload_info round-trip — and use file.url as the
        // final URL.
        bool uploadInvoked = false;
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"8677973"}},"status":200,"details":null}""",
            // Verbatim shape from the user's bug report (only relevant fields kept).
            """{"response":{"upload":{"upload_id":"reca9HCakwCX1XF0fxex185m23w4cg0g","url":null,"file":{"url":"https://www.rapidgator.net/file/abc/x.rar.html"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) =>
            {
                uploadInvoked = true;
                return Task.CompletedTask;
            });

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.False(uploadInvoked, "Bytes upload must be skipped on dedup hit");
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://www.rapidgator.net/file/abc/x.rar.html", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        // file/upload_info should NOT have been called (responses queue would still hold it).
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_WhenFileUploadResponseIsNotJson_AttemptFailedIncludesBodySnippet()
    {
        // Reproduces the real-world Rapidgator failure: login + folder/create succeed,
        // file/upload returns an HTML error page (Free-tier size cap, captcha, etc.) so
        // the envelope can't be deserialized. The fallback message must include a snippet
        // of the raw body so the user sees what actually came back.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"p","url":"u","nb_folders":0,"nb_files":0,"size_files":0,"created":1,"folders":[]}},"status":200,"details":null}""",
            "<html><body>Premium account required to upload files larger than 500 MB.</body></html>",
        });
        RapidgatorPipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failure = Assert.Single(events.OfType<AttemptFailed>());
        Assert.StartsWith("file/upload failed:", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("Premium account required", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenFileUploadResponseTruncatesLongBody_KeepsMessageBounded()
    {
        // Defensive: if the upstream returns a multi-kilobyte HTML page, the log message
        // must not blow up to that size — the helper trims to ~200 chars with an ellipsis.
        string longBody = new('A', 5000);
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"p","url":"u","nb_folders":0,"nb_files":0,"size_files":0,"created":1,"folders":[]}},"status":200,"details":null}""",
            longBody,
        });
        RapidgatorPipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failure = Assert.Single(events.OfType<AttemptFailed>());
        Assert.EndsWith("…", failure.Reason, StringComparison.Ordinal);
        Assert.True(failure.Reason.Length < 300, $"message length {failure.Reason.Length} exceeded the 200-char snippet cap");
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
