// <copyright file="DefaultHttpHandlerFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

public sealed class DefaultHttpHandlerFactory(AppSettings settings) : IHttpHandlerFactory
{
    // Static Chrome/Edge User-Agent. Some XFileSharing-family backends and CDN/WAF layers
    // (Cloudflare, Sucuri) silently drop or challenge clients with no UA — sending a
    // realistic one keeps requests indistinguishable from a logged-in browser. The string
    // is intentionally non-current-to-the-day so we don't have to chase Chrome's release
    // cadence; backends only key on "is it a recognisable browser", not the exact version.
    internal const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0";

    public HttpHandler Create(ProxyChoice proxy, IAppLogger logger)
    {
        HttpClientHandler clientHandler = new()
        {
            AllowAutoRedirect = false,

            // Pipelines forward cookies BY HAND (read Set-Cookie off response snapshots, build the
            // Cookie request header themselves — GigaPeta, HitFile refresh). Make that invariant
            // real: with no auto cookie container, a manually-set Cookie header is what goes on the
            // wire and a stray Set-Cookie can never silently start participating. Several pipeline
            // comments assert "built without UseCookies" — this is the line that makes them true.
            UseCookies = false,

            // Browsers send Accept-Encoding: gzip, deflate, br, zstd and expect to receive
            // compressed responses. Hosters increasingly only send compressed bodies; without
            // decompression enabled we'd either get garbled HTML or fall through to the WAF's
            // "client looks suspicious" challenge page.
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (settings.AllowInvalidServerCertificates)
        {
            // User opted in (Connection tab) — accept any cert. Required for hosters whose
            // storage CDN nodes ship certs that fail standard validation (FileBoom's
            // cmb-*.filestore.app edges return RemoteCertificateNameMismatch). Applies to
            // every request through this handler — login AND upload — so the user is
            // explicitly accepting the MITM exposure when they tick this.
            clientHandler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        if (proxy.WebProxy is not null)
        {
            clientHandler.Proxy = proxy.WebProxy;
            clientHandler.UseProxy = true;
        }
        else
        {
            clientHandler.UseProxy = false;
        }

        // Per-attempt timeout: the request itself has its own cancellation; the client-level
        // timeout is generous to allow long uploads while a stuck connection still gets killed.
        HttpClient client = new(clientHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Apply the UA once at HttpClient level so every request out of this handler carries it.
        // TryParseAdd handles the multi-token form correctly; falling back to a literal
        // assignment via TryAddWithoutValidation if .NET rejects the parse keeps boot resilient.
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(DefaultUserAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        var snap = MockServerConfig.FromAppSettings(settings);
        return new HttpHandler(client, logger, proxy.Description, snap);
    }
}
