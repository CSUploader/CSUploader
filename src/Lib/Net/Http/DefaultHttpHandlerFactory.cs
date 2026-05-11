// <copyright file="DefaultHttpHandlerFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

public sealed class DefaultHttpHandlerFactory(AppSettings settings) : IHttpHandlerFactory
{
    public HttpHandler Create(ProxyChoice proxy, IAppLogger logger)
    {
        HttpClientHandler clientHandler = new()
        {
            AllowAutoRedirect = false,
        };

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

        MockServerConfig snap = MockServerConfig.FromAppSettings(settings);
        return new HttpHandler(client, logger, proxy.Description, snap);
    }
}
