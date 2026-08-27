// <copyright file="UploadSchedulerCancellationTests.cs" company="CSUploader">
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
/// Guards the launch/cancel race in <see cref="UploadScheduler"/>: stopping or pausing must never
/// surface as <c>"The CancellationTokenSource has been disposed."</c> on a file.
/// </summary>
/// <remarks>
/// <para>
/// <c>LaunchUpload</c>/<c>LaunchHash</c> flip the file to Uploading/Hashing, create its
/// <see cref="CancellationTokenSource"/>, and hand the work to a detached worker. The stop paths
/// (<c>StopAllFiles</c>, <c>PauseRunningFiles</c>, <c>DoRemovePackage</c>) cancel AND dispose that
/// source for every file in those states — including ones whose worker has not started yet. So the
/// token must be read on the launching thread, before the work is queued; a worker that reaches
/// into the source itself races the dispose and throws <see cref="ObjectDisposedException"/> out of
/// the pipeline loop, which the generic handler reports as a red row with an error the user cannot
/// act on.
/// </para>
/// <para>
/// The observed production failure, from the app's own log table:
/// <c>Upload pipeline crashed: System.ObjectDisposedException: The CancellationTokenSource has been
/// disposed. at System.Threading.CancellationTokenSource.get_Token() at
/// UploadScheduler.&lt;&lt;LaunchUpload&gt;b__1&gt;d.MoveNext()</c>.
/// </para>
/// <para>
/// These tests do not race the thread pool to reproduce that. They swap in a
/// <see cref="HeldWorkLauncher"/>, which parks every launched worker at the exact instant the race
/// opens — the file is already Uploading/Hashing and its source exists, but the body has read
/// nothing yet. Stop/Pause then runs to completion through the pump, and only afterwards are the
/// workers released. Every worker therefore loses the race, on every machine, every run.
/// </para>
/// </remarks>
public sealed class UploadSchedulerCancellationTests : IAsyncLifetime
{
    /// <summary>
    /// Files launched in the single <c>FillSlots</c> pass that the stop then races. Small on
    /// purpose — with the launcher held, one file would prove the point; a handful just makes the
    /// failure message read like the screenful of red rows the user actually saw.
    /// </summary>
    private const int FileCount = 8;

    private readonly string _tempDir;
    private readonly List<(UploadScheduler Scheduler, HeldWorkLauncher Launcher)> _harnesses = [];
    private readonly List<GatedPipeline> _pipelines = [];

    public UploadSchedulerCancellationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Drains before deleting anything. The workers are detached — <c>UploadScheduler.Dispose</c>
    /// waits for the channel loop, not for them — so without an explicit drain a hash worker can
    /// still be reading a temp file while this method deletes it, and stray bodies leak into the
    /// next test's thread pool (see tests/CLAUDE.md).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (GatedPipeline pipeline in _pipelines)
        {
            pipeline.ReleaseAll();
        }

        foreach ((UploadScheduler scheduler, HeldWorkLauncher launcher) in _harnesses)
        {
            await launcher.ReleaseAndDrainAsync(scheduler.DrainAsync);
        }

        foreach ((UploadScheduler scheduler, _) in _harnesses)
        {
            scheduler.Dispose();
        }

        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task StopAll_WhenTheWorkerStartsAfterTheStop_NeverReportsADisposedTokenSource()
    {
        GatedPipeline pipeline = new("Rapidgator");
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger);

        // Every file launches, and every worker is caught and held before it reads anything.
        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(FileCount);

        // The stop now runs against files that are all Uploading with a live source — and disposes
        // every one of those sources out from under a worker that has yet to begin.
        scheduler.StopAll();
        await WaitFor(() => package.All(f => f.State == FileState.Cancelled));

        await launcher.ReleaseAndDrainAsync(scheduler.DrainAsync);

