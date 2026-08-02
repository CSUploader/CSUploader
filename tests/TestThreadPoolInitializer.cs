// <copyright file="TestThreadPoolInitializer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;

namespace CSUploader.Tests;

/// <summary>
/// Raises the thread-pool worker-thread floor once, before any test runs.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel. Several classes here dispatch bursts of fire-and-forget
/// <c>Task.Run</c> work — the scheduler's order/force-start tests plus PackageManager's persistence
/// callbacks. When such a burst exceeds the pool's current worker count the pool grows slowly
/// (roughly one new thread every ~500 ms), so queued work can sit unscheduled for a while.
/// Pre-warming the floor lets those bursts run immediately. It is cheap and only ever raises the
/// minimum, so it is kept.
/// <para>
/// ⚠ <b>This was originally added to fix the <c>PackageManagerSoftRemoveTests</c> flake, and that
/// attribution was wrong.</b> That flake was not a slow write — it was a write that never happened:
/// the fixture shared one <c>SqliteConnection</c> between a polling reader and the fire-and-forget
/// writer, which is not thread-safe, so the write threw ("SQLite Error 5: unable to delete/modify
/// user-function due to active statements" — measured at 41 failures in 200 on exactly that shape)
/// and <c>RemovePackage</c> swallowed it. Fixed 2026-08-02 by giving each DbContext its own
/// connection into a shared-cache in-memory database. Do not credit this pre-warm for that, and do
/// not reach for thread-pool tuning the next time a polling assertion fails — check first whether
/// the thing being waited for actually ran.
/// </para>
/// <para>
/// A <see cref="ModuleInitializerAttribute"/> runs exactly once when the test assembly loads —
/// before any test — and needs no xUnit fixture plumbing. We only ever RAISE the minimums
/// (<see cref="Math.Max(int, int)"/> against the current values), never lower them, so a host or CI
/// runner that already configured a higher floor is left untouched.
/// </para>
/// </remarks>
internal static class TestThreadPoolInitializer
{
    private const int MinWorkerThreads = 64;
    private const int MinCompletionPortThreads = 64;

    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(
            Math.Max(workerThreads, MinWorkerThreads),
            Math.Max(completionPortThreads, MinCompletionPortThreads));
    }
}
