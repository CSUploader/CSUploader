// <copyright file="UpdateService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace CSUploader.Lib.Update;

public sealed class UpdateService : IUpdateService
{
    private const string GitHubRepoUrl = "https://github.com/CSUploader/CSUploader";

    private readonly UpdateManager _manager;
    private readonly IAppLogger _logger;

    public UpdateService(IAppLogger logger)
    {
        _logger = logger;
        GithubSource source = new(GitHubRepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);

        Version? asmVersion = Assembly.GetEntryAssembly()?.GetName().Version
                              ?? Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersion = asmVersion?.ToString(3) ?? "0.0.0";
    }

    public string CurrentVersion { get; }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            // Loose builds and `dotnet run` don't have a Velopack package layout to update.
            return UpdateCheckResult.NotInstalled;
        }

        try
        {
            UpdateInfo? info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return UpdateCheckResult.UpToDate;
            }

            string version = info.TargetFullRelease.Version.ToString();
            return UpdateCheckResult.Available(new UpdateAvailableInfo(version, info, EstimateDownloadBytes(info)));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// What the download is expected to move. Velopack applies deltas when they exist and falls back
    /// to the full package on error, so the delta total is the likely answer and the full size is
    /// the fallback — this cannot know which it will be, and a fallback makes the estimate small.
    /// <para>
    /// This is also the only place in the app allowed to read Velopack's types: the whole point of
    /// <see cref="UpdateAvailableInfo.Payload"/> being an <c>object</c> is that the size gets out
    /// without the dependency coming with it.
    /// </para>
    /// </summary>
    internal static long EstimateDownloadBytes(UpdateInfo info)
    {
        long deltas = 0;
        foreach (VelopackAsset delta in info.DeltasToTarget ?? [])
        {
            deltas += delta.Size;
        }

        return deltas > 0 ? deltas : Math.Max(0, info.TargetFullRelease?.Size ?? 0);
    }

    public async Task DownloadAsync(UpdateAvailableInfo info, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        if (info.Payload is not UpdateInfo native)
        {
            throw new ArgumentException("Payload is not a Velopack UpdateInfo.", nameof(info));
        }

        await _manager.DownloadUpdatesAsync(native, p => progress?.Report(p), cancelToken: cancellationToken).ConfigureAwait(false);
    }

    public void ApplyAndRestart(UpdateAvailableInfo info)
    {
        if (info.Payload is not UpdateInfo native)
        {
            throw new ArgumentException("Payload is not a Velopack UpdateInfo.", nameof(info));
        }

        _manager.ApplyUpdatesAndRestart(native);
    }
}
