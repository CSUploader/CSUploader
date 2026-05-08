// <copyright file="MockServerConfigTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload;

namespace CSUploader.Tests.Lib.Net.Http;

public class MockServerConfigTests
{
    [Fact]
    public void FromAppSettings_CapturesEnabledAndBaseUrl()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://localhost:8080" };

        MockServerConfig snap = MockServerConfig.FromAppSettings(settings);

        Assert.True(snap.Enabled);
        Assert.Equal("http://localhost:8080", snap.BaseUrl);
    }

    [Fact]
    public void Disabled_HasEnabledFalseAndEmptyBaseUrl()
    {
        Assert.False(MockServerConfig.Disabled.Enabled);
        Assert.Equal(string.Empty, MockServerConfig.Disabled.BaseUrl);
    }
}
