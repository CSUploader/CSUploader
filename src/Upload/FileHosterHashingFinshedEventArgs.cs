// <copyright file="FileHosterHashingFinshedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Crypto;

namespace CSUploader.Upload
{
    public class FileHosterHashingFinshedEventArgs : HashingFinishedEventArgs
    {
        public FileHosterHashingFinshedEventArgs(bool success, DateTime startDateTime, byte[] hash)
            : base(success, startDateTime, hash)
        {
        }

        public FileHosterHashingFinshedEventArgs(HashingFinishedEventArgs e)
            : base(e.Success, e.DateTimeFinished - e.TimeElapsed, e.Hash)
        {
        }
    }
}
