# Shared Speed-Limit Budget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Revision 6.** r5 closed the design questions; its review found one remaining correctness hole — an ABA race that let an uncharged grant bypass the bucket — plus a product claim that was mathematically wrong. Both are fixed here. See "What changed in r6".

**Goal:** Make an upload speed limit mean what it says when more than one upload runs at once.

**Architecture:** A continuous token bucket whose capacity is deliberately small. Tokens accrue at the rate in force and are spent on grant; a caller returns what it did not move. One bucket per limit scope; the owning scope is re-resolved every iteration **and re-confirmed after every grant**. Time comes from an injected `TimeProvider`.

**Tech Stack:** C# / .NET 10, xUnit, `System.TimeProvider`, `System.Threading.Lock`, `ConditionalWeakTable`.

## Background: the verified defect

`AppSettings.MaxConcurrentUploadJobs` (default **5**) lets `UploadScheduler.LaunchUpload` start several files at once, each under `Task.Run` (`UploadScheduler.cs:83`). Each upload body gets its own `new ThrottledStream(rawStream, getBytesPerSecond)` — eight sites: `HttpHandler.cs:478, 618, 721, 837, 979, 1069, 1197, 1307`. `ThrottledStream` keeps a private window counter and the delegate returns a *rate*, not a draw on a shared budget, so N concurrent streams each get the full limit.

Measured: 4 concurrent streams at a configured 100 kB/s moved 800,000 bytes in 2.03 s = **394 kB/s aggregate**.

## PRODUCT DECISIONS (settled — do not re-litigate)

**1. An override may exceed the global limit.** `PackageFile.GetEffectiveSpeedLimitBytesPerSecond` is an *override* cascade and stays one. Global 100 KiB/s + package A at 500 + package B inheriting = up to **600 KiB/s** machine-wide, by design.

**2. A limit permits a burst of at most 100 ms of data.**

This one is new in r5 and it is forced by the shape of the problem. Any bucket accrues while idle, and the global bucket lives as long as `AppSettings` — a DI singleton (`ServiceRegistration.cs:40`). So "the bucket starts empty" is true exactly once; after an idle second a 100 kB/s bucket holds 100 kB, and the next batch moves that instantly. r4 claimed the delivered rate equalled the configured rate "over any interval", and Codex correctly showed that was false.

The honest fix is to bound how much can bank. **Capacity is one tenth of a second at the current rate**, so:

- an idle bucket can release at most 100 ms of data as a burst — 10 kB at a 100 kB/s limit;
- the burst is small enough that no user perceives it, and is pinned by a test rather than left implicit.

**Stated precisely**, because a looser phrasing is wrong: for a constant rate `R` over an interval
`T`, the bytes delivered are bounded by

```text
bytes  ≤  R × T  +  0.1R          (the interval's earnings, plus one bucketful banked beforehand)
```

So the excess is bounded by **100 ms worth of data, once** — not by a percentage. As a percentage
that excess shrinks with the interval: it is +100% at `T` = 100 ms, +10% at `T` = 1 s, +1% at
`T` = 10 s. An earlier draft claimed "within 10% over any interval longer than ~100 ms", which is
simply false at the short end; a 100 ms window can carry twice the configured average.

The alternative — zero capacity, i.e. strict pacing — would mean a read can never be satisfied until its bytes have accrued one at a time, which costs a wakeup per few bytes. 100 ms is the smallest burst that keeps the wakeup rate sane.

## Global Constraints

- Target `net10.0` / `net10.0-windows10.0.17763.0`; nullable enabled; match surrounding style.
- Copyright header on every file; UTF-8 **without** BOM; CRLF.
- XML doc comments explain *why*.
- **Unlimited must be genuinely zero-cost**: no lock, no shared write, no allocation, no CWT lookup.
- Limits are live-adjustable mid-upload (`UploadsViewModel.cs:437`).
- `SpeedLimitKBps` keeps its persisted KiB/s meaning (`* 1024` at `Package.cs:491`, `PackageFile.cs:340`).
- Verified API facts: `AppSettings.SpeedLimit` is `int?` (`Settings.cs:211`); `PackageOptions.Settings` is `AppSettings?` (`PackageOptions.cs:19`); **`Package` is a primary-constructor class** (`Package.cs:22`); `PackageFile.FileInfo` is **private** (`PackageFile.cs:458`); `System.Threading.Lock` is the house idiom (`Package.cs:24`).

### What this design deliberately does NOT promise

- **Per-stream fairness.** The aggregate rate is guaranteed; the split is not. Today's code offers no fairness at all.
- **Prompt wakeup on refund.** Bounded by a 50 ms sleep ceiling instead. Codex accepted this trade: it "can underutilize capacity briefly but cannot exceed the rate."
- **Exact accrual across a rate change.** Rates are *sampled* on each read, not observed as events, so the interval containing a change is accounted at `min(oldRate, newRate)` — deliberately conservative. See Task 1.

---

### Task 1: `SpeedLimiter` — a capacity-bounded token bucket

**Files:**
- Create: `src/CSUploader.Core/Lib/Net/Http/SpeedLimiter.cs`, `SpeedReservation.cs`
- Create: `tests/TestSupport/ManualTimeProvider.cs`
- Test: `tests/Lib/Net/Http/SpeedLimiterTests.cs`

**Interfaces:**

```csharp
internal readonly record struct SpeedReservation(SpeedLimiter? Limiter, int Bytes)
{
    public static SpeedReservation None { get; }
    public void Refund(int unusedBytes);
}

public sealed class SpeedLimiter
{
    public SpeedLimiter(Func<long?> getBytesPerSecond, TimeProvider? time = null);
    public static SpeedLimiter Unlimited { get; }
    public long? CurrentLimitBytesPerSecond { get; }
    internal SpeedReservation TryAcquire(int requestedBytes);
    internal TimeSpan EstimateWait(int requestedBytes);
}
```

