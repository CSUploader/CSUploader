// <copyright file="FileHosterHashingFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Crypto;

namespace CSUploader.Upload;

public class FileHosterHashingFinishedEventArgs : HashingFinishedEventArgs
{
    public FileHosterHashingFinishedEventArgs(bool success, DateTime startDateTime, byte[] hash)
        : base(success, startDateTime, hash)
    {
    }

    public FileHosterHashingFinishedEventArgs(HashingFinishedEventArgs e)
        : base(e.Success, e.DateTimeFinished - e.TimeElapsed, e.Hash)
    {
    }
}
