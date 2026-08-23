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
            return UpdateCheckResult.Available(new UpdateAvailableInfo(version, info, PlanDownload(info)));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Velopack's own delta-eligibility rule, mirrored so the byte readout counts against the
    /// package that will actually be fetched.
    /// <para>
    /// From <c>UpdateManager.DownloadUpdatesAsync</c> in Velopack 1.2.0: deltas are used only when a
    /// base release with a file name exists, there is at least one delta, there are no more than
    /// <see cref="MaximumDeltasBeforeFallback"/> of them, and their summed size does not exceed the
    /// full package. Any other shape downloads the full package immediately — no delta attempt, so
    /// nothing later corrects a guess that assumed one.
    /// </para>
    /// </summary>
    internal static UpdateDownloadPlan PlanDownload(UpdateInfo info)
    {
        long full = Math.Max(0, info.TargetFullRelease?.Size ?? 0);

        VelopackAsset[] deltas = [.. (info.DeltasToTarget ?? []).OrderBy(d => d.Version)];
        if (info.BaseRelease?.FileName is null || deltas.Length == 0 || deltas.Length > MaximumDeltasBeforeFallback)
        {
            return UpdateDownloadPlan.Full(full);
        }

        long summed = 0;
        foreach (VelopackAsset delta in deltas)
        {
            if (delta.Size < 0 || delta.Size > long.MaxValue - summed)
            {
                return UpdateDownloadPlan.Full(full);
            }

            summed += delta.Size;
        }

        // Velopack's own fallback conditions. Note it compares against the FULL size, so a delta set
        // that is not actually smaller is discarded rather than used.
        return summed > full
            ? UpdateDownloadPlan.Full(full)
            : UpdateDownloadPlan.Deltas([.. deltas.Select(d => d.Size)]);
    }

    /// <summary>
    /// Velopack's default when no <c>UpdateOptions</c> is supplied — and none is, at the
    /// <c>UpdateManager</c> constructed above. Past this count it skips deltas entirely.
    /// </summary>
    internal const int MaximumDeltasBeforeFallback = 10;

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