**Three corrections from r4, all in `Refill`:**

1. **Accrue at `min(_lastRate, currentRate)`.** r4 accrued the whole elapsed interval at `_lastRate`, which over-grants when the rate is *lowered*: prime at 1,000,000 B/s, drop to 10,000, advance 10 ms, and r4 accrues 10,000 (a full second of the new limit) where 100 bytes were earned. Exact piecewise accounting needs the setting-change *timestamp*, which the settings layer does not publish. Taking the minimum is the conservative sampled answer ACROSS ADJACENT SAMPLES — it under-grants briefly after an increase and never over-grants after a decrease. It cannot see a change that reverts between two reads; that over-accrual is bounded by the documented burst.
2. **A conventional constructor stamps the clock**, so `_started` disappears. r4 started the clock on the first `Refill`, which silently broke every test that advanced the clock before touching the limiter — several expected values became 0, and two `SpeedBudget` tests could hang forever because `Task.Delay` advances real time while `ManualTimeProvider` does not.
3. **`GoUnlimited` is deleted.** It was unreachable — the pre-lock unlimited exit returns before it — and its `_lastRate = 0` contradicted `Refund`'s clamp, so a refund during an unlimited spell was discarded exactly as r4 claimed it was not. With capacity bounded to 100 ms, an unlimited or idle spell can bank only that much, which is the documented burst rather than a defect.

- [ ] **Step 1: Write the manual clock**

```csharp
// tests/TestSupport/ManualTimeProvider.cs
/// <summary>A clock the test drives. The limiter's behaviour is entirely a function of elapsed
/// time, so real-clock tests are slow and flaky; this makes them exact and instant.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => 1_000_000_000; // 1 tick = 1 ns

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
private static (SpeedLimiter Limiter, ManualTimeProvider Clock) Build(Func<long?> rate)
{
    ManualTimeProvider clock = new();
    return (new SpeedLimiter(rate, clock), clock); // ctor stamps t=0
}

/// <summary>Takes everything the bucket can currently give. Terminates because the manual clock
/// does not advance while it runs.</summary>
private static long Drain(SpeedLimiter limiter)
{
    long total = 0;
    while (true)
    {
        SpeedReservation r = limiter.TryAcquire(int.MaxValue);
        if (r.Bytes == 0)
        {
            return total;
        }

        total += r.Bytes;
    }
}

[Fact]
public void TheBucketStartsEmpty()
{
    (SpeedLimiter limiter, _) = Build(() => 100_000);
    Assert.Equal(0, Drain(limiter));
}

[Fact]
public void TokensAccrueAtTheConfiguredRate()
{
    (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
    clock.Advance(TimeSpan.FromMilliseconds(50)); // below capacity, so nothing is clipped

    Assert.Equal(5_000, Drain(limiter));
}

[Fact]
public void AnIdleBucket_BanksAtMostOneTenthOfASecond()
{
    // PRODUCT DECISION 2, pinned. Ten seconds idle must not release ten seconds of data.
    (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
    clock.Advance(TimeSpan.FromSeconds(10));

    Assert.Equal(10_000, Drain(limiter)); // capacity, not 1_000_000
}

[Fact]
public void ConcurrentCallers_ShareOneBucket_RatherThanEachGettingTheLimit()
{
    (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 100_000);
    clock.Advance(TimeSpan.FromMilliseconds(100)); // one capacity's worth

    long granted = 0;
    for (int caller = 0; caller < 4; caller++)
    {
        granted += Drain(limiter);
    }

    Assert.Equal(10_000, granted); // NOT 40_000
}

[Fact]
public void LoweringTheRate_DoesNotRetroactivelyEarnAtTheOldRate()
{
    // Codex's exact counter-example to r4: primed at 1 MB/s, dropped to 10 kB/s, 10 ms elapsed.
    // Correct accrual is 100 bytes; r4 produced a full second of the new limit.
    long? rate = 1_000_000;
    ManualTimeProvider clock = new();
    SpeedLimiter limiter = new(() => rate, clock);

    rate = 10_000;
    clock.Advance(TimeSpan.FromMilliseconds(10));

    Assert.Equal(100, Drain(limiter));
}

[Fact]
public void RaisingTheRate_UnderGrantsForTheSampledInterval_RatherThanOverGranting()
{
    // The deliberate conservative side of sampling: the interval spanning the change is accounted
    // at the LOWER rate. Documented, not accidental.
    long? rate = 10_000;
    ManualTimeProvider clock = new();
    SpeedLimiter limiter = new(() => rate, clock);

    rate = 1_000_000;
    clock.Advance(TimeSpan.FromMilliseconds(10));

    Assert.Equal(100, Drain(limiter)); // 10 ms at the OLD rate, not the new one
}

[Fact]
public void Unlimited_GrantsEverythingAndReservesNothing()
{
    SpeedReservation r = SpeedLimiter.Unlimited.TryAcquire(8192);

    Assert.Equal(8192, r.Bytes);
    Assert.Null(r.Limiter); // nothing to refund to; the zero-cost path
}

[Fact]
public void Refund_ReturnsTokensToTheBucketThatGrantedThem()
{
    (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 10_000);
    clock.Advance(TimeSpan.FromMilliseconds(100)); // capacity = 1_000

    SpeedReservation first = limiter.TryAcquire(1_000);
    Assert.Equal(1_000, first.Bytes);
    Assert.Equal(0, limiter.TryAcquire(1).Bytes); // exhausted

    first.Refund(1_000);

    Assert.Equal(500, limiter.TryAcquire(500).Bytes);
}

[Fact]
public void Refund_CannotInflateTheBucketAboveCapacity()
{
    (SpeedLimiter limiter, ManualTimeProvider clock) = Build(() => 10_000);
    clock.Advance(TimeSpan.FromMilliseconds(100));
    SpeedReservation r = limiter.TryAcquire(1_000);
    clock.Advance(TimeSpan.FromSeconds(5)); // refills to capacity on its own
    r.Refund(1_000);

    Assert.Equal(1_000, Drain(limiter)); // capacity, not 2_000
}

[Fact]
public void EstimateWait_TargetsACapacityFill_NotTheWholeRequest()
{
    // Start-empty would stall the first read for 0.82s at 100 kB/s if we waited for a full 80 kB
    // buffer. Targeting one capacity keeps the stream flowing.
    (SpeedLimiter limiter, _) = Build(() => 100_000);

    TimeSpan wait = limiter.EstimateWait(81_920);

    Assert.InRange(wait.TotalMilliseconds, 50, 150);
}
```

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement**

