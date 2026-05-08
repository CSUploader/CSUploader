// <copyright file="AttemptRunnerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class AttemptRunnerTests
{
    [Fact]
    public async Task RunAsync_EmitsProxyPicked_HandlerBuilt_PipelineEvents_ThenAttemptCompleted()
    {
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        AttemptRunner runner = BuildRunner(pipeline);
        AttemptInputs inputs = MakeInputs();

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.IsType<ProxyPicked>(events[0]);
        Assert.IsType<HandlerBuilt>(events[1]);
        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferCompleted);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.True(last.Success);
        Assert.Equal("https://x/y", last.FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenHosterUnregistered_EmitsAttemptFailedAndAttemptCompletedFalse()
    {
        AttemptRunner runner = BuildRunner(pipelines: []);
        AttemptInputs inputs = MakeInputs() with { HosterName = "UnknownHoster" };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e is AttemptFailed);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
    }

    private static AttemptRunner BuildRunner(params IFileHosterPipeline[] pipelines)
    {
        DefaultFileHosterRegistry registry = new(pipelines);
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> handlerFactory = new();
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()));
        return new AttemptRunner(registry, proxySource.Object, handlerFactory.Object);
    }

    private static AttemptInputs MakeInputs() => new()
    {
        FilePath = @"C:\does-not-matter\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "abcd",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
    };

    private sealed class FakeHosterPipeline(bool success, string fileUrl) : IFileHosterPipeline
    {
        public string Name => "Rapidgator";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            if (success)
            {
                yield return new TransferCompleted(fileUrl);
            }
            else
            {
                yield return new AttemptFailed("synthetic failure", null);
            }
        }
    }
}
