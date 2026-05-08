// <copyright file="RapidgatorPipelineFolderTests.cs" company="CSUploader">
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

public class RapidgatorPipelineFolderTests
{
    [Fact]
    public async Task RunAsync_AfterAuth_CreatesFolderAndProceedsToTransfer()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"package1","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
            if (ev is TransferStarted) break; // stop at the bridge into transfer; full transfer in Task 2.4
        }

        Assert.Contains(events, e => e is TransferStarted);
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
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