```csharp
/// <summary>
/// A grant from a <see cref="SpeedLimiter"/>, carrying the limiter that made it so a refund reaches
/// the right bucket even when acquisitions overlap or the governing scope changed in between.
/// <para>
/// ONE-SHOT: refund exactly once. Two refunds of a copied reservation would fill the bucket twice
/// with no elapsed time, and capacity clamping does not prevent it because another stream can spend
/// the tokens in between. <see cref="ThrottledStream"/> is the only caller and refunds once in a
/// <c>finally</c>.
/// </para>
/// <para>
/// <see cref="None"/> means NO GRANT (zero bytes). It is not the unlimited case — unlimited is
/// <c>{ Limiter: null, Bytes: requested }</c>, which grants everything and needs no refund.
/// </para>
/// </summary>
internal readonly record struct SpeedReservation(SpeedLimiter? Limiter, int Bytes)
{
    public static SpeedReservation None => default;

    public void Refund(int unusedBytes)
    {
        if (Limiter is not null && unusedBytes > 0)
        {
            Limiter.Refund(unusedBytes);
        }
    }
}
```

```csharp
/// <summary>
/// A byte budget shared by every stream governed by the same speed limit, as a continuous token
/// bucket whose capacity is deliberately small.
/// <para>
/// A limit must be enforced ACROSS concurrent transfers, not within each one. The scheduler runs up
/// to <c>MaxConcurrentUploadJobs</c> files at once and every request body used to wrap its own
/// throttle with its own private counter, so N uploads each got the full limit.
/// </para>
/// <para>
/// Capacity is <b>one tenth of a second</b> at the current rate. Any bucket banks tokens while
/// idle, and the global one lives as long as <c>AppSettings</c> — so an unbounded capacity would let
/// a paused queue release a full second of data the instant it resumed. A tenth of a second is small
/// enough to be imperceptible and large enough to avoid a wakeup every few bytes.
/// </para>
/// <para>
/// Guarantees the AGGREGATE rate over intervals longer than that burst. Does not promise per-stream
/// fairness, a prompt wakeup on refund, or exact accrual across a rate change; see the plan.
/// </para>
/// </summary>
public sealed class SpeedLimiter
{
    /// <summary>Capacity, and the wait target, are one Nth of a second. See the class remarks.</summary>
    private const int BurstFraction = 10;

    private readonly Func<long?> _getBytesPerSecond;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private double _tokens;
    private long _lastTimestamp;
    private long _lastRate;

    public SpeedLimiter(Func<long?> getBytesPerSecond, TimeProvider? time = null)
    {
        _getBytesPerSecond = getBytesPerSecond;
        _time = time ?? TimeProvider.System;

        // Stamp at construction, not on first use. Starting the clock lazily made every caller that
        // let time pass before its first read see a zero interval — which silently broke the
        // manual-clock tests and would have under-throttled a delayed first read in production.
        _lastTimestamp = _time.GetTimestamp();
        _lastRate = getBytesPerSecond() is > 0 and long rate ? rate : 0;
        _tokens = 0; // starts empty
    }

    /// <summary>A limiter that never throttles, and never touches shared state doing it.</summary>
    public static SpeedLimiter Unlimited { get; } = new(() => null);

    public long? CurrentLimitBytesPerSecond => _getBytesPerSecond();

    /// <summary>
    /// Takes what the bucket can afford right now, up to <paramref name="requestedBytes"/> —
    /// possibly ZERO. Never waits.
    /// </summary>
    internal SpeedReservation TryAcquire(int requestedBytes)
    {
        if (requestedBytes <= 0)
        {
            return SpeedReservation.None;
        }

        // Read once before the lock, only to take the zero-cost unlimited exit. The authoritative
        // read happens INSIDE the lock: a caller that sampled a stale high rate must not refill or
        // grant at it after someone else lowered the limit.
        if (_getBytesPerSecond() is null or <= 0)
        {
            return new SpeedReservation(null, requestedBytes);
        }

        lock (_sync)
        {
            long? bps = _getBytesPerSecond();
            if (bps is null or <= 0)
            {
                return new SpeedReservation(null, requestedBytes);
            }

            Refill(bps.Value);
            int granted = (int)Math.Min(requestedBytes, (long)_tokens);
            if (granted <= 0)
            {
                return SpeedReservation.None;
            }

            _tokens -= granted;
            return new SpeedReservation(this, granted);
        }
    }

    /// <summary>How long until one capacity has accrued — not until the caller's whole request can
    /// be met, which at a low limit would stall the first read for most of a second.</summary>
    internal TimeSpan EstimateWait(int requestedBytes)
    {
        lock (_sync)
        {
            long? bps = _getBytesPerSecond();
            if (bps is null or <= 0)
            {
                return TimeSpan.Zero;
            }

            Refill(bps.Value);
            double target = Math.Min(requestedBytes, CapacityFor(bps.Value));
            double shortfall = Math.Max(0, target - _tokens);
            return TimeSpan.FromSeconds(shortfall / bps.Value);
        }
    }

    internal void Refund(int unusedBytes)
    {
        lock (_sync)
        {
            long? bps = _getBytesPerSecond();

            // Clamp against the current capacity, or the last known one while unlimited — a refund
            // of bytes that were never moved must not be silently discarded just because the limit
            // happens to be off at this instant.
            double capacity = CapacityFor(bps is > 0 ? bps.Value : _lastRate);
            _tokens = Math.Min(capacity, _tokens + unusedBytes);
        }
    }

    private static double CapacityFor(long bytesPerSecond) => Math.Max(1, bytesPerSecond / (double)BurstFraction);

    private void Refill(long bps)
    {
        long now = _time.GetTimestamp();
        double elapsedSeconds = (double)(now - _lastTimestamp) / _time.TimestampFrequency;
        _lastTimestamp = now;

        // Accrue at the LOWER of the rate we last saw and the rate now in force. Rates are sampled
        // per read, not observed as events, so the interval containing a change cannot be split
        // exactly. The minimum is the conservative choice ACROSS ADJACENT SAMPLES: it under-grants
        // briefly after a raise and never over-grants after a drop — where accruing at `_lastRate`
        // alone handed out a full second of a newly-lowered limit after 10 ms.
        //
        // It is not conservative against changes that happen and REVERT between two samples: drop
        // to 1 kB/s for a second and back to 100 kB/s before the next read, and both endpoints read
        // 100 kB/s. Sampling cannot see that, and the over-accrual is bounded by the capacity — the
        // documented 100 ms burst — so it is accepted rather than designed around.
        long accrualRate = _lastRate > 0 ? Math.Min(_lastRate, bps) : bps;
        _tokens += elapsedSeconds * accrualRate;
        _tokens = Math.Min(CapacityFor(bps), _tokens);
        _lastRate = bps;
    }
}
```

