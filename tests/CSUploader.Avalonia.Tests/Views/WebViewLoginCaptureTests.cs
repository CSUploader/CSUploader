// <copyright file="WebViewLoginCaptureTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Pure cookie/probe selection logic extracted from the WPF WebViewLoginWindow (Phase 8 Task 1). A wrong
/// session-cookie pick would hand the pipeline a stale/anonymous session (the ex-load / Hxfile findings), so
/// this is the correctness-critical half — and the only half unit-testable without a live WebView2.
/// </summary>
public class WebViewLoginCaptureTests
{
    [Fact]
    public void SelectCookies_PicksNamedSession_IgnoresEmptyValues()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("other", "x"), ("xfss", ""), ("xfss", "SESSION")], // first xfss empty → skipped
            cookieName: "xfss", usernameCookieName: null, additionalCookieNames: null, cookieValueValidator: null);

        Assert.Equal("SESSION", result.SessionValue);
        Assert.Null(result.UsernameValue);
        Assert.Null(result.AdditionalCookies);
    }

    [Fact]
    public void SelectCookies_NoMatch_ReturnsNullSession()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("a", "1"), ("b", "2")], cookieName: "xfss", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: null);

        Assert.Null(result.SessionValue);
    }

    [Fact]
    public void SelectCookies_ValidatorRejects_ReturnsNullSession()
    {
        // FileBoom-shape: the cookie is present pre-login too; the validator gates the post-login value.
        var result = WebViewLoginCapture.SelectCookies(
            [("accessToken", "bootstrap")], cookieName: "accessToken", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: v => v == "real");

        Assert.Null(result.SessionValue);
    }

    [Fact]
    public void SelectCookies_ValidatorAccepts_ReturnsValue()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("accessToken", "real")], cookieName: "accessToken", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: v => v == "real");

        Assert.Equal("real", result.SessionValue);
    }

    [Fact]
    public void SelectCookies_CapturesUsernameAndAdditional()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("xfss", "S"), ("username", "me@x.com"), ("pcId", "P1"), ("noise", "n")],
            cookieName: "xfss", usernameCookieName: "username", additionalCookieNames: ["pcId"],
            cookieValueValidator: null);

        Assert.Equal("S", result.SessionValue);
        Assert.Equal("me@x.com", result.UsernameValue);
        Assert.NotNull(result.AdditionalCookies);
        Assert.Equal("P1", result.AdditionalCookies!["pcId"]);
        Assert.False(result.AdditionalCookies.ContainsKey("noise"));
    }

    [Fact]
    public void SelectCookies_NameInSessionAndAdditional_LandsOnlyAsSession()
    {
        // 'xfss' is BOTH the session name and listed as additional. The else-if chain must route it to
        // session ONLY; a regression to separate `if`s would ALSO copy it into AdditionalCookies. (pcId is a
        // genuine additional so AdditionalCookies is non-null — the ContainsKey assertion is non-vacuous.)
        var result = WebViewLoginCapture.SelectCookies(
            [("xfss", "S"), ("pcId", "P1")],
            cookieName: "xfss", usernameCookieName: null, additionalCookieNames: ["xfss", "pcId"],
            cookieValueValidator: null);

        Assert.Equal("S", result.SessionValue);
        Assert.NotNull(result.AdditionalCookies);
        Assert.False(result.AdditionalCookies!.ContainsKey("xfss")); // session name must NOT double-land
        Assert.Equal("P1", result.AdditionalCookies["pcId"]);
    }

    [Fact]
    public void SelectCookies_LocksOnFirstMatch_DoesNotRetryLaterCandidate()
    {
        // The loop locks onto the FIRST cookie of the session name and never revisits within one pass — even
        // when a later same-named cookie WOULD pass the validator (the validator rejects the first). Matches
        // the WPF single-pass capture; the poll re-reads the jar on the next tick rather than scanning past.
        var result = WebViewLoginCapture.SelectCookies(
            [("accessToken", "bootstrap-reject"), ("accessToken", "real-would-pass")],
            cookieName: "accessToken", usernameCookieName: null, additionalCookieNames: null,
            cookieValueValidator: v => v == "real-would-pass");

        Assert.Null(result.SessionValue);
    }

    [Fact]
    public void TryParseJsonString_UnwrapsQuotedString()
        => Assert.Equal("42id", WebViewLoginCapture.TryParseJsonString("\"42id\""));

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{not a string}")]
    public void TryParseJsonString_ReturnsNullForNonString(string? raw)
        => Assert.Null(WebViewLoginCapture.TryParseJsonString(raw));

    [Fact]
    public void BuildCookieHeader_JoinsNonEmptyPairs()
        => Assert.Equal("a=1; b=2", WebViewLoginCapture.BuildCookieHeader([("a", "1"), ("skip", ""), ("b", "2")]));

    [Fact]
    public void BuildCookieHeader_EmptyJar_ReturnsNull()
        => Assert.Null(WebViewLoginCapture.BuildCookieHeader([]));
}
