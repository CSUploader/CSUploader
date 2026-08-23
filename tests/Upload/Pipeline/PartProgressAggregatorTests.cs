// <copyright file="PartProgressAggregatorTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline;

/// <summary>
/// One file-level progress figure from parts that are in flight together.
/// <para>
/// The sequential path reports <c>basePosition + bytesInThisChunk</c> — an absolute file position,
/// meaningful only when parts complete in order. Run them together and that lurches backwards
/// whenever a lower-numbered part reports after a higher one, and
/// <c>OperationProgressEventArgs</c> derives bytes-remaining, percentage, speed, ETA and finish
/// time from it.
/// </para>
/// </summary>
public class PartProgressAggregatorTests
{
    [Fact]
    public void Report_AcrossParts_PublishesTheSumNotTheLatestPartsPosition()
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
        // A part reports its own running total repeatedly as its body streams; adding those up
        // would publish several times the file size.
        List<long> published = [];
        PartProgressAggregator aggregator = new(2, published.Add);

        aggregator.Report(0, 100);
        aggregator.Report(0, 200);

        Assert.Equal([100, 200], published);
    }

    /// <summary>
    /// UploadNow retries a part internally (<c>WithStorageRetryAsync</c>), restarting that part's
    /// counter near zero. Subtracting its previous contribution would make the FILE's total fall,
    /// which is the exact defect this class exists to prevent — so each part keeps a high-water
    /// mark and the total plateaus until the resent part passes its old position.
    /// </summary>
    [Fact]
    public void Report_WhenAPartIsRetriedFromTheStart_PlateausTheTotalRatherThanDroppingIt()
    {
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
    public async Task Report_UnderConcurrentParts_PublishesTotalsThatNeverGoBackwards()
    {
        // A final-sum assertion passes even when totals are published out of order, which is the
        // same defect wearing a different hat. This asserts the ORDER of what was published.
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
    public void Report_WhenThePublisherThrows_KeepsGoingRatherThanFailingTheUpload()
    {
        // The publish callback ends up in request-body serialization, so a progress subscriber's
        // exception must never surface as an upload failure — and must not leave the drain latched,
        // which would silently stop all further progress.
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

    [Fact]
    public void Report_DoesNotHoldItsLockWhilePublishing()
    {
        // Publishing under the lock would run arbitrary subscriber code — ultimately the UI — while
        // holding it. It would not DEADLOCK on the same thread (System.Threading.Lock is re-entrant,
        // like Monitor): the re-entrant Report would take the lock again and publish its total from
        // inside the outer publish, so the inner figure surfaces before the outer one. Verified by
        // mutation — publishing inside the lock fails exactly this test.
        List<long> published = [];
        PartProgressAggregator? aggregator = null;
        bool reentered = false;

        aggregator = new PartProgressAggregator(2, total =>
        {
            published.Add(total);
            if (!reentered)
            {
                reentered = true;
                aggregator!.Report(1, 5); // re-entrant call from inside the publisher
            }
        });

        aggregator.Report(0, 10);

        Assert.Equal([10, 15], published);
    }
}
