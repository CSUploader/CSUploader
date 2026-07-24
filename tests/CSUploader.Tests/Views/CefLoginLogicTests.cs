// <copyright file="CefLoginLogicTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Views;

namespace CSUploader.Tests.Views;

/// <summary>
/// Head unit tests for the pure CefGlue sign-in seams (Task 3). These are engine-free — they exercise the
/// JS-probe shim, the probe-script CEF wrapper, the delete-before-navigate ordering, and the cookie-projection
/// contract WITHOUT booting CEF (which needs a display). They run on the Windows head suite (the seams
/// deliberately compile on both targets), so they raise the head test count; the CEF-type-touching paths are
/// human-verified on Linux.
/// </summary>
public sealed class CefLoginLogicTests
{
    // ---- CefProbeResult.TryProbeComplete: empty/null ⇒ not complete, non-empty ⇒ complete (raw value) ------

    [Fact]
    public void TryProbeComplete_Null_IsNotComplete()
    {
        bool complete = CefProbeResult.TryProbeComplete(null, out string value);

        Assert.False(complete);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryProbeComplete_Empty_IsNotComplete()
    {
        bool complete = CefProbeResult.TryProbeComplete(string.Empty, out string value);

        Assert.False(complete);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryProbeComplete_NonEmpty_IsCompleteWithRawValue()
    {
        bool complete = CefProbeResult.TryProbeComplete("appId-123", out string value);

        Assert.True(complete);
        Assert.Equal("appId-123", value);
    }

    [Fact]
    public void TryProbeComplete_ReturnsRawValue_DoesNotJsonDecode()
    {
        // CEF's EvaluateJavaScript<string> hands back the raw string (design: TryParseJsonString is BYPASSED
        // on the CEF path). A JSON-shaped probe payload must flow through byte-for-byte — NOT be unquoted the
        // way WebView2's ExecuteScriptAsync result is. This is the highest API-shape divergence, so pin it.
        const string raw = "{\"appId\":\"x\",\"usedBytes\":10}";

        bool complete = CefProbeResult.TryProbeComplete(raw, out string value);

        Assert.True(complete);
        Assert.Equal(raw, value); // identical: no JSON string-decoding applied
    }

    [Fact]
    public void TryProbeComplete_LiteralNullString_IsCompleteNotSpecialCased()
    {
        // Documents the deliberate divergence: WebViewLoginCapture.TryParseJsonString maps the literal "null"
        // to C# null (not signed in), but the CEF shim does NOT re-run it — a JS probe returning JS null comes
        // back as C# null (handled by the null case above), whereas the 4-char string "null" is a real
        // non-empty value and completes. No real probe returns the string "null"; this pins the raw contract.
        bool complete = CefProbeResult.TryProbeComplete("null", out string value);

        Assert.True(complete);
        Assert.Equal("null", value);
    }

    // ---- CefProbeResult.WrapProbeScript: expression-statement probe ⇒ a returned value under CEF -----------

    [Fact]
    public void WrapProbeScript_IifeExpression_BecomesReturnedValue()
    {
        // CefGlue wraps evaluated code as `function() { <script> }`, so an expression statement yields
        // undefined. Wrapping turns `(...)( );` into `return (...)()` so the wrapper function returns the value.
        const string probe = "(function () { return window.__x; })();";

        string? wrapped = CefProbeResult.WrapProbeScript(probe);

        Assert.Equal("return ((function () { return window.__x; })());", wrapped);
    }

    [Fact]
    public void WrapProbeScript_StripsExactlyOneTrailingSemicolonAndWhitespace()
    {
        string? wrapped = CefProbeResult.WrapProbeScript("  doThing()  ;  \n");

        // One trailing ';' + surrounding whitespace removed; the value is returned. A second ';' (empty
        // statement) is not the shape any probe uses, so only one is stripped.
        Assert.Equal("return (doThing());", wrapped);
    }

    [Fact]
    public void WrapProbeScript_NoTrailingSemicolon_StillReturns()
    {
        string? wrapped = CefProbeResult.WrapProbeScript("42");

        Assert.Equal("return (42);", wrapped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WrapProbeScript_NullOrBlank_PassesThrough(string? probe)
    {
        Assert.Equal(probe, CefProbeResult.WrapProbeScript(probe));
    }

    // ---- WebViewLoginCapture.IsOnLoginPage: gate cookie capture until the browser leaves the login page -----

    [Theory]
    [InlineData(null)]                                                  // no navigation yet → still on login page
    [InlineData("")]                                                    // ditto
    [InlineData("https://katfile.biz/login.html")]                      // exact login page
    [InlineData("https://katfile.biz/login.html?foo=bar")]             // login page with a query
    [InlineData("https://KATFILE.biz/Login.html")]                      // host + path case-insensitive
    public void IsOnLoginPage_StillOnLoginPage_ReturnsTrue(string? currentUrl)
    {
        Assert.True(WebViewLoginCapture.IsOnLoginPage(currentUrl, "https://katfile.biz/login.html"));
    }

    [Theory]
    [InlineData("https://katfile.biz/?op=my_account")]                 // XFS post-login redirect target (path "/")
    [InlineData("https://katfile.biz/")]                                // home as logged-in user
    [InlineData("https://katfile.biz/?op=my_files")]
    public void IsOnLoginPage_NavigatedAwayFromLoginPage_ReturnsFalse(string currentUrl)
    {
        Assert.False(WebViewLoginCapture.IsOnLoginPage(currentUrl, "https://katfile.biz/login.html"));
    }

    [Fact]
    public void IsOnLoginPage_DifferentHostSamePath_ReturnsFalse()
    {
        // A cross-host bounce is "left the login page" — the path alone must not match across hosts.
        Assert.False(WebViewLoginCapture.IsOnLoginPage("https://other.example/login.html", "https://katfile.biz/login.html"));
    }

    // ---- CefNavigationSequencer.DeleteThenNavigateAsync: navigate only AFTER the async delete completes -----

    [Fact]
    public async Task DeleteThenNavigate_DoesNotNavigateUntilDeleteCompletes()
    {
        TaskCompletionSource<bool> deleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int navigateCalls = 0;

        Task sequence = CefNavigationSequencer.DeleteThenNavigateAsync(
            deleteCookiesAsync: () => deleteGate.Task,
            navigate: () => navigateCalls++);

        // The delete callback has not fired: navigation MUST NOT have happened, and the sequence is pending.
        Assert.Equal(0, navigateCalls);
        Assert.False(sequence.IsCompleted);

        // Complete the (fake) async cookie delete; only now may navigation run — exactly once.
        deleteGate.SetResult(true);
        await sequence;

        Assert.Equal(1, navigateCalls);
    }

    [Fact]
    public async Task DeleteThenNavigate_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CefNavigationSequencer.DeleteThenNavigateAsync(null!, () => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CefNavigationSequencer.DeleteThenNavigateAsync(() => Task.CompletedTask, null!));
    }

    // ---- Cookie (Name,Value) projection guard: the CEF projection feeds WebViewLoginCapture identically -----

    [Fact]
    public void CookieProjection_EmptySkipAndFirstMatch_MatchWebView2Contract()
    {
        // The CefGlueLoginWindow projects its CefCookie jar via `.Select(c => (c.Name, c.Value))` — the SAME
        // shape the WebView2 window feeds from CoreWebView2Cookie. This guards that the shared selection logic
        // treats that projection identically: empty values are skipped, and the FIRST non-empty match wins.
        (string Name, string Value)[] projected =
        [
            ("xfss", string.Empty),   // empty ⇒ skipped
            ("xfss", "session-real"), // first non-empty ⇒ the session value
            ("xfss", "session-late"), // later duplicate ⇒ ignored (first-match)
            ("username", "alice"),
            ("extra", "e1"),
        ];

        CookieSelection sel = WebViewLoginCapture.SelectCookies(
            projected,
            cookieName: "xfss",
            usernameCookieName: "username",
            additionalCookieNames: ["extra"],
            cookieValueValidator: null);

        Assert.Equal("session-real", sel.SessionValue);
        Assert.Equal("alice", sel.UsernameValue);
        Assert.NotNull(sel.AdditionalCookies);
        Assert.Equal("e1", sel.AdditionalCookies!["extra"]);
    }

    [Fact]
    public void CookieProjection_BuildCookieHeader_DropsEmptyPairs()
    {
        // The CookieCaptureUrl jar is serialized via the SAME BuildCookieHeader the WebView2 path uses; the CEF
        // projection must produce identical output (empty-value pairs dropped, joined with "; ").
        (string Name, string Value)[] jar =
        [
            ("a", "1"),
            ("b", string.Empty),
            ("c", "3"),
        ];

        string? header = WebViewLoginCapture.BuildCookieHeader(jar);

        Assert.Equal("a=1; c=3", header);
    }
}
