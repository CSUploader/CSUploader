# Parallel Chunk Upload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Revision 8** — r7's review found the ordered wait was unbounded, so a regression would HANG the suite rather than fail it. Bounded. See "What changed in r8".

**Goal:** Upload the parts of a single large file concurrently, on the five hosters whose protocols make parts order-independent, so one big file is no longer limited to one connection's throughput.

**Architecture:** Each qualifying hoster currently PUTs its parts in a sequential loop. Introduce a shared `ParallelPartUploader` that runs them with bounded concurrency, backed by three things the sequential path never needed: independent per-part readers (today all parts share one `FileStream` whose position advances as slices are consumed), a serialized progress aggregator (today progress is an absolute `basePosition + bytesInChunk`, meaningful only in order), and a lowest-index primary-failure policy. Degree of parallelism is a per-hoster capability defaulting to 1.

**Tech Stack:** C# / .NET 10, xUnit, `SemaphoreSlim`, `System.IO.RandomAccess`, `ExceptionDispatchInfo`.

**Spec:** This plan.

## HARD PREREQUISITE

**SATISFIED.** `2026-08-23-shared-speed-limit-budget.md` is implemented on branch `fix/shared-speed-limit-budget`: `ThrottledStream` now draws from a shared `SpeedBudget`, 2360 Core and 532 headless tests pass, and the four-streams-at-100 kB/s test that measured 394 kB/s passes. This gate existed because all of a file's parts draw on that file's budget — before the fix a budget was a per-*stream* rate, so N parallel parts would each get the full limit and a user's 1 MB/s would become N MB/s inside a single file.

## Task 0 — DONE. The gate is passed.

Measured against live VikingFile on 2026-08-23 with `scripts/parallel-part-probe.cs`: the same 120 MiB pushed to presigned R2 part URLs at increasing concurrency, `complete-upload` never called so nothing was published.

| degree | MiB/s | vs degree 1 |
|---|---|---|
| 1 | 12.8 | 1.00x |
| 2 | 18.3 | 1.38x |
| 4 | 21.4 | 1.66x |
| 8 | **32.9** | **2.57x** |

**Conclusion: these hosts throttle per connection.** A single connection saturates near 13 MiB/s while eight reach 33 MiB/s, and throughput had *not* plateaued at 8. A first, shorter run (24 MiB) showed only 1.35x — TCP slow-start dominates a four-second transfer, so short benchmarks understate the gain and must not be used.

**This changes a design default from r1:** the ceiling is higher than the 4 r1 guessed. Default degree is **4** with a user setting allowing up to **8**; 4 is the conservative default because degree 8 multiplies memory and open handles across concurrent files (see Task 4).

## Background: verified findings

- **Nothing is parallel today.** Every part loop is sequential with the `await` inside: `StorageToPipeline:276`, `VikingFilePipeline:256`, `HostizePipeline:272`, `UploadNowPipeline:287-368`, `DataNodesPipeline:283`.
- **Five hosters are parallel-safe** (r1 said four and missed DataNodes):

| Hoster | Mechanism | Enabled? |
|---|---|---|
| VikingFile | server-issued presigned R2 part URLs | yes, anonymous |
| Hostize | server-issued presigned S3 part URLs | yes, anonymous |
| storage.to | server-issued presigned R2 part URLs | **no** — disabled `ServiceRegistration.cs:350` |
| UploadNow | signs each S3 part request on demand (not a URL list) | yes, anonymous |
| DataNodes | `put_chunk_mt.cgi` with `X-Seek-To` byte offsets | yes |

- **DataNodes disproves r1's blanket claim** that XFileSharing-shaped hosts can never parallelise. Its own source says `X-Seek-To` means "the server doesn't rely on arrival order" (`DataNodesPipeline.cs:287`) and that the host's own uploader "sends 1 MiB chunks and up to ten at once" (`:49`). It is `xfspro`-family.
- **Filebin is correctly excluded** — one raw POST body, no parts.
- **GigaFile is deferred, not ruled out.** Only chunk zero provably must run first, to obtain cookies (`GigaFilePipeline.cs:244`). r1's "can never parallelise" was stronger than the source supports. Out of scope here; revisit separately.

### Three hazards the sequential path hides

1. **A shared `FileStream`.** `VikingFilePipeline.cs:252` opens ONE stream and wraps each part in `ChunkSliceStream(fs, len)`, which documents that the position "advances naturally as each slice is consumed" and whose `CanSeek` is false. Two parts reading it concurrently receive nondeterministic regions. `UploadNowPipeline.cs:285` is worse: the same stream also serves an MD5 pre-pass that explicitly moves the position (`:659`).
2. **Absolute progress.** `HttpHandler.cs:842` reports `basePosition + bytesInThisChunk`. `OperationProgressEventArgs.cs:124` derives bytes-remaining, percentage, elapsed, speed, ETA and finish time from it, and `PackageFile.cs:412` derives the UI's own numbers again. Out of order, that value moves backwards.
3. **First-fault ambiguity.** With one part in flight the first error is unambiguous. With N, `Task.WhenAll` surfaces only one fault and discards the rest — and `AttemptRunner.cs:214` decides retryability from the exception it sees.

## Global Constraints

- Target framework `net10.0`; nullable enabled; match surrounding style.
- Copyright header on every new file; UTF-8 **without** BOM; CRLF.
- XML doc comments explain *why*, in the codebase's voice.
- **Default degree is 1 and every non-opted-in hoster keeps byte-identical behaviour**, including the shared-`FileStream` path and the stop-on-first-rejected-part behaviour.
- Failure semantics must not weaken. The honest promise is **"no published completed object"**, not "nothing committed" — successfully uploaded S3/R2 parts do persist under an incomplete multipart.
- The speed limit must hold across all of a file's parts (see HARD PREREQUISITE).

---

### Task 1: `FileSliceReader` — independent readers over one stable handle

**Files:**
- Create: `src/CSUploader.Core/Lib/Net/Http/FileSliceReader.cs`
- Test: `tests/Lib/Net/Http/FileSliceReaderTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class FileSliceReader : IDisposable
{
    public FileSliceReader(string path);
    public long FileLength { get; }
    public Stream OpenSlice(long offset, long length);
}
```

**Why an anchor handle (r1's #13).** r1 opened and closed a `FileStream` per part. Between waves — and at degree 1 always — there would be no open handle at all, letting another process replace the file mid-transfer and yielding a multipart assembled from two different versions. Today VikingFile holds one sharing lock across the whole loop (`:252`) and that property must survive. `FileSliceReader` holds one handle open for the transfer and serves each slice through `RandomAccess`, which is offset-addressed and needs no shared position.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Slices_ReadTheirOwnRegions_Concurrently()
{
    string path = WriteTempFile(patternBytes: 8192);
    using FileSliceReader reader = new(path);

    byte[][] halves = await Task.WhenAll(
        DrainAsync(reader.OpenSlice(0, 4096)),
        DrainAsync(reader.OpenSlice(4096, 4096)));

    Assert.Equal(Expected(0, 4096), halves[0]);
    Assert.Equal(Expected(4096, 4096), halves[1]);
}

[Fact]
public void OpenSlice_CanBeCalledTwiceForTheSameRegion()
{
    // UploadNow retries a part by re-invoking the delegate; a consumed slice would send EOF.
    string path = WriteTempFile(patternBytes: 4096);
    using FileSliceReader reader = new(path);

    Assert.Equal(1024, reader.OpenSlice(0, 1024).Length);
    Assert.Equal(1024, reader.OpenSlice(0, 1024).Length);
}

[Fact]
public void OpenSlice_RejectsRangesOutsideTheFile()
{
    string path = WriteTempFile(patternBytes: 1024);
    using FileSliceReader reader = new(path);

    Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(-1, 10));
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(0, -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => reader.OpenSlice(512, 1024));
}

[Fact]
public void FileLength_IsTheWholeFile_WhileASliceLengthIsItsOwn()
{
    string path = WriteTempFile(patternBytes: 8192);
    using FileSliceReader reader = new(path);

    Assert.Equal(8192, reader.FileLength);
    Assert.Equal(1024, reader.OpenSlice(4096, 1024).Length); // HttpContent uses this for Content-Length
}
```

- [x] **Step 2: Run to verify it fails** — type does not exist.

- [x] **Step 3: Implement**

```csharp
/// <summary>
/// Hands out independent readers over regions of one file, all backed by a single open handle.
/// <para>
/// This is the parallel counterpart to <see cref="ChunkSliceStream"/>, which deliberately shares one
/// caller-owned <see cref="FileStream"/> and rides its advancing position — correct and
/// allocation-free in order, and wrong the moment two parts are in flight, because both would move
/// the same position.
/// </para>
/// <para>
/// One ANCHOR handle is held for the whole transfer rather than opening per part, so the source file
/// cannot be swapped underneath a multi-part upload; slices read through
/// <see cref="RandomAccess"/>, which is offset-addressed and shares no position. Slices are
/// re-openable because a retried part must re-send its bytes, not EOF.
/// </para>
/// </summary>
public sealed class FileSliceReader : IDisposable
{
    private readonly SafeFileHandle _handle;

