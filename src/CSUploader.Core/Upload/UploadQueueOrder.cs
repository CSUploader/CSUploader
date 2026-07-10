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
    /// <summary>
    /// Assigns QueueOrder = 1..N over <paramref name="ordered"/> in its current order.
    /// Returns true if any file's QueueOrder actually changed (so callers can skip a persist
    /// write when the queue was already dense).
    /// </summary>
    public static bool Renumber(IReadOnlyList<PackageFile> ordered)
    {
        bool changed = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].QueueOrder != i + 1)
            {
                ordered[i].QueueOrder = i + 1;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Moves <paramref name="file"/> to 1-based position <paramref name="target"/> (clamped to
    /// [1, N]) and rewrites <see cref="PackageFile.QueueOrder"/> on every file to reflect the new
    /// order. The items in between shift by one position. The list itself is not reordered — only
    /// the QueueOrder properties are updated. <paramref name="ordered"/> must be the current queue
    /// sorted ascending by QueueOrder. Returns true if any QueueOrder changed — false when the file
    /// isn't present, the list is empty, or the file is already at <paramref name="target"/>.
    /// </summary>
    public static bool MoveTo(List<PackageFile> ordered, PackageFile file, int target)
    {
        int current = ordered.IndexOf(file);
        if (current < 0 || ordered.Count == 0)
        {
            return false;
        }

        target = Math.Clamp(target, 1, ordered.Count);
        List<PackageFile> working = [.. ordered];
        working.RemoveAt(current);
        working.Insert(target - 1, file);
        return Renumber(working);
    }

    /// <summary>
    /// Moves the <paramref name="selected"/> files (those present in <paramref name="ordered"/>),
    /// kept as a contiguous block in their current relative order, by <paramref name="delta"/>
    /// positions (negative = sooner), and rewrites <see cref="PackageFile.QueueOrder"/> on every
    /// file to reflect the new order. Clamped so the block stays within bounds. The list itself is
    /// not reordered — only the QueueOrder properties are updated. Returns true if any QueueOrder
    /// changed — false when <paramref name="delta"/> is 0, no selected file is present, or the
    /// clamped move leaves every file at its current position.
    /// </summary>
    public static bool MoveBy(List<PackageFile> ordered, IReadOnlyCollection<PackageFile> selected, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        List<PackageFile> block = [.. ordered.Where(selected.Contains)];
        if (block.Count == 0)
        {
            return false;
        }

        int firstIdx = ordered.IndexOf(block[0]);
        List<PackageFile> working = [.. ordered];
        foreach (PackageFile f in block)
        {
            working.Remove(f);
        }

        int insertAt = Math.Clamp(firstIdx + delta, 0, working.Count);
        working.InsertRange(insertAt, block);
        return Renumber(working);
    }
}
