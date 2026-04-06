// <copyright file="HashingProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Crypto;

public class HashingProgressEventArgs : OperationProgressEventArgs
{
    public HashingProgressEventArgs(long size, long bytesProcessed, DateTime dateTimeStarted)
        : base(size, bytesProcessed, dateTimeStarted)
    {
    }
}