    public FileSliceReader(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        FileLength = RandomAccess.GetLength(_handle);
    }

    public long FileLength { get; }

    public Stream OpenSlice(long fileOffset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // Written as a subtraction, not `fileOffset + length > FileLength`: the addition can
        // overflow to negative and sail through the check.
        if (fileOffset > FileLength || length > FileLength - fileOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The slice extends past the end of the file.");
        }

        return new Slice(_handle, fileOffset, length);
    }

    public void Dispose() => _handle.Dispose();

    private sealed class Slice(SafeFileHandle handle, long fileOffset, long length) : Stream
    {
        // Captured explicitly. In the sync override the `Stream.Read` parameter named `offset` is
        // the BUFFER offset and shadows the constructor's FILE offset — r2 wrote
        // `RandomAccess.Read(..., offset + _read)` and so read from the buffer offset as though it
        // were a file position. A slice starting at 4096 read into buffer[20] read file byte 20.
        private readonly long _fileOffset = fileOffset;
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            // Validate FIRST. Computing `allowed` up front swallows a bad call: count == -1 makes
            // `allowed` negative, the guard below returns 0, and the caller sees a silent EOF where
            // the Stream contract requires ArgumentOutOfRangeException.
            ValidateBufferArguments(buffer, bufferOffset, count);

            int allowed = (int)Math.Min(count, length - _read);
            if (allowed <= 0)
            {
                return 0;
            }

            int n = RandomAccess.Read(handle, buffer.AsSpan(bufferOffset, allowed), _fileOffset + _read);
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int allowed = (int)Math.Min(buffer.Length, length - _read);
            if (allowed <= 0)
            {
                return 0;
            }

            int n = await RandomAccess.ReadAsync(handle, buffer[..allowed], _fileOffset + _read, cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int o, int c) => throw new NotSupportedException();
    }
}
```

**On `FileOptions.Asynchronous` with the sync `RandomAccess.Read`:** confirmed acceptable. Production reads go through `ReadAsync`; the sync override exists only to satisfy `Stream`, and on .NET 10 it blocks while the overlapped operation completes — overhead, not corruption.

**This test is mandatory and must be written before the fix**, because it is the one that catches the shadowing bug:

```csharp
[Fact]
public void Read_UsesTheFileOffset_NotTheBufferOffset()
{
    // r2's bug in one assertion: slice at file offset 4096, read into buffer position 20.
    string path = WriteTempFile(patternBytes: 8192);
    using FileSliceReader reader = new(path);
    byte[] buffer = new byte[1024];

    int n = reader.OpenSlice(4096, 512).Read(buffer, 20, 512);

    Assert.Equal(512, n);
    Assert.Equal(Expected(from: 4096, count: 512), buffer[20..532]);
}
```

- [x] **Step 4: Run to verify it passes.**
- [x] **Step 5: Commit** — `feat(net): independent slice readers over one anchored file handle`

---

### Task 2: `PartProgressAggregator` — one serialized, monotonic total

**Files:**
- Create: `src/CSUploader.Core/Upload/Pipeline/PartProgressAggregator.cs`
- Test: `tests/Upload/Pipeline/PartProgressAggregatorTests.cs`

**Interfaces:**
- Produces: `public sealed class PartProgressAggregator(int partCount, Action<long> publish)` with
  `public void Report(int partIndex, long cumulativeBytesInThatPart)`.

**Why this shape.** Returning a sum and letting the caller publish makes update, scan, return and publish four separate steps, so one thread can compute 10, pause while another publishes 30, then publish its stale 10 — progress going backwards, which is the entire bug. Totals are therefore computed and enqueued under one lock and drained outside it.

**There is no `ResetPart`.** An earlier revision had one, to handle UploadNow retrying a part internally (`WithStorageRetryAsync`, `:204`). It was wrong: subtracting a retried part's contribution makes the published total FALL, which is exactly what this class exists to prevent. Each part instead keeps a **high-water mark**, so a retry plateaus the total until the resent part passes its previous position.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Total_IsTheSumAcrossParts_NotTheLatestPartsAbsolutePosition()
{
    List<long> published = [];
    PartProgressAggregator aggregator = new(3, published.Add);

    aggregator.Report(2, 10);
    aggregator.Report(0, 5);
    aggregator.Report(1, 7);

    Assert.Equal([10, 15, 22], published);
}

[Fact]
public void Report_TreatsEachPartsValueAsCumulative_NotIncremental()
{
    List<long> published = [];
    PartProgressAggregator aggregator = new(2, published.Add);

    aggregator.Report(0, 100);
    aggregator.Report(0, 200);

    Assert.Equal([100, 200], published);
}

[Fact]
public void ARetriedPart_PlateausTheTotal_RatherThanDroppingIt()
{
    // UploadNow retries a part internally, restarting that part's counter near zero. The file's
    // total must NOT fall — OperationProgressEventArgs derives speed and ETA from it, and a
    // backwards jump is the whole defect. It plateaus until the resent part passes its old mark.
    List<long> published = [];
    PartProgressAggregator aggregator = new(2, published.Add);

    aggregator.Report(0, 100);
    aggregator.Report(1, 50);
    aggregator.Report(0, 10);   // retry restarts part 0
    aggregator.Report(0, 120);  // …and passes its previous high-water mark

    Assert.Equal([100, 150, 170], published); // no 60, and nothing published for the replay
    Assert.Equal(published.OrderBy(x => x), published);
}

[Fact]
public async Task PublishedTotals_NeverGoBackwards_UnderConcurrentParts()
{
    const int Parts = 8;
    const int Steps = 500;
    List<long> published = [];
    Lock sync = new();
    PartProgressAggregator aggregator = new(Parts, total =>
    {
        lock (sync)
        {
            published.Add(total);
        }
    });

    await Task.WhenAll(Enumerable.Range(0, Parts).Select(part => Task.Run(() =>
    {
        for (int step = 1; step <= Steps; step++)
        {
            aggregator.Report(part, step);
        }
    })));

    Assert.Equal(published.OrderBy(x => x), published);
    Assert.Equal(Parts * Steps, published[^1]);
}

[Fact]
public void APublisherThatThrows_DoesNotFailTheUpload_OrStallTheQueue()
{
    // The publish callback ends up in request-body serialization; a progress subscriber's
    // exception must never surface as an upload failure, and must not leave the drain latched.
    List<long> seen = [];
    PartProgressAggregator aggregator = new(1, total =>
    {
        seen.Add(total);
        throw new InvalidOperationException("subscriber blew up");
    });

    aggregator.Report(0, 10);
    aggregator.Report(0, 20);

    Assert.Equal([10, 20], seen);
}
```

- [x] **Step 2: Run to verify it fails.**

- [x] **Step 3: Implement**

```csharp
/// <summary>
/// Turns per-part byte counts into one file-level total while parts are in flight together.
/// <para>
/// The sequential path reported <c>basePosition + bytesInThisChunk</c>, an absolute file position
/// meaningful only when parts complete in order. Run together, that lurches backwards whenever a
/// lower-numbered part reports after a higher one — and <c>OperationProgressEventArgs</c> derives
/// speed, ETA and finish time from it.
/// </para>
/// <para>
/// Update and ENQUEUE happen under one lock; publication is drained outside it. Computing a
/// total and publishing it as two separate steps lets a thread publish a stale figure after a
/// newer one — the same defect wearing a different hat — so the queue, not the callback, is
/// what the lock protects.
/// </para>
/// </summary>
public sealed class PartProgressAggregator(int partCount, Action<long> publish)
{
    private readonly long[] _highWaterPerPart = new long[partCount];
    private readonly Lock _sync = new();
    private readonly Queue<long> _pending = new();
    private long _total;
    private bool _draining;

    /// <summary>
    /// Records this part's cumulative bytes and publishes the file-wide sum.
    /// <para>
    /// Each part keeps a HIGH-WATER MARK rather than its latest value. UploadNow retries a part
    /// internally (<c>WithStorageRetryAsync</c>), and the retry restarts that part's counter from
    /// near zero — subtracting the old contribution would make the file's total fall, which is the
    /// exact defect this class exists to prevent. With a high-water mark the total simply plateaus
    /// while the resent part catches up.
    /// </para>
    /// <para>
    /// Totals are computed AND queued under the lock, then drained by a single caller outside it.
    /// Publishing under the lock would run arbitrary subscriber code — ultimately the UI, via
    /// <c>OperationProgressEventArgs</c> — while holding it, which invites contention and deadlock
    /// and lets a subscriber's exception escape into request-body serialization, turning a progress
    /// failure into an upload failure.
    /// </para>
    /// </summary>
    public void Report(int partIndex, long cumulativeBytesInThatPart)
    {
        lock (_sync)
        {
            long previous = _highWaterPerPart[partIndex];
            if (cumulativeBytesInThatPart <= previous)
            {
                return; // a retry replaying ground already counted
            }

            _total += cumulativeBytesInThatPart - previous;
            _highWaterPerPart[partIndex] = cumulativeBytesInThatPart;
            _pending.Enqueue(_total);

            if (_draining)
            {
                return; // another thread owns the drain; ordering is preserved by the queue
            }

            _draining = true;
        }

        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            long next;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }

                next = _pending.Dequeue();
            }

            try
            {
                publish(next);
            }
            catch (Exception)
            {
                // A progress subscriber must never fail an upload. Swallow and keep draining, or
                // the queue stalls and the _draining flag is never cleared.
            }
        }
    }
}
```