- [ ] **Step 5: Run to verify it passes; commit.**

---

### Task 2: `SpeedBudget` — wait, re-resolve, and re-confirm

**Files:**
- Create: `src/CSUploader.Core/Lib/Net/Http/SpeedBudget.cs`
- Test: `tests/Lib/Net/Http/SpeedBudgetTests.cs`

**Interfaces:**

```csharp
public sealed class SpeedBudget(Func<SpeedLimiter> resolveActiveLimiter)
{
    public static SpeedBudget Unlimited { get; }
    internal SpeedLimiter CurrentLimiter { get; }   // for tests and for Task 5's assertion
    internal ValueTask<SpeedReservation> AcquireAsync(int requestedBytes, CancellationToken cancellationToken);
}
```

**The resolve→try→confirm cycle.** A gap remains between resolving a limiter and acquiring from it: if the user clears a file override in that gap, the now-obsolete limiter's provider returns null, so **that dead limiter reports itself unlimited and grants the whole request**. So the owner is re-resolved after the grant and compared by reference; on a mismatch the reservation is refunded and the loop retries. r4 retried immediately, which can spin hot if ownership flaps — r5 yields for `MinWait` first.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Acquire_DiscardsAGrantFromAScopeThatStoppedBeingTheOwner()
{
    ManualTimeProvider clock = new();
    long? overrideLimit = 1_000;
    SpeedLimiter overrideBucket = new(() => overrideLimit, clock);
    SpeedLimiter packageBucket = new(() => 100_000, clock);
    clock.Advance(TimeSpan.FromMilliseconds(100));

    int resolveCount = 0;
    SpeedBudget budget = new(() =>
    {
        // First resolve hands out the override bucket; every later one the package bucket, with
        // the override going unlimited exactly as clearing it would.
        if (resolveCount++ == 0)
        {
            return overrideBucket;
        }

        overrideLimit = null;
        return packageBucket;
    });

    SpeedReservation r = await budget.AcquireAsync(8192, CancellationToken.None);

    Assert.Same(packageBucket, r.Limiter);
    Assert.NotSame(overrideBucket, r.Limiter);
}

[Fact]
public async Task Acquire_RejectsAnUnchargedGrant_FromALimiterThatIsNotTheStaticUnlimited()
{
    // The ABA hole. The SAME limiter is resolved before and after — so reference identity alone
    // passes — but in between its provider read null and handed back the whole request having
    // charged nothing. That grant bypasses the bucket entirely and must be rejected.
    ManualTimeProvider clock = new();
    long? overrideLimit = 10_000;    // A starts LIMITED, so the transient clear is a real change
    SpeedLimiter a = new(() => overrideLimit, clock);
    clock.Advance(TimeSpan.FromMilliseconds(100));

    int calls = 0;
    SpeedBudget budget = new(() =>
    {
        // Restore the override right after the grant, so the confirming resolve returns A again.
        if (++calls == 1)
        {
            overrideLimit = null;
        }
        else
        {
            overrideLimit = 10_000;
        }

        return a;
    });

    SpeedReservation r = await budget.AcquireAsync(81_920, CancellationToken.None);

    Assert.NotNull(r.Limiter);                       // it had to be CHARGED to be returned
    Assert.True(r.Bytes <= 1_000, $"granted {r.Bytes} bytes against a 1,000-byte capacity");
}

[Fact]
public async Task Acquire_AcceptsAnUnchargedGrant_FromTheStaticUnlimited()
{
    // The legitimate uncharged case must still short-circuit: the static instance can never
    // become limited, so there is nothing to bypass.
    SpeedReservation r = await SpeedBudget.Unlimited.AcquireAsync(81_920, CancellationToken.None);

    Assert.Null(r.Limiter);
    Assert.Equal(81_920, r.Bytes);
}

[Fact]
public async Task Acquire_WhenOwnershipFlapsEveryIteration_YieldsRatherThanSpinning()
{
    // r4 refunded and retried with no delay, so an alternating resolver burned a core until
    // cancellation. Bounded CPU is the assertion; cancellation still works either way.
    ManualTimeProvider clock = new();
    SpeedLimiter a = new(() => 1_000_000, clock);
    SpeedLimiter b = new(() => 1_000_000, clock);
    clock.Advance(TimeSpan.FromMilliseconds(100));
    int i = 0;
    SpeedBudget budget = new(() => i++ % 2 == 0 ? a : b);

    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => budget.AcquireAsync(1024, cts.Token).AsTask());

    Assert.True(i < 500, $"resolved {i} times in 200ms — the loop is spinning, not yielding");
}

