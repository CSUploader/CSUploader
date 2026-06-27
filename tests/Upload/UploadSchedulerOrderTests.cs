// <copyright file="UploadSchedulerOrderTests.cs" company="CSUploader">
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
/// Verifies the per-file QueueOrder behaviour in <see cref="UploadScheduler"/>: <c>FillSlots</c>
/// admits files in ascending QueueOrder, the move API renumbers the queue dense 1..N, and a file
/// finishing re-densifies the remaining non-terminal set so "next" is always #1. Uses the same
/// gated-pipeline harness as the force-start tests so the running set is deterministic.
/// </summary>
public sealed class UploadSchedulerOrderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<UploadScheduler> _schedulers = [];
    private readonly List<GatedPipeline> _pipelines = [];

    public UploadSchedulerOrderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"csu-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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
    public async Task FillSlots_PicksLowestQueueOrderFirst()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 1, fileCount: 3);

        scheduler.AddPackage(package);

        // With one upload slot, exactly the lowest-QueueOrder file runs.
        await WaitFor(() => CountState(package, FileState.Uploading) == 1);
        PackageFile running = package.Single(f => f.State == FileState.Uploading);
        int minOrder = package.Min(f => f.QueueOrder);
        Assert.Equal(minOrder, running.QueueOrder);

        // Move a different (still-queued) file to position 1, then finish the running one.
        PackageFile moved = package.First(f => f.State == FileState.UploadQueued);
        scheduler.MoveFileTo(moved, 1);
        await WaitFor(() => moved.QueueOrder == 1);

        pipeline.Complete(running.Name);
        await WaitFor(() => running.State == FileState.Completed);

        // The moved file (now #1) must be the one that runs next.
        await WaitFor(() => moved.State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, moved.State);
    }

    [Fact]
    public async Task FillSlots_HashingFileAhead_ReservesItsUploadSlot()
    {
        // A hash-required file ahead in the queue (Alfafile/Rapidgator — here stuck Hashing) must
        // RESERVE its upload slot. Without this, the later no-hash files grab every slot while the
        // (fast) hash runs, stranding the earlier file behind a full queue — the reported
        // "#1 stays queued while #2.. upload".
        GatedPipeline pipeline = new("Rapidgator", requiresHash: true);
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 2, fileCount: 3);
        PackageFile[] files = [.. package];

        // #1 stuck Hashing; #2 and #3 ready to upload. (Non-idle states survive AddPackage.)
        files[0].State = FileState.Hashing; files[0].QueueOrder = 1;
        files[1].State = FileState.UploadQueued; files[1].QueueOrder = 2;
        files[2].State = FileState.UploadQueued; files[2].QueueOrder = 3;

        scheduler.AddPackage(package); // runs FillSlots; non-idle files are left as-is

        // Two slots: #1 (Hashing) reserves one, #2 uploads — and #3 must NOT be promoted into the
        // slot held for #1.
        await WaitFor(() => files[1].State == FileState.Uploading);
        Assert.Equal(FileState.Uploading, files[1].State);
        Assert.Equal(FileState.Hashing, files[0].State);      // #1 still hashing, its slot held
        Assert.Equal(FileState.UploadQueued, files[2].State); // #3 waits — slot reserved for #1
    }

    [Fact]
    public async Task MoveFileTo_RenumbersDense()
    {
        const int n = 4;
        GatedPipeline pipeline = new("Rapidgator");
        // maxUploads 0 → nothing starts, so every file stays in the non-terminal queue.
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 0, fileCount: n);

        scheduler.AddPackage(package);
        await WaitFor(() => package.All(f => f.QueueOrder > 0));

        PackageFile first = package.Single(f => f.QueueOrder == 1);
        scheduler.MoveFileTo(first, n);
        await WaitFor(() => first.QueueOrder == n);

        // Dense 1..N over all files, and the moved file is last.
        int[] orders = [.. package.Select(f => f.QueueOrder).OrderBy(o => o)];
        Assert.Equal(Enumerable.Range(1, n), orders);
        Assert.Equal(n, first.QueueOrder);
    }

    [Fact]
    public async Task OnComplete_RenumbersRemainingToStartAtOne()
    {
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 1, fileCount: 3);

        scheduler.AddPackage(package);
        await WaitFor(() => CountState(package, FileState.Uploading) == 1);

        PackageFile running = package.Single(f => f.State == FileState.Uploading);
        Assert.Equal(1, running.QueueOrder);

        pipeline.Complete(running.Name);
        await WaitFor(() => running.State == FileState.Completed);

        // The two remaining non-terminal files re-densify to 1 and 2.
        await WaitFor(() =>
        {
            int[] remaining = [.. package
                .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
                .Select(f => f.QueueOrder)
                .OrderBy(o => o)];
            return remaining.SequenceEqual(new[] { 1, 2 });
        });

        int[] remainingOrders = [.. package
            .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
            .Select(f => f.QueueOrder)
            .OrderBy(o => o)];
        Assert.Equal(new[] { 1, 2 }, remainingOrders);
    }

    [Fact]
    public async Task RequeueTerminalFile_AppendsToEndAndClearsStaleOrder()
    {
        // A Failed file carrying a STALE low QueueOrder (colliding with a live position) must, when
        // re-queued via StartAll → RequeueStartableFiles, be appended to the END rather than
        // re-entering at its stale spot. The other non-terminal files keep a dense 1..N with no
        // duplicates. maxUploads 0 → nothing launches, so the QueueOrder assertions are stable.
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 0, fileCount: 3);
        PackageFile[] files = [.. package];

        // Two live, dense, non-terminal files...
        files[1].State = FileState.UploadQueued;
        files[1].QueueOrder = 1;
        files[2].State = FileState.UploadQueued;
        files[2].QueueOrder = 2;

        // ...and a Failed file whose stale QueueOrder COLLIDES with files[1]'s live position.
        files[0].State = FileState.Failed;
        files[0].QueueOrder = 1;

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.StartAll(); // RequeueStartableFiles → resets the Failed file's QueueOrder to 0

        // The retried file ends up appended (highest QueueOrder) and the whole non-terminal set is
        // a contiguous 1..3 permutation — no duplicates, no stale collision.
        await WaitFor(() =>
            files[0].QueueOrder == 3 &&
            IsContiguousPermutation([.. package.Select(f => f.QueueOrder)], 3));

        int[] orders = [.. package.Select(f => f.QueueOrder)];
        Assert.True(files[0].QueueOrder > files[1].QueueOrder, "retried file must sort after the others");
        Assert.True(files[0].QueueOrder > files[2].QueueOrder, "retried file must sort after the others");
        Assert.Equal(Enumerable.Range(1, 3), [.. orders.OrderBy(o => o)]); // dense 1..3, no duplicates
    }

    [Fact]
    public async Task RenumberQueue_UnplacedZeroFile_AppendsToEndNotFront()
    {
        // A non-terminal file carrying QueueOrder==0 (the "unplaced/append" sentinel — e.g. set
        // off-loop by a Reset/retry/force-start) must, when a RenumberQueue runs, sort to the END
        // (largest QueueOrder) rather than folding to position 1. Drive the renumber by completing
        // a different running file (OnUploadCompleted → RenumberQueue). maxUploads 1 so exactly one
        // file runs and the rest stay queued, keeping QueueOrder assertions stable.
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 1, fileCount: 4);

        // Let the scheduler queue all four and launch the lowest one (dense 1..4 via EnsureQueueOrdered).
        scheduler.AddPackage(package);
        await WaitFor(() => CountState(package, FileState.Uploading) == 1 && package.All(f => f.QueueOrder > 0));

        PackageFile running = package.Single(f => f.State == FileState.Uploading);

        // Plant the "unplaced" sentinel on a still-queued file, off the scheduler loop (mimicking a
        // Reset/retry that sets QueueOrder=0). It must NOT be the running one (whose completion drives
        // the renumber). With Fix 2 this 0 sorts LAST on the next RenumberQueue, not to the front.
        PackageFile zeroed = package.First(f => f.State == FileState.UploadQueued);
        zeroed.QueueOrder = 0;

        // Finish the running file → OnUploadCompleted fires RenumberQueue over the remaining set.
        pipeline.Complete(running.Name);
        await WaitFor(() => running.State == FileState.Completed);

        // The three remaining non-terminal files re-densify to a contiguous 1..3, and the file that
        // was 0 is appended (the LARGEST of the three), never position 1.
        await WaitFor(() =>
        {
            int[] remaining = [.. package
                .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
                .Select(f => f.QueueOrder)
                .OrderBy(o => o)];
            return remaining.SequenceEqual(new[] { 1, 2, 3 });
        });

        PackageFile[] remainingFiles = [.. package
            .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))];
        int maxRemaining = remainingFiles.Max(f => f.QueueOrder);
        Assert.Equal(maxRemaining, zeroed.QueueOrder); // appended to the END
        Assert.NotEqual(1, zeroed.QueueOrder);         // NOT folded to the front
        Assert.All(remainingFiles.Where(f => f != zeroed), f => Assert.True(f.QueueOrder < zeroed.QueueOrder));
    }

    [Fact]
    public async Task ForceStart_CompletedNoHashReupload_AppendsWithAssignedPosition()
    {
        // Force-starting a re-upload of a Completed (no-hash) file sets QueueOrder=0 ("append") and
        // launches the upload directly. The single RenumberQueue() after the force-start loop must
        // give it a real appended position immediately: it ends with the MAX QueueOrder (not the
        // front) and OrderDisplay would show a number (QueueOrder > 0). maxUploads 0 so the other
        // files stay queued and the QueueOrder assertions are stable.
        GatedPipeline pipeline = new("Rapidgator");
        (UploadScheduler scheduler, Package package) = Build(pipeline, maxUploads: 0, fileCount: 3);
        PackageFile[] files = [.. package];

        // Two queued, dense, non-terminal files...
        files[1].State = FileState.UploadQueued;
        files[1].QueueOrder = 1;
        files[2].State = FileState.UploadQueued;
        files[2].QueueOrder = 2;

        // ...and a Completed file we'll force-start a re-upload of.
        files[0].State = FileState.Completed;
        files[0].QueueOrder = 0;
        files[0].IsUploadFinished = true;
        files[0].FileUrl = "https://done/old";

        scheduler.AddPackage(package, scheduleIdleFiles: false);
        scheduler.ForceStart([files[0]]);

        // The re-upload launches (Uploading, over the 0 limit) AND gets a dense appended position.
        await WaitFor(() => files[0].State == FileState.Uploading && files[0].QueueOrder > 0);

        int maxOrder = package
            .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
            .Max(f => f.QueueOrder);
        Assert.Equal(maxOrder, files[0].QueueOrder); // appended to the END, not the front
        Assert.True(files[0].QueueOrder > files[1].QueueOrder);
        Assert.True(files[0].QueueOrder > files[2].QueueOrder);

        // The whole non-terminal set is a contiguous 1..3 — a number would show in OrderDisplay.
        int[] orders = [.. package
            .Where(f => f.State is not (FileState.Completed or FileState.Failed or FileState.Cancelled))
            .Select(f => f.QueueOrder)
            .OrderBy(o => o)];
        Assert.Equal(Enumerable.Range(1, 3), orders);
    }

    private static bool IsContiguousPermutation(int[] orders, int n)
        => orders.Length == n && orders.OrderBy(o => o).SequenceEqual(Enumerable.Range(1, n));

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
        scheduler.Start();
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
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(() => new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
        return new AttemptRunner(registry, proxy.Object, hf.Object);
    }

    /// <summary>
    /// Test pipeline whose upload blocks in the Uploading state until <see cref="Complete"/> is
    /// called for that file (keyed by file name). Mirrors the harness in
    /// <c>UploadSchedulerForceStartTests</c>.
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
