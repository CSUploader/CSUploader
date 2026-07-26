// <copyright file="RangeObservableCollection.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CSUploader.Lib;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with bulk insert/remove that raise a SINGLE
/// <see cref="NotifyCollectionChangedAction.Reset"/> instead of one event per element.
/// <para>
/// Exists for the Uploads tab's <c>VisibleRows</c>: expanding/collapsing/adding/removing a large package
/// mutated the row list one element at a time — each op an O(N) scan/shift PLUS one CollectionChanged the
/// DataGrid's collection view processed individually — so a 2,000-file package froze the UI thread with
/// millions of comparisons and thousands of view updates. A Reset lets the view rebuild once, O(N), the
/// same proven-cheap path a filter refresh already takes.
/// </para>
/// <para>
/// Small batches (fewer than <see cref="RangeThreshold"/> items) keep the per-item base-class behavior:
/// granular events preserve grid selection/scroll where the batch is too small for the Reset to win.
/// </para>
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Batch size at which bulk ops switch from per-item events to a single Reset. Below this
    /// the per-item cost is negligible and granular events are friendlier to the grid (selection/scroll
    /// survive); above it the Reset's one O(N) rebuild beats N individual view updates. Internal so tests
    /// can size fixtures against it.</summary>
    internal const int RangeThreshold = 50;

    /// <summary>Inserts <paramref name="items"/> consecutively starting at <paramref name="index"/>.
    /// Raises per-item Add events below <see cref="RangeThreshold"/>, one Reset at/above it.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count < RangeThreshold)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Insert(index + i, items[i]);
            }

            return;
        }

        CheckReentrancy();
        if (Items is List<T> list)
        {
            list.InsertRange(index, items);
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                Items.Insert(index + i, items[i]);
            }
        }

        RaiseReset();
    }

    /// <summary>Removes every element of <paramref name="items"/> that is present (set semantics —
    /// absent items are ignored), preserving the relative order of the survivors. Raises per-item Remove
    /// events below <see cref="RangeThreshold"/>, one Reset at/above it.</summary>
    public void RemoveRange(IReadOnlyCollection<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count < RangeThreshold)
        {
            foreach (T item in items)
            {
                Remove(item);
            }

            return;
        }

        CheckReentrancy();
        HashSet<T> remove = new(items);
        List<T> keep = new(Items.Count);
        foreach (T existing in Items)
        {
            if (!remove.Contains(existing))
            {
                keep.Add(existing);
            }
        }

        if (keep.Count == Items.Count)
        {
            return; // nothing was actually present — no change, no event
        }

        Items.Clear();
        if (Items is List<T> list)
        {
            list.AddRange(keep);
        }
        else
        {
            foreach (T item in keep)
            {
                Items.Add(item);
            }
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