[Fact]
public async Task Acquire_ReturnsAReservationBoundToTheGrantingLimiter()
{
    ManualTimeProvider clock = new();
    SpeedLimiter bucket = new(() => 10_000, clock);
    clock.Advance(TimeSpan.FromMilliseconds(100));
    SpeedBudget budget = new(() => bucket);

    SpeedReservation r = await budget.AcquireAsync(500, CancellationToken.None);

    Assert.Same(bucket, r.Limiter);
    Assert.Equal(500, r.Bytes);
}

[Fact]
public async Task Unlimited_NeverThrottlesAndReservesNothing()
{
    SpeedReservation r = await SpeedBudget.Unlimited.AcquireAsync(8192, CancellationToken.None);
    Assert.Equal(8192, r.Bytes);
    Assert.Null(r.Limiter);
}

[Fact]
public async Task Acquire_OfZeroBytes_ReturnsImmediately()
{
    // Guards a hang, not a rate: the loop exits on Bytes > 0, which a zero request never satisfies.
    // Stream.ReadAsync(Memory<byte>.Empty) is legal, so this is reachable.
    SpeedBudget budget = new(() => new SpeedLimiter(() => 10_000, new ManualTimeProvider()));

    Task<SpeedReservation> acquire = budget.AcquireAsync(0, CancellationToken.None).AsTask();

    Assert.Same(acquire, await Task.WhenAny(acquire, Task.Delay(1000)));
    Assert.Equal(0, (await acquire).Bytes);
}

[Fact]
public async Task Acquire_HonoursCancellationWhileWaiting()
{
    // The manual clock never advances, so the bucket never fills and the loop only exits on cancel.
    SpeedBudget budget = new(() => new SpeedLimiter(() => 1_000, new ManualTimeProvider()));
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => budget.AcquireAsync(8192, cts.Token).AsTask());
}
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement**

```csharp
/// <summary>
/// What a throttled stream holds: a way to find the budget governing it right now, and the patience
/// to wait for capacity.
/// <para>
/// The owning scope is re-resolved every iteration AND re-confirmed after every grant, because a
/// user can set or clear a file or package limit while that file is uploading. Resolving once made a
/// new override invisible; re-resolving without confirming still let a cleared override's dead
/// bucket report "unlimited" and hand over the whole request.
/// </para>
/// </summary>
public sealed class SpeedBudget(Func<SpeedLimiter> resolveActiveLimiter)
{
    /// <summary>Ceiling on one sleep, so a raised limit is noticed promptly and a sibling's refund
    /// becomes usable soon after, without a waiter queue.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(1);

    public static SpeedBudget Unlimited { get; } = new(() => SpeedLimiter.Unlimited);

    /// <summary>The scope currently in force. Exposed so callers can assert which bucket a budget
    /// resolves to without acquiring from it.</summary>
    internal SpeedLimiter CurrentLimiter => resolveActiveLimiter();

    internal async ValueTask<SpeedReservation> AcquireAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        if (requestedBytes <= 0)
        {
            return SpeedReservation.None;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SpeedLimiter active = resolveActiveLimiter();
            SpeedReservation reservation = active.TryAcquire(requestedBytes);
            TimeSpan wait;

            if (reservation.Bytes > 0)
            {
                // Reference identity alone is NOT enough — it has an ABA hole. If the user clears an
                // override, the file's own limiter momentarily reads null and hands back the whole
                // request UNCHARGED via the unlimited fast path; restore the override before this
                // check and the side table returns that same instance, so identity matches and an
                // 81,920-byte grant sails past a 10 kB capacity. An uncharged grant is therefore
                // only trustworthy from the STATIC unlimited limiter, which can never become
                // limited. A charged grant (non-null Limiter) is fine either way: real tokens were
                // spent on the bucket that made it.
                bool grantIsTrustworthy = reservation.Limiter is not null
                    || ReferenceEquals(active, SpeedLimiter.Unlimited);

                if (grantIsTrustworthy && ReferenceEquals(resolveActiveLimiter(), active))
                {
                    return reservation;
                }

                // Ownership changed between resolve and grant. Give the bytes back to the scope
                // that lent them — nothing was read, so the whole grant is unused — and retry
                // against the scope now in force. Yield first: a resolver that flaps every call
                // would otherwise spin a core until cancellation.
                reservation.Refund(reservation.Bytes);
                wait = MinWait;
            }
            else
            {
                wait = active.EstimateWait(requestedBytes);
                if (wait > MaxWait)
                {
                    wait = MaxWait;
                }
                else if (wait < MinWait)
                {
                    // Floors POSITIVE sub-millisecond waits too. Flooring only `<= 0` let a 0.2 ms
                    // estimate become an effectively zero delay and spin hot.
                    wait = MinWait;
                }
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes; commit.**

---

### Task 3: `ThrottledStream` draws from the budget

**Files:**
- Modify: `src/CSUploader.Core/Lib/Net/Http/ThrottledStream.cs`
- Modify: `tests/Lib/Net/Http/ThrottledStreamConcurrencyTests.cs`

- [ ] **Step 1: Rewire the existing tests**

Hoist **one** `SpeedLimiter` (real `TimeProvider` — these measure wall-clock coupling) and one
`SpeedBudget` over it, shared by all four streams. `SingleStream_RunsAtTheLimit` gets **one** hoisted
limiter too. `ConcurrentStreams_WithNoLimit_AreNotThrottled` uses `SpeedBudget.Unlimited`.

**Reduce `PerStreamBytes` from 200,000 to 50,000.** Four streams sharing 100 kB/s move
`4 × 50,000 = 200,000` bytes in ≈2.0 s; at the old 200,000 each it would be ≈8 s. Codex checked the
arithmetic including the capped 50 ms waits and the EOF probe and computed **97.6–100 kB/s**, safely
inside the existing `<= limit * 1.25` ceiling, with scheduling delay only ever lowering it.

- [ ] **Step 2: Run to verify it fails to compile.**

- [ ] **Step 3: Rewrite the read paths**

Delete `_clock`, `_windowStartMs`, `_bytesReadSinceReset`, `ComputeAllowedBytes`. Everything else on
the class is unchanged.

```csharp
public class ThrottledStream(Stream inner, SpeedBudget budget) : Stream
{
    private readonly Stream _inner = inner;
    private readonly SpeedBudget _budget = budget;

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Validate BEFORE acquiring: a negative count would otherwise become a zero-byte grant and
        // a silent 0-return, where the Stream contract calls for ArgumentOutOfRangeException.
        ValidateBufferArguments(buffer, offset, count);

