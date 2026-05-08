// <copyright file="HttpHandlerMockRewriteTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using Moq;
using System.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

public class HttpHandlerMockRewriteTests
{
    [Fact]
    public void Ctor_WithMockServerConfig_DoesNotReadAppSettingsCurrent()
    {
        // The new ctor takes the snapshot directly; AppSettings.Current is irrelevant here.
        MockServerConfig snap = new(true, "http://localhost:9999");
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), proxyDescription: null, mockServer: snap);

        Assert.Equal(snap, handler.MockServerSnapshot);
    }
}
