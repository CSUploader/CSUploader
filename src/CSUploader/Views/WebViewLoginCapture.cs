// <copyright file="WebViewLoginCapture.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;

namespace CSUploader.Views;

/// <summary>
/// Pure cookie/probe selection logic for <see cref="WebViewLoginWindow"/>, extracted from the WPF window's
/// TryCaptureCookiesAsync / TryProbeAsync / BuildCookieHeaderAsync so it is unit-testable without a live
/// WebView2 (whose <c>CoreWebView2Cookie</c> has no public constructor). The window projects its live
/// cookie jar to <c>(Name, Value)</c> tuples at the call site.
/// </summary>
internal static class WebViewLoginCapture
{
    /// <summary>
    /// Picks the session cookie (first non-empty value named <paramref name="cookieName"/> that passes
    /// <paramref name="cookieValueValidator"/>), plus the optional identity cookie and any supplementary
    /// cookies. A null <see cref="CookieSelection.SessionValue"/> means "not signed in yet" — the caller
    /// keeps polling. Mirrors WebViewLoginWindow.xaml.cs:427-489.
    /// </summary>
    public static CookieSelection SelectCookies(
        IEnumerable<(string Name, string Value)> cookies,
        string cookieName,
        string? usernameCookieName,
        IReadOnlyList<string>? additionalCookieNames,
        Func<string, bool>? cookieValueValidator)
    {
        string? session = null;
        string? username = null;
        Dictionary<string, string>? additional = null;

        foreach ((string name, string value) in cookies)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (session is null && string.Equals(name, cookieName, StringComparison.Ordinal))
            {
                session = value;
            }
            else if (usernameCookieName is not null && username is null
                && string.Equals(name, usernameCookieName, StringComparison.Ordinal))
            {
                username = value;
            }
            else if (additionalCookieNames is not null)
            {
                foreach (string wanted in additionalCookieNames)
                {
                    if (string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        additional ??= new(StringComparer.Ordinal);
                        additional.TryAdd(name, value);
                        break;
                    }
                }
            }
        }

        // Validator opt-in (FileBoom's pre-login bootstrap JWT): reject a session value that doesn't pass,
        // so the window waits for the real post-login one.
        if (session is not null && cookieValueValidator is not null && !cookieValueValidator(session))
        {
            session = null;
        }

        return new CookieSelection(session, username, additional);
    }

    /// <summary>Decodes the JSON value <c>CoreWebView2.ExecuteScriptAsync</c> returns (e.g. <c>"\"id\""</c>)
    /// into a plain string. Returns null for <c>null</c>/non-string/invalid JSON. Mirrors WPF
    /// WebViewLoginWindow.xaml.cs:390-405.</summary>
    public static string? TryParseJsonString(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Joins a cookie jar into a single <c>name=value; name=value</c> header (empty pairs dropped);
    /// null when nothing to send. Mirrors WPF WebViewLoginWindow.xaml.cs:371-386.</summary>
    public static string? BuildCookieHeader(IEnumerable<(string Name, string Value)> cookies)
    {
        List<string> pairs = [];
        foreach ((string name, string value) in cookies)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
            {
                pairs.Add(name + "=" + value);
            }
        }

        return pairs.Count > 0 ? string.Join("; ", pairs) : null;
    }
}

/// <summary>The outcome of <see cref="WebViewLoginCapture.SelectCookies"/>: the session value (null until a
/// valid one appears), the optional identity value, and any supplementary cookies (null when none asked
/// for / none present).</summary>
internal readonly record struct CookieSelection(
    string? SessionValue,
    string? UsernameValue,
    IReadOnlyDictionary<string, string>? AdditionalCookies);
