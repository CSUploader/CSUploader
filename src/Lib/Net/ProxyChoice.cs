// <copyright file="ProxyChoice.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;

namespace CSUploader.Lib.Net;

/// <summary>
/// Immutable, non-null description of which proxy a given upload attempt is routed through.
/// Use <see cref="Direct"/> instead of null when no proxy is in play — that way every consumer
/// sees a value-typed answer and the type system enforces "every attempt has a proxy decision".
/// </summary>
/// <param name="Id">Database id of the proxy row, or 0 for direct connection.</param>
/// <param name="WebProxy">Resolved <see cref="IWebProxy"/> for the HttpClient; null for direct.</param>
/// <param name="Description">Human-readable form, surfaced to the Logs tab.</param>
public sealed record ProxyChoice(int Id, IWebProxy? WebProxy, string Description)
{
    public static ProxyChoice Direct { get; } = new(0, null, "(direct)");
}
