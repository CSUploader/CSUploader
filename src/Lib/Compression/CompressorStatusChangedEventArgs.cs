// <copyright file="CompressorStatusChangedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Compression;

public class CompressorStatusChangedEventArgs : JobStatusChangedEventArgs
{
    public CompressorStatusChangedEventArgs(JobStatus? previousStatus, JobStatus newStatus)
        : base(previousStatus, newStatus)
    {
    }
}
