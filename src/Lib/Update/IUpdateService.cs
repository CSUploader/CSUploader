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
    string CurrentVersion { get; }

    /// <summary>
    /// Gets a value indicating whether the app was launched from a Velopack-installed
    /// location. False during <c>dotnet run</c> or when running the loose build output —
    /// in which case <see cref="ApplyAndRestart"/> is a no-op.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Polls the GitHub Releases endpoint. Returns null when no newer release is
    /// available, or when the app isn't running from a Velopack-installed location.
    /// </summary>
    Task<UpdateAvailableInfo?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update bundle into the local Velopack cache. Reports byte progress
    /// (0-100) via <paramref name="progress"/>.
    /// </summary>
    Task DownloadAsync(UpdateAvailableInfo info, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the previously downloaded update and restarts the process.
    /// </summary>
    void ApplyAndRestart(UpdateAvailableInfo info);
}
