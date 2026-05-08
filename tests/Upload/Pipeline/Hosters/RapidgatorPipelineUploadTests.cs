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
