// <copyright file="CefProbeResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Views;

/// <summary>
/// Pure, engine-free seams for the CefGlue interactive sign-in (<c>Views/Cef/CefGlueLoginWindow</c>),
/// isolated here so they unit-test WITHOUT initializing CEF (which needs a display and cannot boot in
/// headless CI). Deliberately placed at the <c>Views/</c> root (NOT under <c>Views/Cef/</c>) and free of any
/// CefGlue reference so it compiles on BOTH the Windows and portable head targets — the head test project
/// targets <c>net10.0-windows</c> and resolves the WINDOWS head (where <c>Views/Cef/**</c> is excluded), so a
/// seam under <c>Views/Cef/</c> could not be exercised by the head suite at all. The window (portable-only)
/// consumes these.
/// </summary>
internal static class CefProbeResult
{
    /// <summary>
    /// The JS success-probe shim (design §mapping "JS success-probe return"). On the CEF path
    /// <c>AvaloniaCefBrowser.EvaluateJavaScript&lt;string&gt;</c> returns the probe's value DIRECTLY — NOT the
    /// JSON-quoted string WebView2's <c>ExecuteScriptAsync</c> produces — so <see
    /// cref="WebViewLoginCapture.TryParseJsonString"/> is BYPASSED here: a non-empty result is a completed
    /// sign-in (<paramref name="value"/> = the raw evaluated string); null/empty means "not authenticated
    /// yet". Kept pure so the empty ⇒ not-complete / non-empty ⇒ complete contract is unit-provable.
    /// </summary>
    public static bool TryProbeComplete(string? evaluated, out string value)
    {
        if (string.IsNullOrEmpty(evaluated))
        {
            value = string.Empty;
            return false;
        }

        value = evaluated;
        return true;
    }

    /// <summary>
    /// Adapts a WebView2-shaped success-probe script for CefGlue evaluation. CefGlue's render subprocess wraps
    /// the code as <c>evaluateScript(function() { &lt;script&gt; })</c> (verified in the 120.6099.211
    /// <c>JavascriptHelper.WrapScriptForEvaluation</c>), so the wrapper returns a value ONLY if the script has
    /// a top-level <c>return</c>. The shared probe scripts are single expression statements
    /// (<c>(function(){ …; return X; })();</c>) — correct for WebView2's <c>ExecuteScriptAsync</c> (which
    /// evaluates the expression) but under CEF they would merely RUN and yield <c>undefined</c>, so the probe
    /// would never complete. This turns the expression statement into a returned value by stripping trailing
    /// whitespace + one trailing <c>;</c> and prefixing <c>return </c>, e.g.
    /// <c>(function(){…})();</c> → <c>return (function(){…})()</c>. Core / the WebView2 path are untouched;
    /// this adaptation lives on the CEF path only. Null/blank passes through unchanged (no probe hoster).
    /// </summary>
    public static string? WrapProbeScript(string? probeScript)
    {
        if (string.IsNullOrWhiteSpace(probeScript))
        {
            return probeScript;
        }

        string trimmed = probeScript.Trim();
        if (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return "return (" + trimmed + ");";
    }
}

/// <summary>
/// Pure ordering seam for the CEF pre-navigation cookie wipe (design R1 "clear stale cookie pre-nav"). CEF's
/// cookie <c>DeleteCookies</c> is asynchronous (its completion rides a callback on the CEF IO thread); the
/// stale-cookie delete MUST finish before the login URL is navigated, else the delete races the navigation
/// and a stale session cookie is captured the instant the page loads (the Hxfile finding). This helper makes
/// that ordering — await the delete, THEN navigate — unit-provable around a fake async cookie-manager seam,
/// with no CefGlue dependency.
/// </summary>
internal static class CefNavigationSequencer
{
    /// <summary>Awaits <paramref name="deleteCookiesAsync"/> to completion, THEN invokes <paramref
    /// name="navigate"/> exactly once. <c>LoadURL</c>/navigation must never run before the delete callback
    /// completes.</summary>
    public static async Task DeleteThenNavigateAsync(Func<Task> deleteCookiesAsync, Action navigate)
    {
        ArgumentNullException.ThrowIfNull(deleteCookiesAsync);
        ArgumentNullException.ThrowIfNull(navigate);

        await deleteCookiesAsync().ConfigureAwait(true);
        navigate();
    }
}