        SpeedReservation reservation = _budget.AcquireAsync(count, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        int read = 0;
        try
        {
            read = _inner.Read(buffer, offset, reservation.Bytes);
            return read;
        }
        finally
        {
            reservation.Refund(reservation.Bytes - read);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        SpeedReservation reservation = await _budget.AcquireAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
        int read = 0;
        try
        {
            read = await _inner.ReadAsync(buffer[..reservation.Bytes], cancellationToken).ConfigureAwait(false);
            return read;
        }
        finally
        {
            // Covers the short read, the EOF probe AND the cancelled read — all three would
            // otherwise spend budget on bytes that never moved.
            reservation.Refund(reservation.Bytes - read);
        }
    }
}
```

**Sync path:** Codex verified across four reviews that no production caller uses it — all eight sites
are upload bodies read asynchronously by `ProgressStreamContent` (`:33`).

- [ ] **Step 4: Run to verify it passes** — including `ConcurrentStreams_ShareTheLimit_RatherThanEachGettingItInFull`, failing since it was written. **This is the moment the reported bug is fixed.**
- [ ] **Step 5: Commit.**

---

### Task 4: A bucket per scope, without touching any constructor

**Files:**
- Create: `src/CSUploader.Core/Upload/SpeedLimitScopes.cs`
- Modify: `Package.cs`, `PackageFile.cs` (add members only)
- Create: `tests/TestSupport/SpeedLimitTestFactory.cs`
- Test: `tests/Upload/SpeedLimiterScopeTests.cs`

**Scope tests assert IDENTITY, not rate.** `SpeedLimitScopes` builds its limiters without a
`TimeProvider`, so draining them would run on the system clock — tokens accruing between iterations,
assertions overshooting, and at high limits a drain loop that may never terminate. Rate behaviour is
pinned deterministically in Task 1. What Task 4 must prove is *which bucket a scope resolves to*, and
with `ConditionalWeakTable` reference identity is the exact, deterministic expression of that.

- [ ] **Step 1: Write the test factory**

```csharp
// tests/TestSupport/SpeedLimitTestFactory.cs
internal static class SpeedLimitTestFactory
{
    /// <summary>Real files in a fresh temp dir — PackageFile reads FileInfo.Length on construction.
    /// Takes the AppSettings so two packages can share one global scope.</summary>
    internal static Package Package(AppSettings settings, int? packageLimitKBps, int fileCount = 2)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"csu-speed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        List<string> paths = [];
        for (int i = 0; i < fileCount; i++)
        {
            string p = Path.Combine(dir, $"f{i}.bin");
            File.WriteAllBytes(p, new byte[1024]);
            paths.Add(p);
        }

        PackageOptions options = new()
        {
            Title = "speed",
            Logger = Mock.Of<IAppLogger>(),
            Settings = settings,
            SelectedFiles = paths,
            FileHosters = new() { { new FileHosterClient("Catbox", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Catbox" } } },
        };

        Package package = new(options) { SpeedLimitKBps = packageLimitKBps };
        package.AddPackageFiles();
        return package;
    }
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public void TwoFilesInOnePackage_ResolveToOneBucket()
{
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);

    Assert.Same(package.First().SpeedLimiter, package.Skip(1).First().SpeedLimiter);
}

[Fact]
public void AFileWithItsOwnLimit_ResolvesToItsOwnBucket()
{
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
    PackageFile capped = package.First();
    capped.SpeedLimitKBps = 200;

    Assert.NotSame(capped.SpeedLimiter, package.Skip(1).First().SpeedLimiter);
    Assert.Equal(200L * 1024, capped.SpeedLimiter.CurrentLimitBytesPerSecond);
}

[Fact]
public void ClearingAFileOverride_ResolvesBackToThePackagesBucket()
{
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
    PackageFile file = package.First();
    file.SpeedLimitKBps = 200;
    Assert.NotSame(package.SpeedLimiter, file.SpeedLimiter);

    file.SpeedLimitKBps = null;

    Assert.Same(package.SpeedLimiter, file.SpeedLimiter);
}

[Fact]
public void PackagesWithNoOverride_ResolveToTheOneGlobalBucket()
{
    AppSettings settings = new() { SpeedLimit = 100 };
    Package a = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);
    Package b = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

    Assert.Same(a.SpeedLimiter, b.SpeedLimiter);
}

[Fact]
public void AnOverrideMayExceedTheGlobalLimit_AndGetsItsOwnBucketToDoIt()
{
    AppSettings settings = new() { SpeedLimit = 100 };
    Package overriding = SpeedLimitTestFactory.Package(settings, packageLimitKBps: 500);
    Package inheriting = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

    Assert.NotSame(overriding.SpeedLimiter, inheriting.SpeedLimiter);
    Assert.Equal(500L * 1024, overriding.SpeedLimiter.CurrentLimitBytesPerSecond);
    Assert.Equal(100L * 1024, inheriting.SpeedLimiter.CurrentLimitBytesPerSecond);
}

[Fact]
public void AnUnlimitedGlobal_ResolvesToTheSharedUnlimitedInstance()
{
    // The ordinary case must not enter the side table: a CWT lookup locks and allocates on first
    // access, which would make "unlimited costs nothing" false for almost every user.
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: null);

