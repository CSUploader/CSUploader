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
            return Describe(await _manager.CheckForUpdatesAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Turns what the updater found into what the rest of the app sees.
    /// <para>
    /// Split out of <see cref="CheckAsync"/> so it can be tested: <c>UpdateManager</c> is concrete
    /// and needs an installed Velopack layout, so the surrounding method cannot be driven from a
    /// test at all. This is the join where an update's DOWNLOAD PLAN is attached, and without a seam
    /// here that attachment is unreachable — passing <see cref="UpdateDownloadPlan.Unknown"/> would
    /// leave every test green and quietly remove the byte readout from the update window.
    /// </para>
    /// </summary>
    internal static UpdateCheckResult Describe(UpdateInfo? info)
        => info is null
            ? UpdateCheckResult.UpToDate
            : UpdateCheckResult.Available(new UpdateAvailableInfo(
                info.TargetFullRelease.Version.ToString(), info, PlanDownload(info)));

    /// <summary>
    /// Whether a size can be counted against the reported percentage, by asking the question
    /// Velopack asks itself: is it going to fetch one package whole, or apply deltas?
    /// <para>
    /// From <c>UpdateManager.DownloadUpdatesAsync</c> in Velopack 1.2.0, deltas are used only when a
    /// base release with a file name exists, there is at least one delta, there are no more than
    /// <see cref="MaximumDeltasBeforeFallback"/> of them, and their summed size does not exceed the
    /// full package. Every other shape downloads the full package immediately.
    /// </para>
    /// <para>
    /// The delta path yields <see cref="UpdateDownloadPlan.Unknown"/> rather than a size — see
    /// <see cref="UpdateDownloadPlan"/> for why no byte figure can be derived there.
    /// </para>
    /// <para>
    /// <b>The two ways of being wrong are not symmetrical.</b> Answering "delta" when it is really a
    /// full download only HIDES a readout that could have been shown. Answering "full" when it is
    /// really deltas feeds the full package's size into an aggregate delta percentage, and that is a
    /// visibly wrong number. So the conditions below are the conservative ones, and if a future
    /// Velopack loosens any of them — raising the ten, say — this becomes wrong in the bad
    /// direction. Nothing here can detect that; it is the cost of the coupling.
    /// </para>
    /// </summary>
    internal static UpdateDownloadPlan PlanDownload(UpdateInfo info)
    {
        long full = info.TargetFullRelease?.Size ?? 0;
        VelopackAsset[] deltas = [.. info.DeltasToTarget ?? []];

        // Summed before any of the eligibility conditions, because that is the order Velopack does
        // it in, and an overflow there throws OUT of the whole download rather than into its
        // delta-fallback handler. Metadata that cannot be added up therefore means nothing gets
        // fetched at all, and there is no size to show for it.
        long summed = 0;
        foreach (VelopackAsset delta in deltas)
        {
            // Each side guarded against its OWN end of the range. Testing the other way round —
            // `size < long.MinValue - summed` for a positive summed — wraps in the comparison
            // itself and rejects perfectly ordinary metadata.
            if ((delta.Size > 0 && summed > long.MaxValue - delta.Size)
                || (delta.Size < 0 && summed < long.MinValue - delta.Size))
            {
                return UpdateDownloadPlan.Unknown;
            }

            summed += delta.Size;
        }

        // Note this is NOT an overflow guard on negative sizes: a negative sum does not throw in
        // Velopack either, it simply satisfies `summed <= full` and sends it down the delta path,
        // which is the answer that falls out below anyway.
        bool usesDeltas = info.BaseRelease?.FileName is not null
            && deltas.Length > 0
            && deltas.Length <= MaximumDeltasBeforeFallback
            && summed <= full;

        return usesDeltas ? UpdateDownloadPlan.Unknown : UpdateDownloadPlan.Full(full);
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