- [x] **Step 4: Run to verify it passes.**
- [x] **Step 5: Commit** — `feat(upload): serialize part progress into one monotonic total`

---

### Task 3: `ParallelPartUploader` — bounded concurrency with a lowest-index primary failure

**Files:**
- Create: `src/CSUploader.Core/Upload/Pipeline/ParallelPartUploader.cs`
- Test: `tests/Upload/Pipeline/ParallelPartUploaderTests.cs`

**Interfaces:**

```csharp
public static class ParallelPartUploader
{
    public static Task<PartResult[]> RunAsync(
        int partCount,
        int degreeOfParallelism,
        Func<int, CancellationToken, Task<PartResult>> uploadPart,
        CancellationToken cancellationToken);
}

public readonly record struct PartResult(int PartNumber, string? ETag, string? Error);
```

**Two corrections from r1 (its #4 and #5).**

*Errors must stop the run, not just exceptions.* r1 only cancelled siblings when `uploadPart` **threw**, but the pipelines turn a non-2xx or missing ETag into a *successful* `PartResult` carrying an `Error`. So every remaining part kept uploading. Worse, r1's degree-1 branch never inspected `Error` either, so even the untouched path changed: today VikingFile returns on the first rejected part (`:271`), whereas r1 would upload every later part first. **Degree 1 must be byte-identical, and that means short-circuiting on `Error`.**

*The surfaced failure must be deterministic.* `Task.WhenAll` throws one fault and discards the rest, and `AttemptRunner.cs:214` decides retryability from the exception it sees — so which failure wins can silently suppress a valid retry. Record BOTH shapes — an error `PartResult` and a thrown fault — into one slot, select by **lowest part index** (not by which thread reached the lock first, which is scheduler-dependent), capture thrown faults with `ExceptionDispatchInfo`, and never let a throwing cancellation callback replace the record.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task RunAsync_KeepsResultsInPartOrder_HoweverTheyFinish()
{
    PartResult[] results = await ParallelPartUploader.RunAsync(4, 4,
        async (i, ct) => { await Task.Delay((4 - i) * 20, ct); return new PartResult(i + 1, $"etag-{i}", null); },
        CancellationToken.None);

    Assert.Equal(["etag-0", "etag-1", "etag-2", "etag-3"], results.Select(r => r.ETag));
}

[Fact]
public async Task RunAsync_NeverExceedsTheRequestedDegree()
{
    int running = 0;
    int peak = 0;
    object sync = new();

    await ParallelPartUploader.RunAsync(12, 3, async (i, ct) =>
    {
        lock (sync) { peak = Math.Max(peak, ++running); }
        await Task.Delay(20, ct);
        lock (sync) { running--; }
        return new PartResult(i + 1, "etag", null);
    }, CancellationToken.None);

    Assert.True(peak <= 3, $"peak concurrency was {peak}");
}

[Fact]
public async Task RunAsync_AtDegreeOne_StopsAtTheFirstErrorResult()
{
    // Byte-identical to today: VikingFile returns on the first rejected part.
    List<int> attempted = [];

    PartResult[] results = await ParallelPartUploader.RunAsync(5, 1, (i, ct) =>
    {
        attempted.Add(i);
        return Task.FromResult(i == 1
            ? new PartResult(i + 1, null, "rejected")
            : new PartResult(i + 1, "etag", null));
    }, CancellationToken.None);

    Assert.Equal([0, 1], attempted);
    Assert.Equal("rejected", Array.Find(results, r => r.Error is not null).Error);
}

[Fact]
public async Task RunAsync_WhenAPartReturnsAnError_StopsStartingNewParts()
{
    int started = 0;

    await ParallelPartUploader.RunAsync(16, 2, async (i, ct) =>
    {
        Interlocked.Increment(ref started);
        await Task.Delay(10, ct);
        return i == 1 ? new PartResult(i + 1, null, "rejected") : new PartResult(i + 1, "etag", null);
    }, CancellationToken.None);

    Assert.True(started < 16, $"{started} of 16 parts started after an error");
}

[Fact]
public async Task RunAsync_SurfacesTheRealFault_NotACancellation()
{
    // AttemptRunner decides retryability from this exception, so it must be the real one.
    await Assert.ThrowsAsync<HttpRequestException>(() => ParallelPartUploader.RunAsync(8, 4,
        async (i, ct) =>
        {
            if (i == 0)
            {
                await Task.Delay(10, ct);
                throw new HttpRequestException("the real fault");
            }

            await Task.Delay(5000, ct); // cancelled by the first fault
            return new PartResult(i + 1, "etag", null);
        },
        CancellationToken.None));
}

[Fact]
public async Task RunAsync_WhenAnErrorResultRacesALaterException_ReportsTheErrorResult()
{
    // The causal failure is part 0's HTTP rejection. Part 3 dies while draining afterwards; if
    // thrown faults and error results are tracked separately, part 3's exception wins and the user
    // is told the wrong thing. Lowest index wins, so the rejection is reported.
    // An ASYNCHRONOUS latch, not a Barrier. Barrier.SignalAndWait blocks, and WhenAll is still
    // lazily enumerating when part 0 takes the first semaphore slot — so part 0 would block before
    // parts 1-3 were ever created, deadlocking the test rather than racing it.
    int arrived = 0;
    TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    PartResult[] results = await ParallelPartUploader.RunAsync(8, 4, async (i, ct) =>
    {
        if (Interlocked.Increment(ref arrived) == 4)
        {
            allStarted.SetResult();
        }

        await allStarted.Task; // all four initial workers are now in flight

        if (i == 0)
        {
            return new PartResult(1, null, "rejected (HTTP 403)");
        }

        if (i == 3)
        {
            await Task.Delay(20, CancellationToken.None); // deliberately ignores the linked token
            throw new HttpRequestException("collateral damage");
        }

        await Task.Delay(1000, ct);
        return new PartResult(i + 1, "etag", null);
    }, CancellationToken.None);

    Assert.Equal("rejected (HTTP 403)", results[0].Error);
}

[Fact]
public async Task RunAsync_RecordsAnUnrelatedCancellation_RatherThanSwallowingIt()
{
    // Two earlier filters got this wrong in opposite directions, so it gets its own test. Part 3
    // rejects and cancels the linked token; part 0 independently times out with an OCE carrying
    // CancellationToken.None. That is a REAL fault, not a consequence of our cancellation, and
    // AttemptRunner.cs:200 treats a None-token OCE as a fault too — so it must be recorded, and
    // lowest-index-wins means it is what surfaces.
    int arrived = 0;
    TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    await Assert.ThrowsAsync<TaskCanceledException>(() => ParallelPartUploader.RunAsync(8, 4, async (i, ct) =>
    {
        if (Interlocked.Increment(ref arrived) == 4)
        {
            allStarted.SetResult();
        }

        await allStarted.Task;

        if (i == 3)
        {
            return new PartResult(4, null, "rejected"); // cancels the linked token
        }

        if (i == 0)
        {
            // WAIT for the cancellation to actually land before throwing. Without this the test
            // races: part 0 can resume the instant SetResult fires, throw while linked is still
            // uncancelled, and the OLD `when (linked.IsCancellationRequested)` filter would see
            // false, record the exception, and PASS — a test that green-lights the bug it exists
            // to catch. RunContinuationsAsynchronously prevents inline continuations; it does not
            // order these two.
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => cancelled.TrySetResult()))
            {
                // BOUNDED. An unbounded await here turns a regression into a HANG: if error-result
                // cancellation ever stops working, part 0 never completes and Task.WhenAll waits
                // forever. With a timeout the same regression is an ordinary failing test.
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            // An unrelated cancellation: NOT our linked token, so it is a real fault.
            throw new TaskCanceledException("independent timeout", null, CancellationToken.None);
        }

        await Task.Delay(1000, ct);
        return new PartResult(i + 1, "etag", null);
    }, CancellationToken.None));
}

