// <copyright file="MockServerConfig.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Snapshot of the mock-server portion of <see cref="AppSettings"/> taken at handler-build
/// time. Removes <see cref="HttpHandler"/>'s read of <c>AppSettings.Current</c> — by the
/// time the handler is constructed, the runtime decision is frozen on the snapshot.
/// </summary>
public sealed record MockServerConfig(bool Enabled, string BaseUrl)
{
    public static MockServerConfig Disabled { get; } = new(false, string.Empty);

    public static MockServerConfig FromAppSettings(AppSettings settings)
        => new(settings.UseMockServer, settings.MockServerBaseUrl ?? string.Empty);
}
