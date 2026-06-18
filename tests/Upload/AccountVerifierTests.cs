// <copyright file="AccountVerifierTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload;

public class AccountVerifierTests
{
    private static Mock<IProxySource> DirectProxySource()
    {
        Mock<IProxySource> src = new();
        src.Setup(s => s.Next()).Returns(ProxyChoice.Direct);
        return src;
    }

    [Fact]
    public async Task CheckAsync_UnknownHoster_ReturnsNotImplemented()
    {
        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("Unknown")).Returns((IFileHosterPipeline?)null);

        AccountVerifier verifier = new(
            registry.Object, Mock.Of<IHttpHandlerFactory>(), DirectProxySource().Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("Unknown", "u", "p");

        Assert.False(result.IsValid);
        Assert.Contains("not implemented", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_PipelineThrows_ReturnsInvalidWithErrorMessage()
    {
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.Setup(p => p.CheckAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network down"));

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("Rapidgator")).Returns(pipeline.Object);

        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AccountVerifier verifier = new(
            registry.Object, factory.Object, DirectProxySource().Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("Rapidgator", "u", "p");

        Assert.False(result.IsValid);
        Assert.Contains("network down", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_UsesProxyFromProxySource()
    {
        // Account checks now share the upload proxy rotation; whatever IProxySource
        // hands back must reach the HttpHandlerFactory unchanged.
        AccountCheckResult expected = new(true, AccountType.Premium, "Premium until 2030-06-01");
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.Setup(p => p.CheckAccountAsync("u", "p", It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("Rapidgator")).Returns(pipeline.Object);

        ProxyChoice routed = new(42, new System.Net.WebProxy("http://1.2.3.4:8080"), "http://1.2.3.4:8080");
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns(routed);

        ProxyChoice? capturedProxy = null;
        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Callback<ProxyChoice, IAppLogger>((proxy, _) => capturedProxy = proxy)
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AccountVerifier verifier = new(registry.Object, factory.Object, proxySource.Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("Rapidgator", "u", "p");

        Assert.Same(expected, result);
        Assert.Same(routed, capturedProxy);
    }

    [Fact]
    public async Task CheckAsync_ProxySourceReturnsNull_RefusesCheckAndDoesNotInvokePipeline()
    {
        // Use Proxies is on but the rotation is empty — account check must NOT silently
        // go direct (would leak the user's IP to the login endpoint). Asserts the pipeline
        // never gets called.
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.Setup(p => p.CheckAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountCheckResult(true, AccountType.Free, "should never run"));

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("Rapidgator")).Returns(pipeline.Object);

        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns((ProxyChoice?)null);

        Mock<IHttpHandlerFactory> factory = new();

        AccountVerifier verifier = new(registry.Object, factory.Object, proxySource.Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("Rapidgator", "u", "p");

        Assert.False(result.IsValid);
        Assert.Contains("Use Proxies is enabled", result.Message, StringComparison.Ordinal);
        pipeline.Verify(
            p => p.CheckAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()),
            Times.Never);
        factory.Verify(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WithSessionCookieAndRefreshablePipeline_RoutesToRefreshAccountAsync()
    {
        // A "Check / Refresh" with a captured session on a session-refreshable pipeline (HitFile)
        // must re-read server-side data via RefreshAccountAsync — NOT re-open the interactive
        // CheckAccountAsync (which would pop the WebView).
        AccountCheckResult refreshed = new(true, AccountType.Free, "storage refreshed");
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.As<ISessionRefreshablePipeline>()
            .Setup(p => p.RefreshAccountAsync("appid", "cookie=1", It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("HitFile")).Returns(pipeline.Object);

        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AccountVerifier verifier = new(registry.Object, factory.Object, DirectProxySource().Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("HitFile", "u", "p", apiKey: "appid", sessionCookie: "cookie=1");

        Assert.Same(refreshed, result);
        pipeline.Verify(
            p => p.CheckAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAsync_NoSessionCookie_StaysOnCheckAccountAsync()
    {
        // The initial sign-in (no captured cookie yet) must go through CheckAccountAsync even on a
        // session-refreshable pipeline — there's no session to re-read with.
        AccountCheckResult signedIn = new(true, AccountType.Free, "signed in");
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.Setup(p => p.CheckAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signedIn);
        Mock<ISessionRefreshablePipeline> refreshable = pipeline.As<ISessionRefreshablePipeline>();

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("HitFile")).Returns(pipeline.Object);

        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AccountVerifier verifier = new(registry.Object, factory.Object, DirectProxySource().Object, Mock.Of<IAppLogger>());

        AccountCheckResult result = await verifier.CheckAsync("HitFile", "u", "p", apiKey: null, sessionCookie: null);

        Assert.Same(signedIn, result);
        refreshable.Verify(
            p => p.RefreshAccountAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAsync_ProxySourceReturnsDirect_HandlerBuiltDirect()
    {
        // ProxyManager.Next() returns ProxyChoice.Direct when "Use proxies for uploads"
        // is off or no enabled proxies exist — account check must respect that.
        AccountCheckResult expected = new(true, AccountType.Free, "Free");
        Mock<IFileHosterPipeline> pipeline = new();
        pipeline.Setup(p => p.CheckAccountAsync("u", "p", It.IsAny<string?>(), It.IsAny<HttpHandler>(), It.IsAny<ProxyChoice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        Mock<IFileHosterRegistry> registry = new();
        registry.Setup(r => r.Find("Rapidgator")).Returns(pipeline.Object);

        ProxyChoice? capturedProxy = null;
        Mock<IHttpHandlerFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Callback<ProxyChoice, IAppLogger>((proxy, _) => capturedProxy = proxy)
            .Returns(new HttpHandler(new System.Net.Http.HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));

        AccountVerifier verifier = new(registry.Object, factory.Object, DirectProxySource().Object, Mock.Of<IAppLogger>());

        await verifier.CheckAsync("Rapidgator", "u", "p");

        Assert.NotNull(capturedProxy);
        Assert.Null(capturedProxy!.WebProxy);
        Assert.Equal("(direct)", capturedProxy.Description);
    }
}
