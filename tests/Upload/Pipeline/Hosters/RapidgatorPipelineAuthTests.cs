// <copyright file="RapidgatorPipelineAuthTests.cs" company="CSUploader">
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

public class RapidgatorPipelineAuthTests
{
    [Fact]
    public async Task RunAsync_FirstCall_LogsInAndYieldsAuthSucceeded()
    {
        Queue<string> responses = new(new[]
        {
            // /api/v2/user/login → token + primary folder id
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);
    }

    [Fact]
    public async Task RunAsync_SecondCallSameCredentials_ReusesAuthAndSkipsLogin()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));
        List<UploadEvent> second = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.DoesNotContain(second, e => e is AuthStarted);
    }

    [Fact]
    public async Task RunAsync_LoginFailsWithStatus401_YieldsAuthFailed()
    {
        Queue<string> responses = new(new[] { """{"response":null,"status":401,"details":"bad credentials"}""" });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
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

    private static async Task<List<UploadEvent>> CollectAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> result = [];
        await foreach (UploadEvent ev in stream)
        {
            result.Add(ev);
        }

        return result;
    }
}
