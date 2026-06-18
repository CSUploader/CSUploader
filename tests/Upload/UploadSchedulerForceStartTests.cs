// <copyright file="UploadSchedulerForceStartTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Verifies the force-start path in <see cref="UploadScheduler"/>: a force-started file
/// launches past the concurrency admission gate (the global upload limit, the per-host limit,
/// and the hashing limit), yet is still counted by <c>FillSlots</c> when admitting normal
/// files — so force-starting over the limit over-fills a slot and suppresses the next normal
/// admission until the running count drops back below the limit (the limit is never raised).
/// </summary>
// Uses a <see cref="GatedPipeline"/> so each upload blocks in the Uploading state until the
// test explicitly releases it — making the "N concurrently running" assertions deterministic
// instead of racing the real pipeline. No database/PackageManager is involved (the PackageFiles
// have no DbId, so no persistence fires); teardown releases every outstanding gate so no upload
// task is left blocked after the scheduler is disposed.
public sealed class UploadSchedulerForceStartTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<UploadScheduler> _schedulers = [];
    private readonly List<GatedPipeline> _pipelines = [];

    public UploadSchedulerForceStartTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-force-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Unblock any upload still parked on its gate so the fire-and-forget task can unwind
        // before the scheduler's channel is torn down.
        foreach (GatedPipeline pipeline in _pipelines)
        {
            pipeline.ReleaseAll();
        }

        foreach (UploadScheduler scheduler in _schedulers)
        {
            scheduler.Dispose();
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ForceStart_WhenUploadLimitFull_LaunchesOverTheLimit()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 6);

        // Normal start fills exactly the limit; the remaining two wait.
        scheduler.AddPackage(package);
        await WaitFor(() => CountState(package, FileState.Uploading) == 4);
        Assert.Equal(2, CountState(package, FileState.UploadQueued));

        // Force-start one queued file → five running, one over the limit of four.
        PackageFile queued = package.First(f => f.State == FileState.UploadQueued);
        scheduler.ForceStart([queued]);

        await WaitFor(() => CountState(package, FileState.Uploading) == 5);
        Assert.Equal(5, CountState(package, FileState.Uploading));
        Assert.Equal(1, CountState(package, FileState.UploadQueued));
    }

    [Fact]
    public async Task ForceStart_OverLimit_SuppressesNormalAdmitUntilRunningDropsBelowLimit()
    {
        // This is the exact scenario from the feature request: limit 4, four running, force a
        // fifth → five running. When a normal upload finishes the count is back at the limit,
        // so NOTHING new starts; only once another finishes (count < limit) does the queue move.
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 6);

        scheduler.AddPackage(package);
        await WaitFor(() => CountState(package, FileState.Uploading) == 4);

        PackageFile forced = package.First(f => f.State == FileState.UploadQueued);
        scheduler.ForceStart([forced]);
        await WaitFor(() => CountState(package, FileState.Uploading) == 5);

        PackageFile lastQueued = package.Single(f => f.State == FileState.UploadQueued);

        // Finish one upload → running drops from 5 to 4 (== limit). The forced upload still
        // counts, so the last queued file must NOT be admitted.
        PackageFile firstDone = package.First(f => f.State == FileState.Uploading);
        pipeline.Complete(firstDone.Name);
        await WaitFor(() => firstDone.State == FileState.Completed);

        await Task.Delay(150); // give FillSlots a chance to (wrongly) admit
        Assert.Equal(FileState.UploadQueued, lastQueued.State);
        Assert.Equal(4, CountState(package, FileState.Uploading));

        // Finish another → running drops to 3 (< limit) → the queued file is finally admitted.
        PackageFile secondDone = package.First(f => f.State == FileState.Uploading);
        pipeline.Complete(secondDone.Name);
        await WaitFor(() => lastQueued.State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, lastQueued.State);
    }

    [Fact]
    public async Task ForceStart_BypassesZeroUploadLimit()
    {
        // Upload limit 0: a normal queued file never launches. Force start must still run it.
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 0, fileCount: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package); // SchedulePackageFiles → UploadQueued; FillSlots admits none
        await WaitFor(() => file.State == FileState.UploadQueued);
        await Task.Delay(100);
        Assert.Equal(FileState.UploadQueued, file.State);

        scheduler.ForceStart([file]);
        await WaitFor(() => file.State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, file.State);
    }

    [Fact]
    public async Task ForceStart_NeedsHashFile_RespectsHashLimitThenUploadsPastUploadLimit()
    {
        // Hash-required hoster: upload limit 0 (closed) but CPU limit 1 (one hash allowed).
        // Force start must hash the file through the normal CPU gate, then launch its upload over
        // the zero upload limit — proving the override jumps only the UPLOAD gate, after hashing.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 0, fileCount: 1, maxCpu: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        await Task.Delay(100);
        Assert.Equal(FileState.Idle, file.State);

        scheduler.ForceStart([file]);
        await WaitFor(() => file.State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, file.State);
        Assert.True(file.IsHashingComplete, "the file must have hashed before uploading");
    }

    [Fact]
    public async Task ForceStart_NeedsHashFile_RespectsZeroHashLimit_StaysQueuedWithoutHashing()
    {
        // CPU limit 0: force start must NOT bypass the hashing gate. The file is queued for
        // hashing and waits there (never hashes, never uploads), proving the hash limit is honoured.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 1, maxCpu: 0);
        PackageFile file = package.Single();

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.ForceStart([file]);

        await WaitFor(() => file.State == FileState.HashQueued);
        await Task.Delay(150);
        Assert.Equal(FileState.HashQueued, file.State); // hash gate respected: never advances
        Assert.False(file.IsHashingComplete);
    }

    [Fact]
    public async Task ForceStart_WhileGloballyPaused_RunsFileWithoutLiftingThePause()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 2);
        PackageFile target = package.First();
        PackageFile sibling = package.Last();

        scheduler.PauseAll();
        await WaitFor(() => scheduler.IsPaused);

        // Register without scheduling so the sibling stays Idle — proves force start is surgical.
        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.ForceStart([target]);

        await WaitFor(() => target.State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, target.State);
        Assert.True(scheduler.IsPaused, "force start must not lift the global pause");
        Assert.Equal(FileState.Idle, sibling.State);

        // Completing the forced upload runs FillSlots, but the pause is still in effect, so
        // nothing else is admitted.
        pipeline.Complete(target.Name);
        await WaitFor(() => target.State == FileState.Completed);
        await Task.Delay(100);
        Assert.True(scheduler.IsPaused);
        Assert.Equal(FileState.Idle, sibling.State);
    }

    [Fact]
    public async Task ForceStart_CompletedFile_ReuploadsAndClearsPriorResult()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 1);
        PackageFile file = package.Single();
        file.State = FileState.Completed;       // a finished upload...
        file.FileUrl = "https://old/result";    // ...with a prior result
        file.IsUploadFinished = true;

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.ForceStart([file]);

        await WaitFor(() => pipeline.RunCount == 1); // the re-upload attempt actually started
        Assert.Equal(FileState.Uploading, file.State);
        Assert.Null(file.FileUrl);                   // prior result cleared (by ForceStartFile)
        Assert.False(file.IsUploadFinished);

        pipeline.Complete(file.Name);
        await WaitFor(() => file.State == FileState.Completed);
        Assert.Equal("https://gated/" + file.Name, file.FileUrl); // new result recorded
    }

    [Fact]
    public async Task ForceStart_CompletedHashRequiredFile_RehashesAndDoesNotReuseHash()
    {
        // Re-uploading a completed hash-required file must DISCARD the cached hash and re-hash
        // (the file on disk may have changed). With CPU limit 0 it can't get a hash slot, so it
        // parks in HashQueued with its hash cleared — proving the hash is recomputed, not reused,
        // and that re-hashing still respects the CPU limit.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 1, maxCpu: 0);
        PackageFile file = package.Single();
        file.State = FileState.Completed;
        file.IsHashingComplete = true;
        file.FileHash = "DEADBEEF";
        file.FileUrl = "https://old/result";
        file.IsUploadFinished = true;

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.ForceStart([file]);

        await WaitFor(() => file.State == FileState.HashQueued);
        await Task.Delay(150);
        Assert.Equal(FileState.HashQueued, file.State); // waiting to re-hash (CPU limit respected)
        Assert.False(file.IsHashingComplete);           // cached hash discarded...
        Assert.Null(file.FileHash);                     // ...not reused
        Assert.Null(file.FileUrl);                      // prior result cleared
    }

    [Fact]
    public async Task ForceStart_AlreadyUploadingFile_DoesNotRelaunch()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 4, fileCount: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package);
        await WaitFor(() => pipeline.RunCount == 1);

        scheduler.ForceStart([file]); // already running → no second attempt
        await Task.Delay(150);
        Assert.Equal(1, pipeline.RunCount);
        Assert.Equal(FileState.Uploading, file.State);
    }

    private static int CountState(Package package, FileState state) => package.Count(f => f.State == state);

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        int waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }

        Assert.True(condition(), "condition was not met within the timeout");
    }

    private (UploadScheduler Scheduler, Package Package) Build(GatedPipeline pipeline, int maxUploads, int fileCount, int maxCpu = 4)
    {
        _pipelines.Add(pipeline);

        AppSettings settings = new()
        {
            MaxConcurrentUploadJobs = maxUploads,
            MaxConcurrentCPUJobs = maxCpu,
        };
        DefaultFileHosterRegistry registry = new([pipeline]);
        UploadScheduler scheduler = new(settings, BuildAttemptRunner(registry), Mock.Of<IAppLogger>(), new CSUploader.Lib.Crypto.HashingService(), registry);
        scheduler.Start(); // PackageManager does this in production; here we drive the scheduler directly.
        _schedulers.Add(scheduler);

        FileHosterClient hoster = new(pipeline.Name, Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = pipeline.Name, IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = settings,
            FileHosters = new() { { hoster, login } },
        };
        Package package = new(options);
        PackageFile[] files = [.. Enumerable.Range(0, fileCount).Select(i => MakeFile(package, hoster, login, $"f{i}.bin"))];
        package.AddPackageFiles(files);

        return (scheduler, package);
    }

    private PackageFile MakeFile(Package package, FileHosterClient hoster, FileHosterLoginDto login, string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, [1]);
        return new PackageFile(package, path, hoster, login);
    }

    private static AttemptRunner BuildAttemptRunner(IFileHosterRegistry registry)
    {
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> hf = new();
        // Fresh handler per attempt — the scheduler disposes each attempt's handler, and these
        // tests run several uploads concurrently, so a shared instance would be disposed out
        // from under its siblings.
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    /// <summary>
    /// Test pipeline whose upload blocks in the Uploading state until <see cref="Complete"/> is
    /// called for that file (keyed by file name), letting tests hold a precise number of uploads
    /// "running" while they assert admission behaviour. Cancellation unblocks the wait so the
    /// scheduler's pause/stop paths still resolve.
    /// </summary>
    private sealed class GatedPipeline(string name, bool requiresHash = false) : IFileHosterPipeline
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private int _runCount;

        public string Name { get; } = name;

        public bool RequiresHashingBeforeUpload { get; } = requiresHash;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        public int RunCount => Volatile.Read(ref _runCount);

        public void Complete(string fileName) => Gate(fileName).TrySetResult();

        public void ReleaseAll()
        {
            foreach (TaskCompletionSource tcs in _gates.Values)
            {
                tcs.TrySetResult();
            }
        }

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "ok"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _runCount);
            yield return new TransferStarted(ctx.FileSize);

            // Park here (in the Uploading state) until the test releases this file or the
            // attempt is cancelled. No yield inside the using — only the await.
            using (ct.Register(() => Gate(ctx.FileName).TrySetCanceled()))
            {
                await Gate(ctx.FileName).Task;
            }

            yield return new TransferCompleted("https://gated/" + ctx.FileName);
        }

        private TaskCompletionSource Gate(string fileName)
            => _gates.GetOrAdd(fileName, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