    Assert.Same(SpeedLimiter.Unlimited, package.SpeedLimiter);
}

[Fact]
public async Task ConcurrentFirstAccess_YieldsOneBucket()
{
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100, fileCount: 32);
    using Barrier barrier = new(32);

    SpeedLimiter[] buckets = await Task.WhenAll(package.Select(f => Task.Run(() =>
    {
        barrier.SignalAndWait();
        return f.SpeedLimiter;
    })));

    Assert.Single(buckets.Distinct());
}
```

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement**

```csharp
/// <summary>
/// Holds the one bucket belonging to each limit scope. Side tables rather than fields because
/// <see cref="Package"/> is a primary-constructor class with no body to initialise into, and
/// because <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/> creates at most one value per
/// key even under concurrent first access. Entries die with their scope object.
/// </summary>
public static class SpeedLimitScopes
{
    private static readonly ConditionalWeakTable<AppSettings, SpeedLimiter> Global = new();
    private static readonly ConditionalWeakTable<Package, SpeedLimiter> Packages = new();
    private static readonly ConditionalWeakTable<PackageFile, SpeedLimiter> Files = new();

    public static SpeedLimiter ForGlobal(AppSettings? settings)
        // Short-circuit BEFORE the table: unlimited is the common case and must cost nothing.
        // `settings!` is sound — a positive kbps implies settings was non-null.
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

    /// <summary>Reads the nullable ONCE. `x is > 0 ? (long)x.Value * 1024 : null` reads it twice,
    /// and the UI can null it between the two — throwing from .Value mid-upload.</summary>
    private static long? ToBytesPerSecond(int? kbps)
    {
        int? snapshot = kbps;
        return snapshot is > 0 ? (long)snapshot.Value * 1024 : null;
    }
}
```

```csharp
// Package.cs — added members only, no constructor change
/// <summary>The bucket in force for this package right now: its own when it overrides the global
/// setting, else the global one. A property, because the override can change mid-upload.</summary>
public SpeedLimiter SpeedLimiter
    => SpeedLimitKBps is > 0 ? SpeedLimitScopes.ForPackage(this) : SpeedLimitScopes.ForGlobal(Options.Settings);

public SpeedBudget SpeedBudget => new(() => SpeedLimiter);
```

```csharp
// PackageFile.cs — added members only
/// <summary>The bucket in force for this file: its own override when set, else its package's.
/// Mirrors the resolution order of <see cref="GetEffectiveSpeedLimitBytesPerSecond"/> — only the
/// SCOPE of enforcement is new.</summary>
public SpeedLimiter SpeedLimiter
    => SpeedLimitKBps is > 0 ? SpeedLimitScopes.ForFile(this) : Package.SpeedLimiter;

public SpeedBudget SpeedBudget => new(() => SpeedLimiter);
```

`SpeedBudget` is stateless — it holds only a resolver — so a new one per access is fine. The *bucket*
is what must be shared, and the side tables share it.

- [ ] **Step 5: Run to verify it passes; commit.**

---

### Task 5: Carry the budget through the pipeline plumbing

**Files:**
- Modify: `AttemptInputs.cs:25`, `AttemptContext.cs:41`, `AttemptRunner.cs:112`, `PackageFile.BuildAttemptInputs` (`:473`)
- Modify: `HttpHandler.cs` — 8 declarations (444, 574, 594, 702, 815, 953, 1049, 1170) and 8 `new ThrottledStream(...)` sites
- Modify (mechanical): **84 usages / 49 source files**, **97 `Func<long?>` declarations / 65 hoster files**, **80 `SpeedLimitProvider = () => null` initializers / 79 test files** (counts verified by Codex)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void BuildAttemptInputs_CarriesABudgetResolvingToThatFilesBucket()
{
    // Asserts what the budget RESOLVES TO, not merely that it is non-null — r4's version would
    // have passed with SpeedBudget.Unlimited or any unrelated budget carried instead.
    Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
    PackageFile a = package.First();
    PackageFile b = package.Skip(1).First();

    SpeedBudget budgetA = a.BuildAttemptInputs(Mock.Of<IAppLogger>()).SpeedBudget;
    SpeedBudget budgetB = b.BuildAttemptInputs(Mock.Of<IAppLogger>()).SpeedBudget;

    Assert.Same(package.SpeedLimiter, budgetA.CurrentLimiter);
    Assert.Same(budgetA.CurrentLimiter, budgetB.CurrentLimiter);
}
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Change the plumbing types**

```csharp
// AttemptInputs.cs:25 and AttemptContext.cs:41 — replaces `Func<long?> SpeedLimitProvider`
public required SpeedBudget SpeedBudget { get; init; }

// AttemptRunner.cs:112
SpeedBudget = inputs.SpeedBudget,

// PackageFile.cs:473, inside BuildAttemptInputs
SpeedBudget = SpeedBudget,
```

In `HttpHandler.cs`, change each of the 8 declarations from `Func<long?>? getBytesPerSecond = null,`
to `SpeedBudget? speedBudget = null,` and each construction to `new ThrottledStream(x, speedBudget)`,
keeping the `is not null` guards.

`SpeedReservation` and `SpeedBudget.AcquireAsync` are `internal`; `ThrottledStream` is in the same
assembly and `InternalsVisibleTo` already covers the test assembly, so no visibility change is needed.

- [ ] **Step 4: Run the mechanical pass**

```python
# scripts/migrate-speed-budget.py — run once from the repo root
import pathlib, re

