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
/// <c>Task.Run</c> work — the scheduler's order/force-start tests plus PackageManager's
/// fire-and-forget persistence callbacks (state, queue-order, and soft-remove writes). When that
/// burst exceeds the thread pool's current worker count, the pool grows slowly (roughly one new
/// thread every ~500 ms), so a queued persistence <c>Task.Run</c> can sit unscheduled for seconds.
/// That starves the polling assertions in <c>PackageManagerSoftRemoveTests</c> (e.g.
/// <c>WaitForAsync</c>'s 50×50 ms budget), producing intermittent ~1-in-10 full-suite flakes that
/// vanish in isolation and on re-run. Pre-warming the floor lets those bursts run immediately.
/// The numeric-upload-order feature added more scheduler tests, tipping a borderline-tight budget
/// into frequent failures.
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
