// <copyright file="HosterCredentialModesTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Tests.Upload;

public class HosterCredentialModesTests
{
    // Drift-guard rosters (Phase 5 prep item 4): the hoist made these the SINGLE source of truth for
    // both heads, so the pinned membership is asserted here — a reclassification or accidental removal
    // in HosterCredentialModes trips the matching test instead of silently changing an editor's UI.
    private static readonly string[] KnownApiKeyHosters =
        ["Ex-Load", "KatFile", "Hexload", "Hxfile", "FileBoom", "HitFile", "Keep2Share", "TezFiles", "NitroFlare", "Ufile"];

    private static readonly string[] KnownSessionCookieHosters = ["Isracloud"];

    private static readonly string[] KnownClassicHosters = ["Rapidgator", "Catbox", "MediaFire", "Buzzheavier"];

    [Theory]
    [InlineData("KatFile", HosterCredentialMode.ApiKey)]
    [InlineData("NitroFlare", HosterCredentialMode.ApiKey)]
    [InlineData("Isracloud", HosterCredentialMode.SessionCookie)]
    [InlineData("Rapidgator", HosterCredentialMode.UsernamePassword)]
    [InlineData("SomeBrandNewHoster", HosterCredentialMode.UsernamePassword)]
    public void GetMode_ReturnsExpectedMode(string hoster, HosterCredentialMode expected)
        => Assert.Equal(expected, HosterCredentialModes.GetMode(hoster));

    [Fact]
    public void GetMode_Null_FallsBackToUsernamePassword()
        => Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode(null));

    [Theory]
    [InlineData("katfile")]
    [InlineData("KATFILE")]
    [InlineData("KatFile")]
    public void GetMode_IsCaseInsensitive_ForApiKeyHosters(string hoster)
        => Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode(hoster));

    [Theory]
    [InlineData("isracloud")]
    [InlineData("ISRACLOUD")]
    public void GetMode_IsCaseInsensitive_ForSessionCookieHosters(string hoster)
        => Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode(hoster));

    [Fact]
    public void GetMode_ApiKeyRoster_ClassifiesEveryMemberAsApiKey()
        => Assert.All(KnownApiKeyHosters, h => Assert.Equal(HosterCredentialMode.ApiKey, HosterCredentialModes.GetMode(h)));

    [Fact]
    public void GetMode_SessionCookieRoster_ClassifiesEveryMemberAsSessionCookie()
        => Assert.All(KnownSessionCookieHosters, h => Assert.Equal(HosterCredentialMode.SessionCookie, HosterCredentialModes.GetMode(h)));

    [Fact]
    public void GetMode_ClassicHosters_FallBackToUsernamePassword()
        => Assert.All(KnownClassicHosters, h => Assert.Equal(HosterCredentialMode.UsernamePassword, HosterCredentialModes.GetMode(h)));

    [Fact]
    public void IsApiKeyHoster_TracksGetMode()
    {
        Assert.True(HosterCredentialModes.IsApiKeyHoster("KatFile"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster("Isracloud"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster("Rapidgator"));
        Assert.False(HosterCredentialModes.IsApiKeyHoster(null));
    }

    [Fact]
    public void IsSessionCookieHoster_TracksGetMode()
    {
        Assert.True(HosterCredentialModes.IsSessionCookieHoster("Isracloud"));
        Assert.False(HosterCredentialModes.IsSessionCookieHoster("KatFile"));
        Assert.False(HosterCredentialModes.IsSessionCookieHoster("Rapidgator"));
        Assert.False(HosterCredentialModes.IsSessionCookieHoster(null));
    }

    [Theory]
    [InlineData("KatFile", true)]      // ApiKey family
    [InlineData("Isracloud", true)]    // SessionCookie family
    [InlineData("Rapidgator", false)]  // classic U/P
    [InlineData(null, false)]
    public void IsWebViewSignInHoster_IsTrueForEitherSignInFamily(string? hoster, bool expected)
        => Assert.Equal(expected, HosterCredentialModes.IsWebViewSignInHoster(hoster));
}
