// <copyright file="WebViewLoginProxyTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Pure proxy plumbing for the WebView sign-in (Phase 8 Task 1): the Chromium <c>--proxy-server</c> arg, the
/// per-hoster user-data folder name, and the SOCKS-with-auth refusal — ported from the WPF window/service so
/// the session's issuing IP matches the upload's IP (XFS binds session cookies to the issuing IP).
/// </summary>
public class WebViewLoginProxyTests
{
    [Fact]
    public void BuildProxyServerArg_Direct_ReturnsNull()
    {
        Assert.Null(WebViewLoginProxy.BuildProxyServerArg(ProxyChoice.Direct));
        Assert.Null(WebViewLoginProxy.BuildProxyServerArg(null));
    }

    [Fact]
    public void BuildProxyServerArg_UsesDescriptionVerbatim()
    {
        var proxy = new ProxyChoice(7, new WebProxy("https://p.example.test:8080"), "https://p.example.test:8080");
        Assert.Equal("https://p.example.test:8080", WebViewLoginProxy.BuildProxyServerArg(proxy));
    }

    [Fact]
    public void SanitizeFolderName_ReplacesInvalidChars()
    {
        // ':' and '/' are invalid on Windows → underscores; letters/digits survive.
        string s = WebViewLoginProxy.SanitizeFolderName("ex:load/1");
        Assert.DoesNotContain(':', s);
        Assert.DoesNotContain('/', s);
        Assert.Equal("ex_load_1".Length, s.Length);
    }

    [Fact]
    public void SanitizeFolderName_ProducesExactMappedOutput()
    {
        // Exact output: only the Windows-invalid chars (':' and '/') become '_'; every valid char (letters,
        // digits) survives unchanged and in place. An impl that also rewrote valid chars would fail here.
        Assert.Equal("ex_load_1", WebViewLoginProxy.SanitizeFolderName("ex:load/1"));
    }

    [Fact]
    public void ResolveProxyCredentials_Direct_NoCreds_NoRefusal()
    {
        var r = WebViewLoginProxy.ResolveProxyCredentials(ProxyChoice.Direct);
        Assert.Null(r.Credentials);
        Assert.False(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_HttpsWithAuth_ReturnsCredentials()
    {
        var proxy = new ProxyChoice(3,
            new WebProxy("https://p:8080") { Credentials = new NetworkCredential("u", "pw") },
            "https://p:8080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.NotNull(r.Credentials);
        Assert.Equal("u", r.Credentials!.Username);
        Assert.Equal("pw", r.Credentials.Password);
        Assert.False(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_SocksWithAuth_Refuses()
    {
        var proxy = new ProxyChoice(4,
            new WebProxy("socks5://p:1080") { Credentials = new NetworkCredential("u", "pw") },
            "socks5://p:1080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.Null(r.Credentials);
        Assert.True(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_SocksNoAuth_NoRefusal()
    {
        var proxy = new ProxyChoice(5, new WebProxy("socks5://p:1080"), "socks5://p:1080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.Null(r.Credentials);
        Assert.False(r.SocksAuthUnsupported); // no creds to satisfy → no refusal, just a direct-ish hop
    }
}
