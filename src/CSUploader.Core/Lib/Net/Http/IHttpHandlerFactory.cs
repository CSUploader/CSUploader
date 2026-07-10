// <copyright file="IHttpHandlerFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Constructs a fresh <see cref="HttpHandler"/> for one upload attempt, baking the chosen
/// proxy and a snapshot of the mock-server config into the resulting client. Returns
/// non-null by contract — direct connections produce a no-proxy <see cref="HttpHandler"/>,
/// not null.
/// </summary>
public interface IHttpHandlerFactory
{
    public HttpHandler Create(ProxyChoice proxy, IAppLogger logger);
}
