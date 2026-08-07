// <copyright file="HosterCredentialModesTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CSUploader.Tests.Upload;

public class HosterCredentialModesTests
{
    // Drift-guard rosters (Phase 5 prep item 4): the hoist made these the SINGLE source of truth for
    // both heads, so the pinned membership is asserted here — a reclassification or accidental removal
    // in HosterCredentialModes trips the matching test instead of silently changing an editor's UI.
    private static readonly string[] KnownApiKeyHosters =
        ["Buzzheavier", "Ex-Load", "KatFile", "Hexload", "Hxfile", "FileBoom", "HitFile", "Keep2Share", "TezFiles", "NitroFlare", "Ufile"];

    private static readonly string[] KnownSessionCookieHosters = ["Isracloud"];

    private static readonly string[] KnownClassicHosters = ["Rapidgator", "Catbox", "MediaFire"];

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

    // Registration cross-check (Phase 5 Task 7 review addendum): every credential-mode roster member
    // must be an actual registered FileHosterClient.FileHosters key. The roster names are looked up by
    // exact string (the mode sets use OrdinalIgnoreCase, but the registry is StringComparer.Ordinal), so
    // a casing typo or a rename in the registry that isn't mirrored here would classify a hoster whose
    // name the app never resolves — this catches that drift the GetMode tests above cannot.
    [Fact]
    public void ApiKeyHosterRoster_AreAllRegisteredFileHosters()
        => Assert.All(KnownApiKeyHosters, h => Assert.True(
            FileHosterClient.FileHosters.ContainsKey(h),
            $"'{h}' is in the ApiKey roster but is not a registered FileHosterClient.FileHosters key."));

    [Fact]
    public void SessionCookieHosterRoster_AreAllRegisteredFileHosters()
        => Assert.All(KnownSessionCookieHosters, h => Assert.True(
            FileHosterClient.FileHosters.ContainsKey(h),
            $"'{h}' is in the SessionCookie roster but is not a registered FileHosterClient.FileHosters key."));

    /// <summary>
    /// Every hoster whose credential is a captured session MUST be able to re-check that session
    /// without opening the sign-in window. <see cref="AccountVerifier"/> routes on the interface alone,
    /// so a hoster that doesn't implement it re-runs the interactive check — which is what made adding
    /// an UpZur account ask for sign-in TWICE: once when the user pressed Sign in, and again when the
    /// save ran its verification pass. This asserts the contract for the whole family rather than the
    /// one host that surfaced it.
    /// </summary>
    [Fact]
    public void EverySessionCookieHoster_CanBeRecheckedWithoutTheSignInWindow()
    {
        ServiceCollection services = new();
        services.AddCoreServices(AppContext.BaseDirectory);
        services.AddSingleton(Mock.Of<IInteractiveAuthService>());
        services.AddSingleton(Mock.Of<IToastNotificationService>());
        using ServiceProvider provider = services.BuildServiceProvider();

        IFileHosterPipeline[] pipelines = [.. provider.GetServices<IFileHosterPipeline>()];
        string[] sessionCookieHosters = [.. FileHosterClient.FileHosters.Keys.Where(HosterCredentialModes.IsSessionCookieHoster)];

        // Guards the guard: if the roster is ever emptied this test must fail, not vacuously pass.
        Assert.NotEmpty(sessionCookieHosters);

        Assert.All(sessionCookieHosters, name =>
        {
            IFileHosterPipeline? pipeline = pipelines.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            Assert.True(pipeline is not null, $"'{name}' is a session-cookie hoster with no registered pipeline.");
            Assert.True(
                pipeline is ISessionRefreshablePipeline,
                $"'{name}' stores a session cookie as its only credential but cannot re-check it offline, "
                + "so every save/refresh will reopen the sign-in window.");
        });
    }
}
