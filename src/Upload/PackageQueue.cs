// <copyright file="PackageQueue.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;

namespace CSUploader.Upload;

public class PackageQueue
{
    private readonly object lockObject = new();

    private readonly LinkedList<PackageQueueItem> queue = new();

    public void Enqueue(PackageJob packageJob, PackageDetails packageDetails)
    {
        lock (lockObject)
        {
            // Check if item is already queued
            if (queue.Any(i => ReferenceEquals(i.PackageDetails, packageDetails)))
            {
                return;
            }

            PackageQueueItem queueItem = new(packageDetails, packageJob);
            
            queue.AddLast(queueItem);

            return;
        }
    }

    public bool TryDequeue<T>(PackageJob packageJob, [NotNullWhen(true)] out T? package)
        where T : class
    {
        package = null;

        lock (lockObject)
        {
            PackageQueueItem? queueItem = null;
            foreach (PackageQueueItem item in queue.Where(i => i.PackageJob == packageJob && i.PackageDetails is T))
            {
                if (queueItem == null || item.PackageDetails.Priority > queueItem.PackageDetails.Priority)
                {
                    queueItem = item;
                }
            }

            if (queueItem != null)
            {
                package = queueItem.PackageDetails as T;
                if (package != null)
                {
                    queue.Remove(queueItem);
                    return true;
                }
            }
        }

        return false;
    }

    public bool Remove(PackageDetails packageDetails)
    {
        lock (lockObject)
        {
            PackageQueueItem? item = queue.FirstOrDefault(i => ReferenceEquals(i.PackageDetails, packageDetails));
            if (item != null)
            {
                queue.Remove(item);
                return true;
            }

            return false;
        }
    }

    public bool IsQueued(PackageDetails packageDetails)
    {
        lock (lockObject)
        {
            return queue.Any(i => ReferenceEquals(i.PackageDetails, packageDetails));
        }
    }

    public bool Any(Func<PackageDetails, bool> action)
    {
        lock (lockObject)
        {
            return queue.Any(i => action(i.PackageDetails));
        }
    }

    public int Count(PackageJob packageJob)
    {
        lock (lockObject)
        {
            return queue.Count(i => i.PackageJob == packageJob);
        }
    }
}
