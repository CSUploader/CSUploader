// <copyright file="FileHosterHashingProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Crypto;

namespace CSUploader.Upload
{
    public class FileHosterHashingProgressEventArgs : HashingProgressEventArgs
    {
        public FileHosterHashingProgressEventArgs(long size, long bytesProcessed, DateTime dateTimeStarted)
            : base(size, bytesProcessed, dateTimeStarted)
        {
        }

        public FileHosterHashingProgressEventArgs(HashingProgressEventArgs e)
            : base(e.Size, e.BytesProcessed, e.DateTimeFinish)
        {
        }
    }
}
