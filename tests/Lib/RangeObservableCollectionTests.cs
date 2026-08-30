// <copyright file="RangeObservableCollectionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using CSUploader.Lib;
using Xunit;

namespace CSUploader.Tests.Lib;

/// <summary>
/// Covers <see cref="RangeObservableCollection{T}"/> — the Uploads tab's VisibleRows backing type. The
/// contract under test: small batches keep the granular per-item events (grid selection/scroll survive),
/// large batches collapse to a single Reset (one O(N) view rebuild instead of thousands of per-row
/// updates), and in both regimes the resulting content and order are identical.
/// </summary>
public class RangeObservableCollectionTests
{
    private const int Threshold = RangeObservableCollection<int>.RangeThreshold;

    [Fact]
    public void InsertRange_BelowThreshold_RaisesPerItemAddEvents_InOrder()
    {
        RangeObservableCollection<int> col = [1, 2, 3];
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.InsertRange(1, [10, 11, 12]);

        Assert.Equal([1, 10, 11, 12, 2, 3], col);
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
    }

    [Fact]
    public void InsertRange_AtThreshold_RaisesOneReset_WithIdenticalContent()
    {
        RangeObservableCollection<int> col = [1, 2, 3];
        int[] block = [.. Enumerable.Range(100, Threshold)];
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.InsertRange(1, block);

        Assert.Equal([1, .. block, 2, 3], col);
        NotifyCollectionChangedEventArgs single = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, single.Action);
    }

    [Fact]
    public void RemoveRange_BelowThreshold_RaisesPerItemRemoveEvents()
    {
        RangeObservableCollection<int> col = [1, 2, 3, 4, 5];
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.RemoveRange([2, 4, 99]); // 99 absent — set semantics ignore it

        Assert.Equal([1, 3, 5], col);
        Assert.Equal(2, events.Count); // one Remove per PRESENT item; the absent one raises nothing
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Remove, e.Action));
    }

    [Fact]
    public void RemoveRange_AtThreshold_RaisesOneReset_PreservingSurvivorOrder()
    {
        // Interleave survivors with removals so a naive rebuild that reorders would be caught.
        RangeObservableCollection<int> col = [.. Enumerable.Range(0, (Threshold * 2) + 1)];
        int[] evens = [.. col.Where(i => i % 2 == 0)]; // Threshold+1 items → Reset regime
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.RemoveRange(evens);

        Assert.Equal(Enumerable.Range(0, (Threshold * 2) + 1).Where(i => i % 2 == 1), col);
        NotifyCollectionChangedEventArgs single = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, single.Action);
    }

    [Fact]
    public void RemoveRange_NothingPresent_RaisesNoEvent()
    {
        RangeObservableCollection<int> col = [1, 2, 3];
        int[] absent = [.. Enumerable.Range(1000, Threshold)]; // big enough for the Reset regime
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.RemoveRange(absent);

        Assert.Equal([1, 2, 3], col);
        Assert.Empty(events); // no change → no Reset (a spurious Reset would repaint the grid for nothing)
    }

    [Fact]
    public void EmptyBatches_AreNoOps()
    {
        RangeObservableCollection<int> col = [1];
        List<NotifyCollectionChangedEventArgs> events = [];
        col.CollectionChanged += (_, e) => events.Add(e);

        col.InsertRange(0, []);
        col.RemoveRange([]);

        Assert.Equal([1], col);
        Assert.Empty(events);
    }
    [Fact]
    public void ReplaceAll_RaisesExactlyOneReset_NeverAnEmptyOne()
    {
        // The Uploads tab re-ranks in one step when a sort is applied. Clear() + InsertRange is
        // TWO resets, and the first one is an EMPTY collection: the grid drops selection and
        // currency against a momentarily empty list before the rows come back. One reset over
        // already-swapped contents keeps whatever still exists selected.
        RangeObservableCollection<int> col = [1, 2, 3];
        List<NotifyCollectionChangedEventArgs> events = [];
        List<int> countsSeen = [];
        col.CollectionChanged += (_, e) =>
        {
            events.Add(e);
            countsSeen.Add(col.Count);
        };

        col.ReplaceAll([3, 2, 1]);

        Assert.Equal([3, 2, 1], col);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);
        Assert.Equal([3], countsSeen);
    }

    [Fact]
    public void ReplaceAll_WithIdenticalContent_StillReordersInPlace()
    {
        RangeObservableCollection<int> col = [1, 2, 3];

        col.ReplaceAll([2, 3, 1]);

        Assert.Equal([2, 3, 1], col);
    }

    [Fact]
    public void ReplaceAll_Empty_ClearsTheCollection()
    {
        RangeObservableCollection<int> col = [1, 2, 3];

        col.ReplaceAll([]);

        Assert.Empty(col);
    }

}
