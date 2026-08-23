// <copyright file="SpeedLimitScopes.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload;

/// <summary>
/// Holds the one bucket belonging to each speed-limit scope.
/// <para>
/// Side tables rather than fields, for two reasons. <see cref="Package"/> is a primary-constructor
/// class with no body to initialise into. And <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/>
/// creates at most one value per key even under concurrent first access — where a lazy <c>??=</c>
/// races, and the scheduler calls <c>BuildAttemptInputs</c> from concurrent <c>Task.Run</c> workers.
/// Entries die with their scope object.
/// </para>
/// </summary>
public static class SpeedLimitScopes
{
    private static readonly ConditionalWeakTable<AppSettings, SpeedLimiter> Global = new();
    private static readonly ConditionalWeakTable<Package, SpeedLimiter> Packages = new();
    private static readonly ConditionalWeakTable<PackageFile, SpeedLimiter> Files = new();

    /// <summary>
    /// The global bucket, or the shared unlimited one when no global limit is set. Null settings —
    /// <c>PackageOptions.Settings</c> is nullable for non-DI callers — means no limit to enforce.
    /// </summary>
    public static SpeedLimiter ForGlobal(AppSettings? settings)
        // Short-circuit BEFORE the table: unlimited is the common case and must cost nothing, but a
        // CWT lookup locks and allocates on first access. `settings!` is sound — a positive limit
        // implies settings was non-null.
        => settings?.SpeedLimit is > 0
            ? Global.GetValue(settings!, s => new SpeedLimiter(() => ToBytesPerSecond(s.SpeedLimit)))
            : SpeedLimiter.Unlimited;

    public static SpeedLimiter ForPackage(Package package)
        => package.SpeedLimitKBps is > 0
            ? Packages.GetValue(package, p => new SpeedLimiter(() => ToBytesPerSecond(p.SpeedLimitKBps)))
            : SpeedLimiter.Unlimited;

    public static SpeedLimiter ForFile(PackageFile file)
        => file.SpeedLimitKBps is > 0
            ? Files.GetValue(file, f => new SpeedLimiter(() => ToBytesPerSecond(f.SpeedLimitKBps)))
            : SpeedLimiter.Unlimited;

    /// <summary>
    /// Reads the nullable ONCE. Written inline as <c>x is &gt; 0 ? (long)x.Value * 1024 : null</c>
    /// it reads twice, and the UI can null it between the two — throwing from <c>.Value</c> in the
    /// middle of an upload.
    /// </summary>
    private static long? ToBytesPerSecond(int? kbps)
    {
        int? snapshot = kbps;
        return snapshot is > 0 ? (long)snapshot.Value * 1024 : null;
    }
}
