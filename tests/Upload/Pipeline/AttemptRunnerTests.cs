// <copyright file="AttemptRunnerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
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
        => BuildRunnerWithProxy(ProxyChoice.Direct, pipelines);

    private static AttemptRunner BuildRunnerWithProxy(ProxyChoice? proxy, params IFileHosterPipeline[] pipelines)
    {
        DefaultFileHosterRegistry registry = new(pipelines);
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns(proxy);
        Mock<IHttpHandlerFactory> handlerFactory = new();
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxySource.Object, handlerFactory.Object);
    }

    [Fact]
    public async Task RunAsync_WhenProxySourceReturnsNull_RefusesUploadInsteadOfFallingBackToDirect()
    {
        // Use Proxies is on but no proxy is usable. The runner must NOT build a handler
        // and run the pipeline — that would silently ship bytes over a direct connection,
        // defeating the user's "use proxies" intent. Asserts both the failure event and
        // that no ProxyPicked / HandlerBuilt / TransferStarted slipped through.
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        AttemptRunner runner = BuildRunnerWithProxy(proxy: null, pipeline);

        List<UploadEvent> events = [];
        AttemptCompleted? terminal = null;
        runner.AttemptCompleted += (_, e) => terminal = e;
        await foreach (UploadEvent ev in runner.RunAsync(MakeInputs(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.DoesNotContain(events, e => e is ProxyPicked);
        Assert.DoesNotContain(events, e => e is HandlerBuilt);
        Assert.DoesNotContain(events, e => e is TransferStarted);

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Use Proxies is enabled", fail.Reason, StringComparison.Ordinal);

        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
        Assert.Equal(0, last.ProxyId);
        Assert.NotNull(terminal); // event subscribers (ProxyManager UI) get the terminal too
        Assert.False(terminal!.Success);
    }

    [Fact]
    public async Task RunAsync_WhenCredentialsHavePinnedProxyId_UsesPinnedProxyInsteadOfRotation()
    {
        // Captcha-gated hosters pin a proxy per cookie lifetime to prevent XFileSharing
        // from invalidating the cookie on IP-mismatch. AttemptRunner must look up the
        // pinned proxy by id (no rotation tick) when one is set on the credentials.
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        DefaultFileHosterRegistry registry = new([pipeline]);
        ProxyChoice rotated = new(99, null, "rotated");
        ProxyChoice pinned = new(42, null, "pinned");
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns(rotated);
        proxySource.Setup(s => s.GetById(42)).Returns(pinned);
        Mock<IHttpHandlerFactory> handlerFactory = new();
        ProxyChoice? handlerProxy = null;
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Callback<ProxyChoice, IAppLogger>((p, _) => handlerProxy = p)
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        AttemptRunner runner = new(registry, proxySource.Object, handlerFactory.Object);

        AttemptInputs inputs = MakeInputs() with
        {
            Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Rapidgator", Username = "u", Password = "p", PinnedProxyId = 42 },
        };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Same(pinned, handlerProxy);
        ProxyPicked picked = Assert.Single(events.OfType<ProxyPicked>());
        Assert.Equal(42, picked.Proxy.Id);
        // Rotation must NOT have been consumed.
        proxySource.Verify(s => s.Next(), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenPinnedProxyGone_RecoversByRotatingAndLetsPipelineReauth()
    {
        // Pinned proxy was disabled / deleted between sign-in and this attempt. The
        // runner falls back to the rotation so the pipeline can react to the proxy/pin
        // mismatch by re-signing in through the new proxy (the cookie was bound to the
        // pinned proxy's IP, so it's dead anyway). This is the "self-healing" path —
        // no user intervention required.
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        DefaultFileHosterRegistry registry = new([pipeline]);
        ProxyChoice rotated = new(7, null, "rotated");
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.GetById(42)).Returns((ProxyChoice?)null);
        proxySource.Setup(s => s.Next()).Returns(rotated);
        ProxyChoice? handlerProxy = null;
        Mock<IHttpHandlerFactory> handlerFactory = new();
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Callback<ProxyChoice, IAppLogger>((p, _) => handlerProxy = p)
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        AttemptRunner runner = new(registry, proxySource.Object, handlerFactory.Object);

        AttemptInputs inputs = MakeInputs() with
        {
            Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Rapidgator", Username = "u", Password = "p", PinnedProxyId = 42 },
        };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Same(rotated, handlerProxy);
        ProxyPicked picked = Assert.Single(events.OfType<ProxyPicked>());
        Assert.Equal(7, picked.Proxy.Id);
        Assert.DoesNotContain(events, e => e is AttemptFailed);
        proxySource.Verify(s => s.GetById(42), Times.Once);
        proxySource.Verify(s => s.Next(), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenPinnedProxyGoneAndRotationEmpty_FailsFastWithNoProxyMessage()
    {
        // Recovery falls back to rotation. If rotation is also empty (Use Proxies on
        // but no usable proxy), we still refuse — same path as a no-pin upload with an
        // empty rotation. Surfacing the standard "no usable proxy" reason rather than
        // a pin-specific one keeps the user's mental model consistent.
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        DefaultFileHosterRegistry registry = new([pipeline]);
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.GetById(42)).Returns((ProxyChoice?)null);
        proxySource.Setup(s => s.Next()).Returns((ProxyChoice?)null);
        Mock<IHttpHandlerFactory> handlerFactory = new();
        AttemptRunner runner = new(registry, proxySource.Object, handlerFactory.Object);

        AttemptInputs inputs = MakeInputs() with
        {
            Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Rapidgator", Username = "u", Password = "p", PinnedProxyId = 42 },
        };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("Use Proxies is enabled", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is HandlerBuilt);
    }

    [Fact]
    public async Task RunAsync_WhenBodyAbortThenSuccess_RetriesAndCompletesSuccessfully()
    {
        // Attempt 1 throws a body-incomplete transport fault (HttpClient wraps
        // UploadBodyTransferException). Re-sending is safe because the body never finished,
        // so AttemptRunner re-runs the whole pipeline; attempt 2 succeeds.
        ProgrammablePipeline pipeline = new(attempt =>
            attempt == 1
                ? PipelineBehavior.ThrowAfterStarted(
                    new HttpRequestException("Error while copying content to a stream.", new UploadBodyTransferException(new IOException("reset"))))
                : PipelineBehavior.Succeed("https://x/y"));
        AttemptRunner runner = BuildRunner(pipeline);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(MakeInputs(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(2, pipeline.Invocations);
        Assert.Contains(events, e => e is TransferCompleted);
        Assert.DoesNotContain(events, e => e is AttemptFailed);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.True(last.Success);
        Assert.Equal("https://x/y", last.FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenBodyAbortEveryAttempt_ExhaustsRetriesAndFails()
    {
        // The body-incomplete fault recurs on every attempt. After MaxUploadAttempts the
        // runner must surface a terminal AttemptFailed (NOT throw) so the row shows Failed,
        // not Cancelled.
        ProgrammablePipeline pipeline = new(_ =>
            PipelineBehavior.ThrowAfterStarted(
                new HttpRequestException("Error while copying content to a stream.", new UploadBodyTransferException(new IOException("reset")))));
        AttemptRunner runner = BuildRunner(pipeline);

        List<UploadEvent> events = [];
        // Must NOT throw — exhausted retries are a terminal Failed, not a cancellation.
        await foreach (UploadEvent ev in runner.RunAsync(MakeInputs(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(3, pipeline.Invocations);
        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("after 3 attempts", fail.Reason, StringComparison.Ordinal);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
    }

    [Fact]
    public async Task RunAsync_WhenNonRetryableTransportFault_FailsImmediatelyWithoutRetry()
    {
        // A plain transport fault (not a body abort) is NOT safe to blindly re-send — the
        // body may have been fully delivered. Fail immediately on the first attempt.
        ProgrammablePipeline pipeline = new(_ =>
            PipelineBehavior.ThrowAfterStarted(new HttpRequestException("500")));
        AttemptRunner runner = BuildRunner(pipeline);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(MakeInputs(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(1, pipeline.Invocations);
        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.DoesNotContain("after", fail.Reason, StringComparison.Ordinal);
        Assert.Equal("500", fail.Reason);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
    }

    [Fact]
    public async Task RunAsync_WhenPipelineYieldsServerVerdictFailure_ForwardsWithoutRetry()
    {
        // A server verdict is a parsed bad response yielded as AttemptFailed (no throw). It's
        // terminal — the runner forwards it and does not retry.
        ProgrammablePipeline pipeline = new(_ =>
            PipelineBehavior.YieldServerFailure("server said no"));
        AttemptRunner runner = BuildRunner(pipeline);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(MakeInputs(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(1, pipeline.Invocations);
        AttemptFailed fail = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Equal("server said no", fail.Reason);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
    }

    [Fact]
    public async Task RunAsync_WhenUserCancels_ThrowsOperationCanceledAndDoesNotRetry()
    {
        // The pipeline throws OperationCanceledException because the token is cancelled.
        // The runner must propagate a cancellation (scheduler keys "Cancelled" off it) and
        // must NOT retry.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        ProgrammablePipeline pipeline = new(_ =>
            PipelineBehavior.ThrowAfterStarted(new OperationCanceledException()));
        AttemptRunner runner = BuildRunner(pipeline);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (UploadEvent _ in runner.RunAsync(MakeInputs(), cts.Token))
            {
                /* drain */
            }
        });

        Assert.Equal(1, pipeline.Invocations);
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

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "Login OK"));

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

    /// <summary>
    /// What a <see cref="ProgrammablePipeline"/> attempt does after yielding TransferStarted.
    /// </summary>
    private sealed record PipelineBehavior(
        string? SuccessUrl,
        Exception? Throw,
        string? ServerFailureReason)
    {
        public static PipelineBehavior Succeed(string url) => new(url, null, null);

        public static PipelineBehavior ThrowAfterStarted(Exception ex) => new(null, ex, null);

        public static PipelineBehavior YieldServerFailure(string reason) => new(null, null, reason);
    }

    /// <summary>
    /// A pipeline whose per-attempt behavior is chosen by a factory keyed off the 1-based
    /// invocation number, so tests can make attempt 1 throw and attempt 2 succeed, etc.
    /// </summary>
    private sealed class ProgrammablePipeline(Func<int, PipelineBehavior> behaviorFor) : IFileHosterPipeline
    {
        public int Invocations { get; private set; }

        public string Name => "Rapidgator";

        public bool RequiresHashingBeforeUpload => false;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "Login OK"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            int attempt = ++this.Invocations;
            PipelineBehavior behavior = behaviorFor(attempt);

            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();

            if (behavior.Throw is not null)
            {
                throw behavior.Throw;
            }

            if (behavior.ServerFailureReason is not null)
            {
                yield return new AttemptFailed(behavior.ServerFailureReason, null);
                yield break;
            }

            yield return new TransferCompleted(behavior.SuccessUrl!);
        }
    }
}