[Fact]
public async Task RunAsync_WhenTheCallerCancels_AndNoPartFailed_Throws()
{
    using CancellationTokenSource cts = new();
    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallelPartUploader.RunAsync(4, 2,
        (i, ct) => Task.FromResult(new PartResult(i + 1, "etag", null)), cts.Token));
}
```

- [x] **Step 2: Run to verify it fails.**

- [x] **Step 3: Implement**

```csharp
public static class ParallelPartUploader
{
    public static async Task<PartResult[]> RunAsync(
        int partCount,
        int degreeOfParallelism,
        Func<int, CancellationToken, Task<PartResult>> uploadPart,
        CancellationToken cancellationToken)
    {
        PartResult[] results = new PartResult[partCount];

        if (degreeOfParallelism <= 1)
        {
            // A real sequential loop, short-circuiting on the first error result exactly as every
            // hoster does today. Any deviation here changes untouched hosters.
            for (int i = 0; i < partCount; i++)
            {
                results[i] = await uploadPart(i, cancellationToken).ConfigureAwait(false);
                if (results[i].Error is not null)
                {
                    break;
                }
            }

            return results;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim gate = new(degreeOfParallelism, degreeOfParallelism);

        // ONE failure record covering both shapes. Tracking thrown faults separately from error
        // RESULTS let a part that died while draining mask the HTTP 403 that actually caused the
        // failure — and AttemptRunner decides retryability from whatever it is handed.
        (int Index, ExceptionDispatchInfo? Fault)? primary = null;
        Lock failureSync = new();

        void RecordFailure(int index, ExceptionDispatchInfo? fault)
        {
            lock (failureSync)
            {
                // Lowest part index wins, deterministically. "First to take the lock" is
                // scheduler-dependent, so two runs of the same failure could report different
                // causes — an explicit rule makes the reported error reproducible.
                if (primary is null || index < primary.Value.Index)
                {
                    primary = (index, fault);
                }
            }

            try
            {
                linked.Cancel();
            }
            catch (Exception)
            {
                // A throwing cancellation callback must never replace the recorded failure.
            }
        }

        async Task RunPartAsync(int index)
        {
            try
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // the run is already doomed; this part never started, and never took a slot
            }

            try
            {
                results[index] = await uploadPart(index, linked.Token).ConfigureAwait(false);

                // An error RESULT dooms the attempt just as a thrown fault does — nothing is
                // finalised without every part — so stop spending the user's bandwidth.
                if (results[index].Error is not null)
                {
                    RecordFailure(index, fault: null);
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == linked.Token)
            {
                // Swallow ONLY a cancellation that our own linked token caused. Two earlier
                // filters were both wrong:
                //
                //   `linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested`
                //     let a CALLER-induced cancellation fall through and be recorded as a part
                //     failure, so a low-index cancelled sibling displaced the genuine error.
                //
                //   `linked.IsCancellationRequested` alone swallowed UNRELATED cancellations: if
                //     part 3 rejects and cancels `linked` while part 0 independently times out
                //     with an OCE carrying CancellationToken.None, part 0's real fault vanished.
                //     AttemptRunner.cs:200 explicitly treats a None-token OCE as a fault, so
                //     swallowing it here also contradicts the retry layer.
                //
                // Matching on the exception's own token distinguishes "we cancelled this" from
                // "this timed out on its own". Pure caller cancellation is handled by the final
                // ThrowIfCancellationRequested.
            }
            catch (Exception ex)
            {
                RecordFailure(index, ExceptionDispatchInfo.Capture(ex));
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, partCount).Select(RunPartAsync)).ConfigureAwait(false);

        // A recorded failure always beats the caller's cancellation: if a part genuinely failed,
        // that is the cause worth reporting, and checking cancellation first would hide it behind
        // an OperationCanceledException.
        if (primary is { } failure)
        {
            failure.Fault?.Throw();          // a thrown fault propagates with its original stack
            return results;                  // an error RESULT travels back in the array
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }
}

/// <summary>One part's outcome: its 1-based number plus either an ETag or an error. Hosts that do
/// not use ETags (Hostize) leave <see cref="ETag"/> null on success.</summary>
public readonly record struct PartResult(int PartNumber, string? ETag, string? Error);
```

- [x] **Step 4: Run to verify it passes.**
- [x] **Step 5: Commit** — `feat(upload): a bounded-concurrency runner with a lowest-index primary failure`

---

### Task 4: The capability, the setting, and its UI

**Files:**
- Modify: `src/CSUploader.Core/Upload/Pipeline/IFileHosterPipeline.cs`
- Modify: `src/CSUploader.Core/Upload/Settings.cs` (backing fields at `:62`), `SettingKey.cs`
- Modify: `src/CSUploader.Core/ViewModels/SettingsViewModel.cs:269` (explicit load/apply cases)
- Modify: `src/CSUploader/Views/SettingsView.axaml:447` (explicit controls)
- Modify: `src/CSUploader.Core/Upload/Pipeline/AttemptInputs.cs`, `AttemptContext.cs`, `AttemptRunner.cs`, `PackageFile.BuildAttemptInputs`
- Modify: the five opted-in pipelines
- Test: `tests/Upload/Pipeline/PartParallelismTests.cs`

**Three corrections from r1 (#2, #3).**

*r1 never opted anyone in* — it added the default and wrote a test expecting four hosters to exceed it. The overrides are written here.

*A default interface implementation is not callable as a concrete-class member.* `MaxParallelPartsFor` must be **explicitly implemented** on each opted-in pipeline. `AttemptRunner` then calls it through the interface, which is where the effective degree is resolved.

*There is no `AttemptContext.Settings`* (verified: it ends at `SpeedBudget` and `Cancellation`). `BuildAttemptInputs` has no registry either, so it carries only the user's CEILING; `AttemptRunner`, which holds the selected pipeline, resolves the effective degree onto the context. Also note the real hoster name is **`"Storage.to"`** (`StorageToPipeline.cs:113`), not `"storage.to"`.

- [x] **Step 1: Write the failing tests**

```csharp
[Fact]
public void EveryPipeline_DefaultsToOnePart_UnlessItOptsIn()
{
    string[] optedIn = ["VikingFile", "Hostize", "Storage.to", "UploadNow", "DataNodes"];

    foreach (IFileHosterPipeline pipeline in AllRegisteredPipelines())
    {
        int degree = pipeline.MaxParallelPartsFor(new FileHosterLoginDto());
        if (optedIn.Contains(pipeline.Name, StringComparer.Ordinal))
        {
            Assert.True(degree > 1, $"{pipeline.Name} should have opted in but returned {degree}");
        }
        else
        {
            Assert.Equal(1, degree);
        }
    }
}

[Fact]
public void EffectiveDegree_IsTheLesserOfTheHostersAndTheUsersCeiling()
{
    Assert.Equal(2, PartParallelism.Effective(hosterDeclares: 8, userCeiling: 2));
    Assert.Equal(4, PartParallelism.Effective(hosterDeclares: 4, userCeiling: 8));
    Assert.Equal(1, PartParallelism.Effective(hosterDeclares: 8, userCeiling: 0)); // never below 1
}

[Fact]
public void BuildAttemptInputs_CarriesTheUsersCeiling_NotTheResolvedDegree()
{
    // It has no registry, so it cannot know what the hoster declares. It carries only the ceiling.
    PackageFile file = TestFileOn("VikingFile", userCeiling: 4);
    Assert.Equal(4, file.BuildAttemptInputs(Mock.Of<IAppLogger>()).MaxParallelPartsCeiling);
}

[Fact]
public void AttemptRunner_CombinesTheHostersDeclarationWithTheCeiling()
{
    // The runner has the pipeline, so this is where the two halves meet.
    AttemptContext ctx = BuildContext(hosterDeclares: 8, userCeiling: 4);
    Assert.Equal(4, ctx.MaxParallelParts);
}
```

- [x] **Step 2: Run to verify it fails.**

- [x] **Step 3: Implement**

```csharp
// IFileHosterPipeline.cs
/// <summary>
/// How many of this file's parts may be sent at once. Defaults to 1 — sequential, exactly as every
/// hoster behaved before this existed. Override ONLY where the protocol makes parts genuinely
/// order-independent: presigned per-part URLs, on-demand per-part signing, or an explicit byte
/// offset per chunk (DataNodes' X-Seek-To). Append-only chunk endpoints and GigaFile's
/// cookie-chained chunks must not.
/// </summary>
int MaxParallelPartsFor(FileHosterLoginDto credentials) => 1;
```

Each of the five opted-in pipelines gets an **explicit** member:

```csharp
// VikingFilePipeline / HostizePipeline / StorageToPipeline / UploadNowPipeline
/// <summary>Presigned per-part uploads; measured at 2.57x on eight connections (Task 0).</summary>
public int MaxParallelPartsFor(FileHosterLoginDto credentials) => 8;

// DataNodesPipeline — the host's own uploader sends up to ten at once (see the class remarks).
public int MaxParallelPartsFor(FileHosterLoginDto credentials) => 8;
```

```csharp
// src/CSUploader.Core/Upload/Pipeline/PartParallelism.cs
/// <summary>The user's ceiling caps whatever a hoster declares, and the result is never below 1.</summary>
public static class PartParallelism
{
    public static int Effective(int hosterDeclares, int userCeiling)
        => Math.Max(1, Math.Min(hosterDeclares, userCeiling));
}
```

```csharp
// Settings.cs — beside the existing backing fields at :62
private int? maxParallelPartsPerFile;

public static int DefaultMaxParallelPartsPerFile { get; } = 4;

/// <summary>
/// Ceiling on concurrent parts within ONE file, capping whatever a hoster declares. Defaults to 4
/// rather than the measured-best 8 because degree multiplies with MaxConcurrentUploadJobs: at 5
/// concurrent files, degree 8 means 40 in-flight part bodies.
/// </summary>
public int MaxParallelPartsPerFile
{
    get => maxParallelPartsPerFile ?? DefaultMaxParallelPartsPerFile;
    set => maxParallelPartsPerFile = value;
}
```

**Where the degree is resolved.** `BuildAttemptInputs(IAppLogger logger)` has no access to the
pipeline registry — its only parameter is the logger, and the registry belongs to `UploadScheduler`
and `AttemptRunner`. So the two halves are resolved in different places:

- `AttemptInputs` gains `public int MaxParallelPartsCeiling { get; init; } = 1;`, set by
  `BuildAttemptInputs` from settings alone:
  `MaxParallelPartsCeiling = Package.Options.Settings?.MaxParallelPartsPerFile ?? AppSettings.DefaultMaxParallelPartsPerFile`.
- `AttemptRunner` already holds the selected pipeline (`AttemptRunner.cs:89-100`), so it combines
  the two and puts the answer on the context:
  `MaxParallelParts = PartParallelism.Effective(pipeline.MaxParallelPartsFor(inputs.Credentials), inputs.MaxParallelPartsCeiling)`.
- `AttemptContext` gains `public int MaxParallelParts { get; init; } = 1;` — the resolved number,
  which is all a pipeline needs. **Defaulted, not `required`**: many tests construct an
  `AttemptContext` directly (e.g. `VikingFilePipelineTests.cs:239`), and a required member would
  break every one of them for no benefit. The default is 1, which is also the safe value.
  `AttemptInputs.MaxParallelPartsCeiling` is defaulted the same way, for the same reason —
  `AttemptRunnerIntegrationTests.cs:35` builds inputs directly.

Settings persistence and UI follow the existing patterns exactly: a new `SettingKey`, a new explicit
case in `SettingsViewModel.cs:269`'s load/apply switch, and a `NumericUpDown` beside its neighbours
in `SettingsView.axaml:447` with a new localized label in **all six** `.resx` files.

- [x] **Step 4: Run to verify it passes, plus both full suites.**
- [x] **Step 5: Commit** — `feat(upload): hosters declare part parallelism, capped by a user setting`

---

### Task 5: VikingFile — the reference conversion

**Files:**
- Modify: `src/CSUploader.Core/Upload/Pipeline/Hosters/VikingFilePipeline.cs:246-285`, and its test seam at `:56`/`:64`
- Modify: `src/CSUploader.Core/Lib/Net/Http/HttpHandler.cs` — `PutChunkAsync`
- Test: `tests/Upload/Pipeline/Hosters/VikingFileParallelPartsTests.cs`

**The test seam must be rebuilt first (r1's #11).** `_putPartOverride` is `Func<string, int, HttpResponseSnapshot>` — private, constructor-injected, and **synchronous**. r1's tests assigned a non-existent `PutPartOverride` property with an async lambda; they cannot compile, and a synchronous seam cannot exercise async overlap, cancellation, offsets or slice contents. Replace it with:

```csharp
/// <summary>
/// Test seam for one part PUT. Carries the BODY and a progress reporter as well as the addressing,
/// because the assertions that matter are about content and interleaving: r2's seam took only
/// (url, partNumber, offset, length), so "each part reads its own range" could only check the
/// offsets the pipeline *passed*, never the bytes it actually read.
/// </summary>
internal delegate Task<HttpResponseSnapshot> PutPartHandler(
    string url,
    int partNumber,
    long fileOffset,
    long length,
    Stream body,
    Action<long> reportProgress,
    CancellationToken cancellationToken);
```

Two consequences for the existing fixtures:

- `VikingFilePipelineTests.cs:95` captures a plain `List<T>` and asserts invocation order. That
  becomes racy the moment degree exceeds 1 — lock the captures or assert order-independently.
- `VikingFilePipelineTests.cs:239-244` uses a path that does not exist on disk, which was harmless
  while the seam bypassed all file access. Task 5 opens a `FileSliceReader` unconditionally, so
  these fixtures must be given **real, small, patterned files** — which is also what lets
  `EachPart_ReadsItsOwnBytes` verify content instead of arguments.

**`PutChunkAsync` changes.** Add `Action<long>? reportPartProgress = null` **after** the existing
`HttpMethod? method` parameter (`HttpHandler.cs:817`), or positional callers such as
`DropMeFilesPipeline.cs:500` break. The argument is *cumulative bytes sent within this part*. Keep
passing the real `basePosition` — it is recorded in the HTTP transaction log (`:829`), and r1's
`basePosition: 0` would have logged every part as chunk zero.

```csharp
// inside PutChunkAsync, in the ProgressStreamContent callback
if (reportPartProgress is null)
{
    UploadProgress?.Invoke(this, new OperationProgressEventArgs(
        totalFileSize, basePosition + bytesInThisChunk, dateTimeStarted));
}
else
{
    // The aggregator owns publication for a parallel upload: it sums across parts and raises the
    // event itself, outside its lock. Raising it here as well would publish a per-part figure
    // interleaved with the file-wide ones.
    reportPartProgress(bytesInThisChunk);
}
```

- [x] **Step 1: Write the failing tests**

**Fixture sizes.** These use a **small patterned file** (4 x 4 KiB = 16 KiB) with `init.PartSize`
stubbed to 4096, not a 400 MB file. The old fixtures pointed at a path that does not exist on disk,
which was harmless only while the seam bypassed all file access — Task 5 opens a `FileSliceReader`
unconditionally, so the file must be real. `Pattern(i)` is `(byte)(i % 251)`.

```csharp
// Every lambda takes all SEVEN PutPartHandler parameters:
//   (url, partNumber, fileOffset, length, body, reportProgress, ct)

[Fact]
public async Task Parts_AreSentConcurrently_AndCompleteWithETagsInOrder()
{
    int peak = 0;
    int running = 0;
    Lock sync = new();

    VikingFilePipeline pipeline = PipelineWithSeam(async (url, partNumber, fileOffset, length, body, report, ct) =>
    {
        lock (sync) { peak = Math.Max(peak, ++running); }
        await Task.Delay(20, ct);
        lock (sync) { running--; }
        return Snapshot(200, etag: $"etag-{partNumber}");
    });

    UploadOutcome outcome = await RunUploadAsync(pipeline, PatternedFile(16 * 1024), partSize: 4096, degree: 4);

    Assert.True(peak > 1, "parts were still sent one at a time");
    Assert.Equal(["etag-1", "etag-2", "etag-3", "etag-4"], CapturedCompleteEtags(outcome));
}

[Fact]
public async Task EachPart_ReadsItsOwnBytes()
{
    // The shared-FileStream hazard, asserted on CONTENT. Recording the offset the pipeline passed
    // proves only that the pipeline did its arithmetic; draining `body` proves the stream actually
    // delivers that region — which is what a shared, position-advancing FileStream would get wrong.
    ConcurrentDictionary<int, byte[]> bodies = new();

    // Explicit release gates, not Task.Delay: delays do not guarantee continuation ORDER, and the
    // whole point is that the parts consume their streams in reverse. Part N waits for part N+1.
    TaskCompletionSource[] released = [.. Enumerable.Range(0, 5)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    released[4].SetResult(); // part 4 goes first

    VikingFilePipeline pipeline = PipelineWithSeam(async (url, partNumber, fileOffset, length, body, report, ct) =>
    {
        // WaitAsync(ct) and a finally, both deliberately. If DrainAsync throws, the runner cancels
        // its linked token — but a bare `await released[n].Task` ignores that token, so every other
        // worker would wait forever and Task.WhenAll would hang instead of surfacing the fault.
        await released[partNumber].Task.WaitAsync(ct);
        try
        {
            bodies[partNumber] = await DrainAsync(body);
            return Snapshot(200, etag: $"etag-{partNumber}");
        }
        finally
        {
            released[partNumber - 1].TrySetResult(); // let the part below me read, pass or fail
        }
    });

    await RunUploadAsync(pipeline, PatternedFile(16 * 1024), partSize: 4096, degree: 4);

    for (int part = 1; part <= 4; part++)
    {
        Assert.Equal(Expected(from: (part - 1) * 4096, count: 4096), bodies[part]);
    }
}

[Fact]
public async Task ProgressOnlyEverIncreases()
{
    // Drives the seam's reportProgress hook directly, so the aggregator is genuinely exercised.
    List<long> published = [];
    Lock sync = new();

    VikingFilePipeline pipeline = PipelineWithSeam(async (url, partNumber, fileOffset, length, body, report, ct) =>
    {
        await Task.Delay((5 - partNumber) * 10, ct);
        for (long sent = 1024; sent <= length; sent += 1024)
        {
            report(sent);
        }

        return Snapshot(200, etag: $"etag-{partNumber}");
    });

    await RunUploadAsync(pipeline, PatternedFile(16 * 1024), partSize: 4096, degree: 4,
        onProgress: total => { lock (sync) { published.Add(total); } });

    Assert.NotEmpty(published);
    Assert.Equal(published.OrderBy(x => x), published);
    Assert.Equal(16 * 1024, published[^1]);
}

[Fact]
public async Task AtDegreeOne_BehavesExactlyAsBefore_StoppingOnTheFirstRejectedPart()
{
    List<int> attempted = [];

    VikingFilePipeline pipeline = PipelineWithSeam((url, partNumber, fileOffset, length, body, report, ct) =>
    {
        attempted.Add(partNumber);
        return Task.FromResult(partNumber == 2 ? Snapshot(403) : Snapshot(200, etag: "e"));
    });

    await RunUploadAsync(pipeline, PatternedFile(16 * 1024), partSize: 4096, degree: 1);

    Assert.Equal([1, 2], attempted);
}
```

- [x] **Step 2: Run to verify it fails.**

- [x] **Step 3: Convert the loop**

Replace the shared `await using FileStream? fs` and the sequential `for` with one `FileSliceReader`
held for the whole transfer plus the runner:

```csharp
using FileSliceReader source = new(ctx.FilePath);
PartProgressAggregator progress = new(
    init.PartUrls.Count,
    total => ctx.Handler.RaiseUploadProgress(new OperationProgressEventArgs(ctx.FileSize, total, started)));

PartResult[] results = await ParallelPartUploader.RunAsync(
    init.PartUrls.Count,
    ctx.MaxParallelParts,
    async (i, ct) =>
    {
        int partNumber = i + 1;
        long basePos = (long)i * init.PartSize;
        long len = Math.Min(init.PartSize, total - basePos);
        Stream body = source.OpenSlice(basePos, len);

        HttpResponseSnapshot resp = _putPart is not null
            ? await _putPart(init.PartUrls[i], partNumber, basePos, len, body, bytes => progress.Report(i, bytes), ct)
            : await ctx.Handler.PutChunkAsync(
                init.PartUrls[i], body, len, basePos, total, started,
                headers: null, ctx.SpeedBudget, ct, method: null,
                reportPartProgress: bytes => progress.Report(i, bytes));

        if (resp.StatusCode is < 200 or >= 300)
        {
            return new PartResult(partNumber, null, $"VikingFile R2 part {partNumber} rejected (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
        }

        return string.IsNullOrEmpty(resp.ETag)
            ? new PartResult(partNumber, null, $"VikingFile R2 part {partNumber} returned no ETag")
            : new PartResult(partNumber, resp.ETag, null);
    },
    ctx.Cancellation);

if (Array.Find(results, r => r.Error is not null) is { Error: not null } failed)
{
    return (null, failed.Error);
}

return await CompleteUploadAsync(ctx, init, [.. results.Select(r => (r.PartNumber, r.ETag!))]);
```

**`RaiseUploadProgress`.** `UploadProgress` is an event on `HttpHandler`, so the aggregator cannot
raise it from outside the class. Add an internal `RaiseUploadProgress(OperationProgressEventArgs)`
method to `HttpHandler` for this purpose. Verify the event's declaration before implementing — if it
is already invoked from a helper, reuse that instead of adding a second path.

- [x] **Step 4: Run both suites.**
- [x] **Step 5: Commit** — `feat(vikingfile): a file's parts upload in parallel`

---

### Task 6: The remaining four hosters

Each repeats Task 5's shape, with these **per-host differences that are not optional**:

**Hostize** (`HostizePipeline.cs:272`)
- **No ETags at all.** `complete` takes only `shareId` and the server finalises (`:33`). Its `PartResult`s carry `ETag = null` on success, and Task 5's ETag assertions do not transfer — assert part *count* and success status instead.
- The parser currently discards the ticket's explicit `partNumber` and assumes array order (`:243`). Preserve `(partNumber, url)` pairs and derive each offset from the number.
- Send `"concurrency"` in the ticket request (`RequestAsync`, `:194`) with the effective degree. The class remarks note the site's own uploader sends `"concurrency":4` and that omitting it was correct only "because these go up one at a time" — once they don't, send the real number.

**UploadNow** (`UploadNowPipeline.cs:287-368`)
- **Part count up front must be ceiling division:** `(FileSize + PartSizeBytes - 1) / PartSizeBytes` with `PartSizeBytes = 64 MiB` (`:75`). Floor division drops the final partial part. Decide and document the zero-byte case; today the loop uploads no parts and completes an empty multipart.
- **The MD5 pre-pass shares the upload stream** (`:285`) and `ComputeMd5Async` moves its position (`:659`). Both passes need independent readers.
- **`WithStorageRetryAsync` re-invokes the part delegate** (`:204`), so each invocation must call `OpenSlice` afresh — a consumed slice sends EOF on retry. The aggregator needs no special handling: its high-water mark makes the replayed bytes a no-op and the total simply plateaus.
- **Thread the runner's linked token** through MD5, the signer request, and retry delays; they currently use `ctx.Cancellation`, so siblings will not stop. `AuthorizeAsync` catches all exceptions including cancellation and converts them to an error string (`:519`) — exclude `OperationCanceledException`.

**DataNodes** (`DataNodesPipeline.cs:283`)
- Chunks are self-locating via `X-Seek-To`, so the conversion is the simplest of the five: no ETags, no completion ordering. Keep the existing 8 MiB chunk size.
- **`X-Upload-SID` only groups chunks** belonging to one upload — every worker reuses the same immutable SID. It imposes no ordering.
- **`import_file` is a barrier, not an ordering constraint**: it must run after every chunk succeeds (`:326-357`), which the runner already guarantees.
- **Preserve the zero-byte behaviour.** The current loop sends one zero-length chunk because `totalChunks` is at least 1. A generic ceiling division yields 0 chunks for an empty file and would change behaviour — special-case it.
- **Check the response body, not just the status.** The documented success is `{"status":"OK"}`, but the code today accepts any HTTP 2xx (`:313-318`). Under parallelism a silently-rejected chunk becomes a truncated file, so validate the envelope while converting.
- Note the class's own warning that this host "can fail the finalise spuriously, after every byte is already up" (`:45`) — do not let parallelism be blamed for that pre-existing flakiness.

**storage.to** (`StorageToPipeline.cs:276`)
- Same shape as VikingFile. **Disabled** (`ServiceRegistration.cs:350`), so it cannot be verified live; convert for consistency and say so in the commit message. Its local `Func<long?>? getBytesPerSecond` parameter at `:614` also needs the budget migration.

- [x] Steps 1-5 per hoster, in the order Hostize → DataNodes → UploadNow → storage.to (simplest first, UploadNow last as the riskiest).

---

### Task 7: Orphan cleanup

**Files:** the four multipart pipelines.

r1 claimed "nothing is committed until complete-multipart". That is too strong: successfully uploaded S3/R2 parts persist under an incomplete multipart upload, and **none of these pipelines has an abort path**. Every outer retry starts a fresh multipart and leaves another orphan. UploadNow additionally creates folder and file metadata *before* transferring bytes (`:235`).

**Only UploadNow qualifies.** "Where the protocol offers one" turned out to mean one host of the five. UploadNow signs its own storage requests through the host's signer service, so S3's `AbortMultipartUpload` (`DELETE {object}?uploadId=`) is available to us. VikingFile, Hostize and storage.to are handed presigned **part** URLs plus a complete endpoint and nothing else — there is no abort URL and no credential to mint one, so their orphans are the host's to collect. DataNodes is not multipart at all. Recorded here rather than left implied, so nobody later reads four unchanged pipelines as an oversight.

- [x] **Step 1:** Best-effort abort in a `finally` around the transfer, so it covers all four ways out that leave the multipart open: a refused part, a refused assembly, a raw transport fault, and cancellation. Skipped once the assembly is confirmed — the upload id no longer exists then.
- [x] **Step 2:** Its own `CancellationTokenSource(5s)`. The usual reason to be here is that `ctx.Cancellation` is already cancelled; signing or sending on that token would cancel the cleanup before it left the machine. Mutation-verified: swapping in `ctx.Cancellation` fails `ACancelledUpload_StillSendsTheAbort`.
- [x] **Step 3:** Catch-all around the whole cleanup, cancellation included, because it runs in a `finally` and an escape would replace the error the user needs. 404 is treated as success (already gone). Mutation-verified by `AnAbortThatFails_LeavesTheRealErrorIntact`.
- [x] **Step 4:** Committed.

**Not addressed:** UploadNow creates folder and file metadata *before* transferring bytes, so a failed upload also leaves a phantom file record. No delete endpoint for it appears in the 2026-08-08 capture, and guessing one is how a cleanup path ends up firing DELETEs at the wrong resource.

---

### Task 8: Verify against the real hosts

> **Not started — needs an explicit go-ahead.** Every step below uploads real files to a live
> third-party service under someone's account or anonymous quota. That is not something to do on my
> own initiative, so it is the one task left open.

- [ ] **Step 1:** VikingFile — upload a large file, download it, compare hashes.
- [ ] **Step 2:** Hostize — same.
- [ ] **Step 3:** **UploadNow — same.** It is the least similar conversion (on-demand signing, MD5 pre-pass, internal retry) and must not ship on unit tests alone.
- [ ] **Step 4:** **DataNodes — same.** Different mechanism entirely (`X-Seek-To`, not presigned URLs).
- [ ] **Step 5:** With a 1 MB/s limit set, upload at degree 8 and confirm the observed rate is ~1 MB/s and not ~8. This is the interaction with the prerequisite fix and the one that would embarrass us if wrong.
- [ ] **Step 6:** Re-run `scripts/parallel-part-probe.cs` and record the end-to-end speed-up against Task 0's baseline, then delete the probe script.

---

## What changed in r2

| r1 finding | Resolution |
|---|---|
| #1 prerequisite not in the tree | Restated as a hard gate; unchanged. |
| #2 Task 4 opted nobody in; default interface member not callable; wrong host name | Explicit `MaxParallelPartsFor` on all five pipelines; name corrected to `"Storage.to"`. |
| #3 no `AttemptContext.Settings`; settings UI unwired | Degree resolved in `BuildAttemptInputs`, carried as `int MaxParallelParts`; `SettingsViewModel.cs:269` and `SettingsView.axaml:447` now in scope. |
| #4 error results don't stop siblings; degree 1 ≠ today | Runner short-circuits on `PartResult.Error` in **both** branches; pinned by `AtDegreeOne_StopsAtTheFirstErrorResult`. |
| #5 no deterministic primary fault | `ExceptionDispatchInfo` captures the first non-cancellation fault; cancel-callback failures cannot replace it. |
| #6 aggregator not monotonic; no retry semantics | Totals computed under one lock. (r2 added a `ResetPart` for UploadNow's retry; r3 removed it — see below.) |
| #7 UploadNow part count | Ceiling division, with the zero-byte case called out. |
| #8 UploadNow's MD5 stream, retry streams, token threading | All four now explicit requirements in Task 6. |
| #9 host list wrong — DataNodes missed | Five hosters; DataNodes added; the blanket XFS claim withdrawn; GigaFile downgraded to "deferred". |
| #10 Hostize specifics | No-ETag completion, `partNumber` preservation, and `"concurrency"` all specified. |
| #11 test seam inadequate | New Task-based `PutPartHandler` carrying part number, offset, length and token; existing order-asserting tests flagged. |
| #12 "nothing committed" too strong | Reworded to "no published completed object"; orphan abort is now Task 7. |
| #13 per-part handles change file lifetime | `FileSliceReader` holds one anchor handle and serves slices via `RandomAccess`. |
| #14 verification too narrow; unsafe revert | Live tests extended to UploadNow and DataNodes; the probe is a committed script, deleted at the end, so no `git checkout --` is needed. |
| #15 `basePosition: 0`; parameter ordering | Real offset preserved for the transaction log; new parameter appended after `HttpMethod? method`. |

## What changed in r3

| r2 finding | Resolution |
|---|---|
| #1 error results not recorded with thrown faults, so a later exception masks the causal rejection | One `RecordFailure` covering both shapes, with **lowest part index wins** as an explicit, reproducible rule. A recorded failure beats caller cancellation. Pinned by `WhenAnErrorResultRacesALaterException_ReportsTheErrorResult`. |
| #2 "deterministic first fault" overstated; semaphore fine | Selection rule made explicit rather than "first to take the lock", which was scheduler-dependent. Codex confirmed no double-release and that `firstFault?.Throw()` was the only propagation path. |
| #3 `ResetPart` violates monotonic progress | **Deleted.** Each part keeps a **high-water mark**, so a retry plateaus the total instead of dropping it. Pinned by `ARetriedPart_PlateausTheTotal_RatherThanDroppingIt`. |
| #4 publishing under the lock; the closure was wrong anyway | Totals are computed and **queued** under the lock; a single drainer publishes outside it. A throwing subscriber is swallowed so a progress failure cannot fail an upload. Hook is now `Action<long> reportPartProgress` carrying cumulative bytes within the part. |
| #5 `FileSliceReader.Read` used the buffer offset as the file offset | **A real bug.** `_fileOffset` captured explicitly, parameter renamed `bufferOffset`, and a mandatory test reads a slice at 4096 into buffer position 20. |
| #6 `offset + length` can overflow the range check | Rewritten as `length > FileLength - fileOffset`. Async handle confirmed fine. |
| #7 DataNodes protocol details missing | Zero-byte one-chunk behaviour preserved; `{"status":"OK"}` body validated rather than bare 2xx; SID and `import_file` roles documented. |
| #8 `BuildAttemptInputs` has no `registry` | Split: `BuildAttemptInputs` carries only the user ceiling; `AttemptRunner` (which holds the pipeline) resolves the effective degree onto the context. |
| #9 seam cannot test its advertised assertions | Seam now carries the **body** and an `Action<long> reportProgress`; fixtures replaced with real patterned files, since Task 5 opens the file unconditionally and `VikingFilePipelineTests.cs:239` used a nonexistent path. |

## What changed in r4

| r3 finding | Resolution |
|---|---|
| #1 caller cancellation recorded as a part failure, letting a cancelled low-index sibling displace the genuine error | The filter is now `when (linked.IsCancellationRequested)` alone. Any cancellation seen through the linked token is a consequence — of a sibling's failure or of the caller — never a cause. Pure caller cancellation is handled by the final `ThrowIfCancellationRequested`. |
| #2 the race test does not race | A `Barrier(4)` holds all four initial workers until they have started, so part 0's synchronous rejection cannot cancel part 3 before it begins. |
| #3 stale `ResetPart` and old signatures left by surgical editing | Every reference purged; the interface block, the rationale and Task 6 now all describe the high-water-mark design. |
| #4 seam tests pass 5-parameter lambdas to a 7-parameter handler; `EachPart_ReadsItsOwnBytes` never reads the body | All lambdas take the full seven parameters. The content test now **drains `body`** and compares bytes, which is what actually catches a wrongly sliced stream; fixtures use a real 16 KiB patterned file with `partSize` 4096 instead of a nonexistent path and a 400 MB size. |
| #5 `required` members break existing fixtures | `AttemptContext.MaxParallelParts` and `AttemptInputs.MaxParallelPartsCeiling` are **defaulted to 1**, not required, so `VikingFilePipelineTests.cs:239` and `AttemptRunnerIntegrationTests.cs:35` keep compiling. 1 is also the safe value. |
| #6 `Slice.Read` swallows invalid arguments | `ValidateBufferArguments` runs before the allowed-slice arithmetic, so `count == -1` throws instead of returning a silent 0. |

## What changed in r5

| r4 finding | Resolution |
|---|---|
| #1 the filter `when (linked.IsCancellationRequested)` swallows an UNRELATED cancellation — part 3 rejecting while part 0 times out with a `None`-token OCE loses part 0's real fault, and contradicts `AttemptRunner.cs:200` | Matches on the exception's own token: `when (ex.CancellationToken == linked.Token)`. That distinguishes "we cancelled this" from "this timed out on its own". Both earlier filters are documented in the code so neither gets reintroduced. |
| #2 `Barrier(4)` **deadlocks** — `Task.WhenAll` is still lazily enumerating when part 0 takes the first semaphore slot and blocks in `SignalAndWait`, so parts 1-3 are never created | Replaced with an asynchronous latch: `Interlocked.Increment` plus a `TaskCompletionSource(RunContinuationsAsynchronously)` that every worker awaits. Nothing blocks a thread, so enumeration completes. |
| #3 `Task.Delay` does not guarantee continuation ORDER, so reverse consumption was not deterministic | Explicit per-part release gates: part N waits for part N+1 to finish reading, so the reverse order is guaranteed rather than hoped for. |
| #4 the aggregator's XML summary still claimed publication happens under the lock | Corrected: update and **enqueue** are under the lock, publication is drained outside it. |
| #5 stale test name | The old `EachPart_ReadsItsOwnByteRange` renamed to `EachPart_ReadsItsOwnBytes`. |
| #6 stale rationale citing "Task 5's unqualified call" | `AttemptRunner` calls it through the interface; each opted-in pipeline still declares the member explicitly. |

## What changed in r6

| r5 finding | Resolution |
|---|---|
| #1 the release gates HANG if a part throws — it never releases its neighbour, and the others await tasks that ignore the linked token, so `Task.WhenAll` never completes | The neighbour is released in a `finally` (pass or fail) **and** every gate wait is `await released[n].Task.WaitAsync(ct)`. Both, as advised: the `finally` handles the throwing part, the token handles a part cancelled before it ever reaches the `finally`. |
| #2 the token-identity filter was sound but untested | New `RunAsync_RecordsAnUnrelatedCancellation_RatherThanSwallowingIt`: part 3 rejects and cancels the linked token while part 0 throws an OCE carrying `CancellationToken.None`. Given two prior regressions in this exact filter, it now has its own deterministic test. |
| #3, #4 two stale strings that my previous replacements failed to match | Corrected — and verified by grep this time rather than assumed. |

## What changed in r7

| r6 finding | Resolution |
|---|---|
| #1 the new cancellation test RACES — part 0 can throw before part 3 cancels `linked`, so the old broken filter would see `false`, record the fault, and pass | Part 0 now registers on `ct` and awaits the cancellation actually landing before throwing. `RunContinuationsAsynchronously` prevents inline continuations; it does not ORDER these two, which is what the race turned on. |
| #2 line 843 still claimed the degree is resolved in `BuildAttemptInputs` | Corrected: `BuildAttemptInputs` carries only the ceiling; `AttemptRunner`, which holds the pipeline, resolves the effective degree. |
| #3 the prerequisite section still said the speed-budget fix was absent | Marked **SATISFIED** — it is implemented and green on `fix/shared-speed-limit-budget`. |
| #4 a global rename had corrupted a changelog row into `X → X` | Left-hand side restored to the former name. |
| #5 "deterministic first fault" survived in the architecture blurb, a task heading and a commit message | All three now say "lowest-index primary failure". The one remaining instance is a changelog row quoting the old phrase, which is correct. |

---

### Task 9 (LAST): close the seam-join gap with a fakeable transport

**Deferred deliberately — do this after Tasks 1-8.** Raised by Codex reviewing the shared-budget
work and carried here because the same shape recurs in every conversion.

**The gap.** Several tests claim to prove a join and actually stop one layer short:

- `MegaWebSocketUploaderThrottleTests` drives `SendChunkThrottledAsync` directly. **Deleting its
  production call in `UploadAsync` leaves every one of them green**, so a future edit could go back
  to a raw `ws.SendAsync` and no test would notice.
- `HttpHandlerSpeedBudgetTests` constructs synthetic budgets rather than ones that came from a
  scope, and `SpeedLimiterScopeTests` stops at `BuildAttemptInputs`. Between them, a regression in
  `AttemptRunner`'s forwarding or in any pipeline's optional-budget argument still passes both
  suites.
- The same will be true of Tasks 5-6: a converted pipeline that stops calling
  `ParallelPartUploader` would keep its unit tests.

**Why it is last.** Closing it properly means introducing a seam for the transport — an interface
over the `ClientWebSocket` for MEGA, and something equivalent for the part PUTs — so a test can
drive `UploadAsync` and a converted pipeline end to end against a fake. That is a refactor of code
the feature is otherwise only lightly touching, and doing it first would mean rewriting the seam
twice.

- [x] **Step 1:** `IMegaSocket` (`ConnectAsync`/`SendAsync`/`ReceiveAsync`) with `MegaClientWebSocket`
  behind it in production and a `FakeMegaSocket` in tests. `UploadAsync` takes an optional
  `socketFactory`, null everywhere but the tests.
- [x] **Step 2:** `MegaUploadTransportJoinTests` drives `UploadAsync` end to end. Mutation-verified:
  replacing the `SendChunkThrottledAsync` call with a raw whole-chunk send fails it while all six
  `MegaWebSocketUploaderThrottleTests` stay green — which is the whole point of the step.
- [x] **Step 3:** `VikingFileParallelPartsTests.ThroughTheRealHandler_*` now watches the real
  transport rather than draining it: peak concurrency, and each request body compared against its
  own region of the file. Mutation-verified twice — pinning the degree to 1 fails the first
  assertion, and slicing every part from offset 0 fails the second. Its fixture was `index % 251`,
  which repeats; it is xorshift32 now, for the reason Codex gave for the contract suite.
- [x] **Step 4:** `speedBudget` is a required parameter on all nine byte-carrying `HttpHandler`
  methods. It sat after `headers`/`extraFields`, which are optional, so making it required meant
  moving it ahead of them; the compiler then found all 78 call sites. Three carried no file bytes
  (UploadNow's CreateMultipartUpload, its completion envelope, and its JSON API) and now say
  `SpeedBudget.Unlimited` out loud. Inside the handler the `speedBudget is not null` branch became
  `!ReferenceEquals(speedBudget, SpeedBudget.Unlimited)`. Not literally the same predicate: before,
  `null` meant a raw stream and the `Unlimited` singleton was wrapped; now the singleton means a raw
  stream and a null would enter `ThrottledStream` and fail there. No production caller is affected:
  every call that carries FILE BYTES builds `new SpeedBudget(...)`, which was wrapped before and is
  wrapped now, and the only callers naming the singleton are the three control requests that carry
  none. The point is one fewer way to be silently unthrottled, not an identity. It also surfaced — and removed — a nullable `SpeedBudget?` on storage.to's private `PutAsync`.
- [x] **Step 5:** Committed.

**What Step 4 would NOT have caught:** MEGA and transfer.it never called `HttpHandler` at all — they
wrote to a raw `ClientWebSocket`. A required parameter cannot catch a path that bypasses the
parameter. Steps 1-2 are what pin that one, and this note is here so the step is not remembered as
broader protection than it is.

---

## What changed in r8

| r7 finding | Resolution |
|---|---|
| #1 `await cancelled.Task` is unbounded, so a regression in error-result cancellation hangs `Task.WhenAll` instead of failing | Bounded with `WaitAsync(TimeSpan.FromSeconds(10))`. The same regression is now an ordinary failing test rather than a stuck CI run. `await using` on the registration was confirmed valid on .NET 10 and is unchanged. |
| #2 the live rationale still said "capture the first non-cancellation fault" | Rewritten to describe the actual policy: record both error results and thrown faults into one slot and select by lowest part index, never by which thread reached the lock first. |

## Remaining known risks

1. ~~Degree 8 across 5 concurrent files is 40 in-flight part bodies. The default ceiling of 4 keeps it to 20; confirm `PutChunkAsync`'s buffer sizes make that acceptable before raising it.~~ **Checked.** Nothing buffers a part: every converted pipeline streams from a `FileSliceReader` slice. The per-request cost is `ProgressStreamContent`'s 80 KiB copy buffer, so 40 in-flight parts is ~3 MB, and UploadNow's MD5 pre-pass adds one more 80 KiB buffer per part while it runs. Memory is not what caps the degree here.
2. The Task 0 probe measured raw PUTs, not the full pipeline. Task 8 Step 6 is what confirms the gain survives hashing, throttling and progress reporting.
3. `OperationProgressEventArgs.cs:132` computes `DateTimeFinish` before the new `TimeRemaining` is assigned — a pre-existing bug Codex spotted, unrelated to this work but visible in the same code path.
