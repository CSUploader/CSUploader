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

    [Fact]
    public void Create_BakesMockServerSnapshotFromCurrentSettings()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://mock:9000" };
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.True(handler.MockServerSnapshot.Enabled);
        Assert.Equal("http://mock:9000", handler.MockServerSnapshot.BaseUrl);
    }
}