# MUST exclude the files created by Tasks 1-4: an earlier revision's script rewrote SpeedLimiter's
# own `Func<long?> getBytesPerSecond` into `SpeedBudget speedBudget` and its calls into
# `speedBudget()`, so the tree did not compile.
EXCLUDE = {'SpeedLimiter.cs', 'SpeedBudget.cs', 'SpeedReservation.cs', 'SpeedLimitScopes.cs',
           'SpeedLimiterTests.cs', 'SpeedBudgetTests.cs', 'SpeedLimiterScopeTests.cs',
           'SpeedLimitTestFactory.cs', 'ManualTimeProvider.cs', 'ThrottledStream.cs',
           'ThrottledStreamConcurrencyTests.cs'}

SUBS = [
    (r'getBytesPerSecond:\s*ctx\.SpeedLimitProvider', 'speedBudget: ctx.SpeedBudget'),
    (r'ctx\.SpeedLimitProvider', 'ctx.SpeedBudget'),
    (r'SpeedLimitProvider\s*=\s*\(\)\s*=>\s*null', 'SpeedBudget = SpeedBudget.Unlimited'),
    (r'SpeedLimitProvider', 'SpeedBudget'),
    (r'Func<long\?>\?\s+getBytesPerSecond', 'SpeedBudget? speedBudget'),
    (r'Func<long\?>\s+getBytesPerSecond', 'SpeedBudget speedBudget'),
    (r'getBytesPerSecond', 'speedBudget'),
]

changed = 0
for path in list(pathlib.Path('src').rglob('*.cs')) + list(pathlib.Path('tests').rglob('*.cs')):
    if any(p in ('bin', 'obj') for p in path.parts) or path.name in EXCLUDE:
        continue
    text = original = path.read_text(encoding='utf-8')
    for pattern, replacement in SUBS:
        text = re.sub(pattern, replacement, text)
    if text != original:
        path.write_text(text, encoding='utf-8', newline='\r\n')
        changed += 1
print(f'rewrote {changed} files')
```

Then let the compiler finish: composite delegate types it cannot see (`StorageToPipeline.cs:614`'s
local parameter; the override fields at `AlfafilePipeline.cs:45`, `BRuploadPipeline.cs:55`,
`CatboxPipeline.cs:46`, `XFileSharingApiPipeline.cs:415`) must be retyped by hand. Build, fix, repeat.

- [ ] **Step 5: Build and run both suites; commit.**

---

## What changed in r5

| r4 finding | Resolution |
|---|---|
| #1 `_lastRate` over-grants after a rate DROP (1 MB/s → 10 kB/s, 10 ms → a full second of the new limit) | Accrual now uses `min(_lastRate, currentRate)` — conservative on both sides. Pinned by `LoweringTheRate_DoesNotRetroactivelyEarnAtTheOldRate` (Codex's exact example) and its raise counterpart. Exact accounting would need a change *event*, which settings do not publish; that limitation is now stated. |
| #2 `GoUnlimited()` unreachable; stale limiter reused on re-enable | **Deleted.** With capacity bounded to 100 ms, a stale timestamp can bank only that much, which is the documented burst rather than a defect. |
| #3 manual-clock tests incompatible with lazy start | The constructor stamps the clock, so `_started` is gone and tests may advance time before first use. Every Task 1 and 2 test rewritten against the bounded capacity. |
| #4 "starts empty" only helps once; idle buckets bank a full second | **Answered as a product decision:** capacity is one tenth of a second. Pinned by `AnIdleBucket_BanksAtMostOneTenthOfASecond`. The false "configured rate over any interval" claim is replaced with "within 10% over any interval longer than the burst". |
| #5 `Refund` clamp contradicted `GoUnlimited` | Contradiction removed with `GoUnlimited`; `Refund` clamps against current capacity, falling back to the last known rate while unlimited. |
| #6 owner flapping can hot-livelock | A stale grant now yields `MinWait` before retrying. Pinned by `Acquire_WhenOwnershipFlapsEveryIteration_YieldsRatherThanSpinning`. |
| #7 Task 5's test only asserted non-null | `SpeedBudget.CurrentLimiter` exposed; the test now asserts which bucket the carried budget resolves to. |
| #8 stale `4 × 200,000` text; `EstimateWait` vs `MaxWait` mismatch | Text corrected. The 50 ms cap means the first grant is about half a capacity rather than a whole one, which is fine and no longer misdescribed. |

## What changed in r6

| r5 finding | Resolution |
|---|---|
| #1 ABA hole: an uncharged grant from a limiter that momentarily read null passes reference confirmation | A grant is trustworthy only if it was **charged** (`Limiter is not null`) or came from the **static** `SpeedLimiter.Unlimited`, which can never become limited. Pinned by `Acquire_RejectsAnUnchargedGrant_FromALimiterThatIsNotTheStaticUnlimited`, driving A→unlimited→A, plus a companion proving the legitimate uncharged path still short-circuits. |
| #2 the "within 10%" claim is false at short intervals | Replaced with the actual bound `bytes ≤ R×T + 0.1R` — the excess is 100 ms of data **once**, which is +100% at 100 ms, +10% at 1 s, +1% at 10 s. |
| #3 `min(old, current)` is conservative only across adjacent samples | Comment qualified: a drop that reverts between two reads is invisible to sampling, and the resulting over-accrual is bounded by the documented burst. |

## Remaining known risks

1. **No wakeup on refund** — bounded to 50 ms, not eliminated. Codex accepted this trade explicitly.
2. **No per-stream fairness** — the aggregate is guaranteed, the split is not.
3. **Rate changes are sampled, not observed**, so the interval containing a change is accounted at the lower of the two rates. Conservative across adjacent samples; a change that reverts between two reads is invisible, and that over-accrual is bounded by the 100 ms burst.
4. `TimeProvider` is new to this codebase; `ManualTimeProvider` is a hand-rolled double rather than a new package reference.
5. The 100 ms burst is a deliberate, tested product decision. If it is ever judged too generous, `BurstFraction` is the single knob.
