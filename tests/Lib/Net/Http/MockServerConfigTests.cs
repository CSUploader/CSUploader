// <copyright file="MockServerConfigTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// <see cref="MockServerConfig.FromAppSettings"/> has two behaviours, one per build configuration,
/// and this suite is built in BOTH — Debug locally, Release by the release workflow's gate — so
/// each is asserted under the configuration that actually has it. Asserting only the Debug one
/// unconditionally is what broke the Release gate once.
/// </summary>
public class MockServerConfigTests
{
#if DEBUG
    [Fact]
    public void FromAppSettings_CapturesEnabledAndBaseUrl()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://localhost:8080" };

        var snap = MockServerConfig.FromAppSettings(settings);

        Assert.True(snap.Enabled);
        Assert.Equal("http://localhost:8080", snap.BaseUrl);
    }
#else
    [Fact]
    public void FromAppSettings_IgnoresAPersistedFlag_OutsideADebugBuild()
    {
        // The guard the DEBUG-only developer switch depends on: Debug and release builds on one
        // machine share a settings database, so a flag left on after a development session outlives
        // the switch that set it. Were it honoured here it would redirect every file-hoster request
        // to localhost with no UI left to turn it back off.
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://localhost:8080" };

        var snap = MockServerConfig.FromAppSettings(settings);

        Assert.False(snap.Enabled);
        Assert.Equal(string.Empty, snap.BaseUrl);
    }
#endif

    [Fact]
    public void Disabled_HasEnabledFalseAndEmptyBaseUrl()
    {
        Assert.False(MockServerConfig.Disabled.Enabled);
        Assert.Equal(string.Empty, MockServerConfig.Disabled.BaseUrl);
    }
}
