// <copyright file="AttemptRunnerIntegrationTests.cs" company="CSUploader">
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

public class AttemptRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_OnSuccessWithProxy_RaisesAttemptCompletedWithProxyId()
    {
        FakePipeline pipeline = new();
        DefaultFileHosterRegistry registry = new([pipeline]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(new ProxyChoice(42, null, "http://x:1"));
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()));
        AttemptRunner runner = new(registry, proxy.Object, hf.Object);

        AttemptCompleted? captured = null;
        runner.AttemptCompleted += (_, e) => captured = e;

        AttemptInputs inputs = new()
        {
            FilePath = "x.zip",
            FileName = "x.zip",
            FileSize = 100,
            HosterName = "Fake",
            Credentials = new FileHosterLoginDto(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
        };

        await foreach (UploadEvent _ in runner.RunAsync(inputs, CancellationToken.None)) { /* drain */ }

        Assert.NotNull(captured);
        Assert.True(captured!.Success);
        Assert.Equal(42, captured.ProxyId);
    }

    private sealed class FakePipeline : IFileHosterPipeline
    {
        public string Name => "Fake";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;
        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TransferStarted(ctx.FileSize);
            yield return new TransferCompleted("https://done");
        }
    }
}