        AssertNoDisposedTokenSource(package, logger);
        Assert.All(package, f => Assert.Equal(FileState.Cancelled, f.State));
    }

    [Fact]
    public async Task StopAll_WhenTheHashWorkerStartsAfterTheStop_NeverReportsADisposedTokenSource()
    {
        // Same race, hashing half: LaunchHash flips the file to Hashing and creates the source on
        // the pump thread, and StopAllFiles disposes sources for Hashing files too.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger);

        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(FileCount);

        scheduler.StopAll();
        await WaitFor(() => package.All(f => f.State == FileState.Cancelled));

        await launcher.ReleaseAndDrainAsync(scheduler.DrainAsync);

        AssertNoDisposedTokenSource(package, logger);
    }

    [Fact]
    public async Task StopAll_WhenTheHashFinishedJustBeforeTheStop_DoesNotQueueTheUploadAnyway()
    {
        // Cancelling a worker does not recall its completion. If the hash has already SUCCEEDED and
        // its callback is sitting in the queue behind the stop, that callback used to run as though
        // nothing had happened: it moved the row to UploadQueued and FillSlots started uploading the
        // file the user had just stopped.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger, fileCount: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(1);

        // Hold the pump so the ordering below is exact rather than lucky: the stop is queued first,
        // then the hash worker runs to completion with a token that is still live and posts its
        // SUCCESS behind the stop. That is the window, reproduced deterministically.
        using PumpBlock block = new(scheduler);
        scheduler.StopAll();
        await launcher.ReleaseAsync(0);

        block.Release();
        await scheduler.DrainAsync();

        Assert.Equal(FileState.Cancelled, file.State);
        Assert.Equal(1, launcher.HeldCount); // no upload worker was launched for the stopped file
    }

    [Fact]
    public async Task PauseAll_WhenTheWorkerStartsAfterThePause_ParksEveryRowAsPausedNotFailed()
    {
        // Pause is where the race hurts most. PauseRunningFiles cancels and disposes the source but
        // deliberately leaves the row Uploading — "State will transition to Paused in the completion
        // callback". A worker that threw ObjectDisposedException took the crashed branch instead, so
        // the callback marked it Failed: pausing a full queue left a screen of red rows.
        GatedPipeline pipeline = new("Rapidgator");
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger);

        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(FileCount);

        scheduler.PauseAll();
        await WaitFor(() => scheduler.IsPaused);

        // Pause leaves the rows Uploading until each worker's completion callback parks them, so
        // releasing the workers is what produces the final states.
        await launcher.ReleaseAndDrainAsync(scheduler.DrainAsync);
        await WaitFor(() => package.All(f => f.State is FileState.Paused or FileState.Failed));

        AssertNoDisposedTokenSource(package, logger);
        Assert.All(package, f => Assert.Equal(FileState.Paused, f.State));
    }

    [Fact]
    public async Task CompletionCallback_FromAnAttemptTheUserAlreadyRestarted_LeavesTheNewAttemptAlone()
    {
        // Stop, then restart before the stopped attempt has finished unwinding. The old attempt's
        // completion callback is already queued and knows nothing about the new one — but it ends up
        // running against whatever the row holds by then.
        GatedPipeline pipeline = new("Rapidgator");
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger, fileCount: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(1);

        scheduler.StopAll();
        await WaitFor(() => file.State == FileState.Cancelled);

        // The user changes their mind. Attempt B installs its own source and takes the row back.
        scheduler.StartAll();
        await launcher.WaitForHeldAsync(2);
        Assert.Equal(FileState.Uploading, file.State);
        CancellationTokenSource attemptB = file.Cts!;
        Assert.NotNull(attemptB);

        // Only now does attempt A finish unwinding and post its completion.
        await launcher.ReleaseAsync(0);
        await scheduler.DrainAsync();

        // A's callback belongs to an attempt that no longer owns this row, so it must leave B's
        // source in place: dropping it makes B unstoppable — a ghost upload the user cannot cancel
        // behind a row that claims to be finished.
        Assert.Same(attemptB, file.Cts);

        // ...and must not have disposed it either. Reading Token throws once a source is disposed,
        // and a disposed-but-uncancelled source is the one state where the pipeline's own
        // cancellation registration can still throw ObjectDisposedException.
        Assert.False(attemptB.Token.IsCancellationRequested);

        // ...nor overwrite B's state with A's outcome.
        Assert.Equal(FileState.Uploading, file.State);
    }

    [Fact]
    public async Task HashCompletionCallback_FromAnAttemptTheUserAlreadyRestarted_LeavesTheNewAttemptAlone()
    {
        // The hashing half of the same restart window. Worth its own test because a stale hash
        // completion has an extra way to do damage: on success it does not merely mark the row, it
        // LAUNCHES the upload — so an unguarded one could start a second attempt for a file that
        // already has one running.
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        RecordingLogger logger = new();
        (UploadScheduler scheduler, Package package, HeldWorkLauncher launcher) = Build(pipeline, logger, fileCount: 1);
        PackageFile file = package.Single();

        scheduler.AddPackage(package);
        await launcher.WaitForHeldAsync(1);
        Assert.Equal(FileState.Hashing, file.State);

        scheduler.StopAll();
        await WaitFor(() => file.State == FileState.Cancelled);

        scheduler.StartAll();
        await launcher.WaitForHeldAsync(2);
        Assert.Equal(FileState.Hashing, file.State);
        CancellationTokenSource attemptB = file.Cts!;
        Assert.NotNull(attemptB);

        await launcher.ReleaseAsync(0);
        await scheduler.DrainAsync();

        Assert.Same(attemptB, file.Cts);
        Assert.False(attemptB.Token.IsCancellationRequested); // throws if A's callback disposed it
        Assert.Equal(FileState.Hashing, file.State);

        // And no third worker: the stale completion must not have launched an upload alongside B.
        Assert.Equal(2, launcher.HeldCount);
    }

    /// <summary>
    /// Checks the symptom on both surfaces it reaches — the row's own <see cref="PackageFile.Error"/>
    /// and the crash the scheduler logs — and fails with the offending lines attached. The log is
    /// worth checking separately: <see cref="PackageFile.Error"/> is one state transition away from
    /// being overwritten, the log line is not.
    /// </summary>
    private static void AssertNoDisposedTokenSource(Package package, RecordingLogger logger)
    {
        const string Symptom = "CancellationTokenSource has been disposed";

        string[] hits =
        [
            .. package
                .Where(f => f.Error?.Contains(Symptom, StringComparison.Ordinal) == true)
                .Select(f => $"{f.Name}: {f.Error}"),
            .. logger.Messages.Where(m => m.Contains(Symptom, StringComparison.Ordinal)),
        ];

        Assert.True(
            hits.Length == 0,
            $"A stop disposed a CancellationTokenSource out from under {hits.Length} worker(s). " +
            $"Read the token before handing work to the launcher:{Environment.NewLine}" +
            string.Join(Environment.NewLine, hits.Take(5)));
    }

    private static async Task WaitFor(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), $"condition was not met within the timeout: {expression}");
    }

    private (UploadScheduler Scheduler, Package Package, HeldWorkLauncher Launcher) Build(GatedPipeline pipeline, IAppLogger logger, int fileCount = FileCount)
    {
        _pipelines.Add(pipeline);

        // No admission limit is in play — every file must launch in one FillSlots pass so the stop
        // that follows meets the whole burst at once.
        AppSettings settings = new()
        {
            MaxConcurrentUploadJobs = FileCount,
            MaxConcurrentCPUJobs = FileCount,
        };
        DefaultFileHosterRegistry registry = new([pipeline]);
        UploadScheduler scheduler = new(settings, BuildAttemptRunner(registry), logger, new CSUploader.Lib.Crypto.HashingService(), registry);

        HeldWorkLauncher launcher = new();
        scheduler.WorkLauncher = launcher.Launch;

        scheduler.Start(); // PackageManager does this in production; here we drive the scheduler directly.
        _harnesses.Add((scheduler, launcher));

        FileHosterClient hoster = new(pipeline.Name, Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = pipeline.Name, IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = "p",
            Logger = logger,
            Settings = settings,
            FileHosters = new() { { hoster, login } },
        };
        Package package = new(options);
        PackageFile[] files = [.. Enumerable.Range(0, fileCount).Select(i => MakeFile(package, hoster, login, $"f{i}.bin"))];
        package.AddPackageFiles(files);

        return (scheduler, package, launcher);
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
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    /// <summary>
    /// Stands in for <c>Task.Run</c> in the scheduler: holds every worker it is handed until the
    /// test releases them, then runs them all and hands back one task that completes when the last
    /// one has unwound.
    /// </summary>
    /// <remarks>
    /// This is what makes the race deterministic. The scheduler has finished launching — file
    /// Uploading, source created and published to <c>file.Cts</c> — but no worker has executed a
    /// single line, which is precisely the window a stop closes on. Release is one-way and
    /// idempotent, so teardown can call it after a test already did.
    /// </remarks>
    private sealed class HeldWorkLauncher
    {
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Task> _started = [];
        private readonly Lock _lock = new();

        public int HeldCount
        {
            get
            {
                lock (_lock)
                {
                    return _gates.Count;
                }
            }
        }

        public Task Launch(Func<Task> work)
        {
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task started = RunWhenReleasedAsync(gate, work);
            lock (_lock)
            {
                _gates.Add(gate);
                _started.Add(started);
            }

            return started;
        }

        public async Task WaitForHeldAsync(int count)
        {
            await WaitFor(() => HeldCount >= count);
            Assert.Equal(count, HeldCount);
        }

        /// <summary>
        /// Releases a single worker by launch order and waits for it to unwind — which is how a
        /// test arranges for one attempt to finish while a later attempt is still held.
        /// </summary>
        public async Task ReleaseAsync(int index)
        {
            Task started;
            lock (_lock)
            {
                _gates[index].TrySetResult();
                started = _started[index];
            }

            await started;
        }

        /// <summary>
        /// Releases every held worker and returns once they — and anything their completions went
        /// on to launch — have finished.
        /// </summary>
        /// <param name="pumpBarrier">
        /// The scheduler's <c>DrainAsync</c>. Awaiting the workers is not enough on its own: a
        /// finished worker only POSTS its completion, and it is that callback, running later on the
        /// pump, which launches any follow-up work (a completed hash queues its upload). Without
        /// running the pump in between, the loop would check for new workers before the thing that
        /// creates them has run, and return early.
        /// </param>
        public async Task ReleaseAndDrainAsync(Func<Task> pumpBarrier)
        {
            while (true)
            {
                Task[] pending;
                lock (_lock)
                {
                    foreach (TaskCompletionSource gate in _gates)
                    {
                        gate.TrySetResult();
                    }

                    pending = [.. _started];
                }

                await Task.WhenAll(pending);
                await pumpBarrier();

                lock (_lock)
                {
                    if (_started.Count == pending.Length)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task RunWhenReleasedAsync(TaskCompletionSource gate, Func<Task> work)
        {
            await gate.Task;
            await work();
        }
    }

    /// <summary>
    /// Occupies the scheduler's single consumer, so a test can queue actions behind it and control
    /// the exact order the pump sees them in.
    /// </summary>
    private sealed class PumpBlock : IDisposable
    {
        private readonly ManualResetEventSlim _held = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public PumpBlock(UploadScheduler scheduler)
        {
            scheduler.PostFileMutation(() =>
            {
                _held.Set();
                _release.Wait();
            });

            Assert.True(_held.Wait(TimeSpan.FromSeconds(10)), "the pump never picked up the blocking action");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            // Release on the way out too: a failed assertion must not leave the pump wedged and
            // hang the rest of the class in teardown.
            _release.Set();
            _held.Dispose();
            _release.Dispose();
        }
    }

    /// <summary>
    /// Upload blocks in the Uploading state until the test releases it (or the attempt is
    /// cancelled), so a released worker cannot race ahead and finish before it is stopped.
    /// </summary>
    private sealed class GatedPipeline(string name, bool requiresHash = false) : IFileHosterPipeline
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private int _releasedAll;

        public string Name { get; } = name;

        public bool RequiresHashingBeforeUpload { get; } = requiresHash;

        public bool RequiresHashingAfterUpload => false;

        public long? MaxFileSize => null;

        public int? MaxFilesPerPackage => null;

        /// <summary>
        /// Opens every gate, including ones that do not exist yet.
        /// </summary>
        /// <remarks>
        /// Sticky on purpose. Teardown releases the pipeline before it releases the held workers,
        /// and a worker released afterwards can still reach this pipeline for the first time — a
        /// held hash worker that finally succeeds makes the scheduler launch its upload, which
        /// arrives here looking for a gate created after this ran. Without the flag that upload
        /// parks forever and teardown never returns.
        /// </remarks>
        public void ReleaseAll()
        {
            Interlocked.Exchange(ref _releasedAll, 1);
            foreach (TaskCompletionSource tcs in _gates.Values)
            {
                tcs.TrySetResult();
            }
        }

        public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
            => Task.FromResult(new AccountCheckResult(true, AccountType.Free, "ok"));

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TransferStarted(ctx.FileSize);

            using (ct.Register(() => Gate(ctx.FileName).TrySetCanceled()))
            {
                await Gate(ctx.FileName).Task;
            }

            yield return new TransferCompleted("https://gated/" + ctx.FileName);
        }

        private TaskCompletionSource Gate(string fileName)
        {
            TaskCompletionSource gate = _gates.GetOrAdd(fileName, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            // Checked after the add, and ReleaseAll sets the flag before its sweep — so either the
            // sweep sees this gate or this sees the flag. Neither order can leave it closed.
            if (Volatile.Read(ref _releasedAll) == 1)
            {
                gate.TrySetResult();
            }

            return gate;
        }
    }

    /// <summary>
    /// Captures the scheduler's log so the crash text itself is available to assert on.
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<string> _messages = [];

        public event LogEventHandler? OnLogOutput;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public void Log(
            object? sender,
            LogType logType,
            string text,
            HttpTransaction? httpTransaction = null,
            string filePath = "",
            string function = "",
            int lineNumber = 0)
        {
            lock (_messages)
            {
                _messages.Add($"[{logType}] {text}");
            }

            _ = OnLogOutput; // nothing subscribes in these tests; recording is the whole job
        }
    }
}
