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

    /// <summary>
    /// The snapshot for the current settings — and, outside a Debug build, always
    /// <see cref="Disabled"/>.
    /// <para>
    /// The switch that sets <c>UseMockServer</c> is a DEBUG-only developer tool
    /// (<c>DeveloperSettingsView</c>, which release builds do not compile), but the VALUE it writes
    /// lives in the settings database, and Debug and release builds on one machine share that
    /// database. Without this guard a flag left on after a development session would follow the
    /// user into a shipped build and redirect every file-hoster request to localhost:8080 — every
    /// upload failing, with no UI left anywhere to turn it back off. Honouring the flag only where
    /// the switch exists keeps the developer tool from being able to escape.
    /// </para>
    /// </summary>
    public static MockServerConfig FromAppSettings(AppSettings settings)
    {
#if DEBUG
        return new(settings.UseMockServer, settings.MockServerBaseUrl ?? string.Empty);
#else
        _ = settings;
        return Disabled;
#endif
    }
}
