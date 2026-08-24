// <copyright file="IUpdateService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// Auto-update facade. Wraps Velopack's <c>UpdateManager</c> + <c>GithubSource</c> so the
/// MainViewModel can be unit-tested with a mock.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Gets the running app's version (from the assembly).
    /// </summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// Gets a value indicating whether the app was launched from a Velopack-installed
    /// location. False during <c>dotnet run</c> or when running the loose build output.
    /// <para>
    /// When false, <see cref="DownloadAsync"/> and <see cref="ApplyAndRestart"/> are not no-ops —
    /// they THROW. Velopack's <c>DownloadUpdatesAsync</c> opens with <c>EnsureInstalled</c>, so
    /// there is no safe way to call the install path without a package layout around the process.
    /// <see cref="CheckAsync"/> still works: it reads the release feed directly and reports
    /// <see cref="UpdateCheckStatus.AvailableNotInstallable"/> rather than an installable update.
    /// </para>
    /// </summary>
    public bool IsInstalled { get; }

    /// <summary>
    /// Polls the GitHub Releases endpoint. Returns an explicit outcome so callers can tell
    /// "up to date" from "check failed" (network, auth, 404) from "there is one, but this build
    /// cannot install it".
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update bundle into the local Velopack cache. Reports byte progress
    /// (0-100) via <paramref name="progress"/>.
    /// </summary>
    public Task DownloadAsync(UpdateAvailableInfo info, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the previously downloaded update and restarts the process.
    /// </summary>
    public void ApplyAndRestart(UpdateAvailableInfo info);
}
