// <copyright file="WebViewLoginProxy.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;

namespace CSUploader.Views;

/// <summary>
/// Pure proxy plumbing for <see cref="WebViewLoginWindow"/> / <see cref="Services.AvaloniaWebViewInteractiveAuthService"/>,
/// ported from the WPF window's BuildProxyServerArg/SanitizeFolderName (WebViewLoginWindow.xaml.cs:534-566) and
/// the WPF service's ResolveProxyCredentials (WebViewInteractiveAuthService.cs:148-187). The i18n of the SOCKS
/// refusal stays OUT of here (the service formats it) so this is Localizer-free and headlessly testable.
/// </summary>
internal static class WebViewLoginProxy
{
    /// <summary>Builds the Chromium <c>--proxy-server</c> value (<c>scheme://host:port</c>, no credentials —
    /// auth rides <c>BasicAuthenticationRequested</c>). Null for null/direct/no-WebProxy. ProxyChoice.Description
    /// is already scheme://host:port by construction in ProxyManager, which Chromium accepts verbatim.</summary>
    public static string? BuildProxyServerArg(ProxyChoice? proxy)
        => proxy is null || proxy.Id == 0 || proxy.WebProxy is null ? null : proxy.Description;

    /// <summary>Sanitises a hoster name into a directory segment (Windows-invalid chars → '_'). Mirrors the
    /// WPF SanitizeFolderName.</summary>
    public static string SanitizeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }

        return new string(buffer);
    }

    /// <summary>
    /// Classifies the pinned proxy: returns HTTP/HTTPS Basic credentials for the 407 challenge, or flags
    /// SOCKS-with-auth (Chromium's <c>--proxy-server</c> can't carry SOCKS creds and there's no event to
    /// supply them — the service turns this flag into the localized refusal message). Direct / no-auth →
    /// neither.
    /// </summary>
    public static ProxyResolution ResolveProxyCredentials(ProxyChoice proxy)
    {
        if (proxy.Id == 0 || proxy.WebProxy is null)
        {
            return new ProxyResolution(null, false); // direct
        }

        bool isSocks = proxy.Description.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

        NetworkCredential? cred = proxy.WebProxy.Credentials?.GetCredential(new Uri("https://example.com/"), "Basic");
        if (string.IsNullOrEmpty(cred?.UserName))
        {
            return new ProxyResolution(null, false); // no credentials to supply
        }

        return isSocks
            ? new ProxyResolution(null, true)
            : new ProxyResolution(new ProxyCredentials(cred!.UserName, cred.Password), false);
    }
}

/// <summary>Result of <see cref="WebViewLoginProxy.ResolveProxyCredentials"/>: the Basic credentials to feed
/// <c>BasicAuthenticationRequested</c> (null when none), and whether the proxy is the unsupported
/// SOCKS-with-auth shape.</summary>
internal readonly record struct ProxyResolution(ProxyCredentials? Credentials, bool SocksAuthUnsupported);

/// <summary>Username/password pair for HTTP/HTTPS proxy Basic auth in the embedded browser. Port of the WPF
/// <c>ProxyCredentials</c> record (WebViewLoginWindow.xaml.cs:574).</summary>
public sealed record ProxyCredentials(string Username, string? Password);
