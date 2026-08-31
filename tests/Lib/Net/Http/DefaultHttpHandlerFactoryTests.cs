// <copyright file="DefaultHttpHandlerFactoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

public class DefaultHttpHandlerFactoryTests
{
    [Fact]
    public void Create_ReturnsNonNullHandler()
    {
        AppSettings settings = new();
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.NotNull(handler); // by signature; this asserts type only
    }

#if DEBUG
    [Fact]
    public void Create_BakesMockServerSnapshotFromCurrentSettings()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://mock:9000" };
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.True(handler.MockServerSnapshot.Enabled);
        Assert.Equal("http://mock:9000", handler.MockServerSnapshot.BaseUrl);
    }
#else
    [Fact]
    public void Create_BakesADisabledMockServerSnapshot_OutsideADebugBuild()
    {
        // The factory is the only place the snapshot is taken, so it is also where the release
        // guard in MockServerConfig.FromAppSettings has to hold: a persisted flag reaching a
        // shipped build must not send handler traffic to localhost. See MockServerConfigTests.
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://mock:9000" };
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.False(handler.MockServerSnapshot.Enabled);
        Assert.Equal(string.Empty, handler.MockServerSnapshot.BaseUrl);
    }
#endif

    [Fact]
    public void Create_AttachesABrowserShapedUserAgentByDefault()
    {
        // Some XFileSharing-family backends silently drop traffic with no UA and Cloudflare
        // serves a JS challenge page. The factory is the only place to add the UA so every
        // HttpHandler instance picks it up — regressing this would break uploads silently.
        AppSettings settings = new();
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        string ua = string.Join(" ", handler.ClientForTesting.DefaultRequestHeaders.UserAgent);
        Assert.Contains("Mozilla/5.0", ua, StringComparison.Ordinal);
        Assert.Contains("Chrome/", ua, StringComparison.Ordinal);
    }
}
