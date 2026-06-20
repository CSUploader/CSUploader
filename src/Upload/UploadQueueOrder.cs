// <copyright file="UploadQueueOrder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Pure ordering algebra for the flat upload queue. Takes a list of non-terminal
/// <see cref="PackageFile"/> in current upload order and rewrites each file's
/// <see cref="PackageFile.QueueOrder"/> to a dense 1..N reflecting the requested move.
/// None of these methods reorder the passed-in list; they only write new
/// <see cref="PackageFile.QueueOrder"/> values — sort by QueueOrder to read the result back.
/// No I/O, no scheduling — the scheduler calls these on its loop and persists the result.
/// </summary>
internal static class UploadQueueOrder
{
    /// <summary>Assigns QueueOrder = 1..N over <paramref name="ordered"/> in its current order.</summary>
    public static void Renumber(IReadOnlyList<PackageFile> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].QueueOrder = i + 1;
        }
    }

    /// <summary>
    /// Moves <paramref name="file"/> to 1-based position <paramref name="target"/> (clamped to
    /// [1, N]) and rewrites <see cref="PackageFile.QueueOrder"/> on every file to reflect the new
    /// order. The items in between shift by one position. The list itself is not reordered — only
    /// the QueueOrder properties are updated. <paramref name="ordered"/> must be the current queue
    /// sorted ascending by QueueOrder.
    /// </summary>
    public static void MoveTo(List<PackageFile> ordered, PackageFile file, int target)
    {
        int current = ordered.IndexOf(file);
        if (current < 0 || ordered.Count == 0)
        {
            return;
        }

        target = Math.Clamp(target, 1, ordered.Count);
        List<PackageFile> working = [.. ordered];
        working.RemoveAt(current);
        working.Insert(target - 1, file);
        Renumber(working);
    }

    /// <summary>
    /// Moves the <paramref name="selected"/> files (those present in <paramref name="ordered"/>),
    /// kept as a contiguous block in their current relative order, by <paramref name="delta"/>
    /// positions (negative = sooner), and rewrites <see cref="PackageFile.QueueOrder"/> on every
    /// file to reflect the new order. Clamped so the block stays within bounds. The list itself is
    /// not reordered — only the QueueOrder properties are updated.
    /// </summary>
    public static void MoveBy(List<PackageFile> ordered, IReadOnlyCollection<PackageFile> selected, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        List<PackageFile> block = [.. ordered.Where(selected.Contains)];
        if (block.Count == 0)
        {
            return;
        }

        int firstIdx = ordered.IndexOf(block[0]);
        List<PackageFile> working = [.. ordered];
        foreach (PackageFile f in block)
        {
            working.Remove(f);
        }

        int insertAt = Math.Clamp(firstIdx + delta, 0, working.Count);
        working.InsertRange(insertAt, block);
        Renumber(working);
    }
}
