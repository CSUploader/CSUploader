// <copyright file="RapidgatorPipelineRetryTests.cs" company="CSUploader">
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

public class RapidgatorPipelineRetryTests
{
    [Fact]
    public async Task FolderCreate401_InvalidatesCache_NextAttemptLogsInAgain()
    {
        Queue<string> responses = new(new[]
        {
            // attempt 1: login OK
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            // attempt 1: folder/create returns 401
            """{"response":null,"status":401,"details":"unauthorized"}""",
            // attempt 2: login again (cache was invalidated)
            """{"response":{"token":"TOK2","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx1 = MakeContext();
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx1, CancellationToken.None))
        { /* drain */ }

        AttemptContext ctx2 = MakeContext();
        List<UploadEvent> attempt2 = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx2, CancellationToken.None))
        {
            attempt2.Add(ev);
            if (ev is AuthSucceeded)
            {
                break;
            }
        }

        Assert.Contains(attempt2, e => e is AuthStarted);
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
