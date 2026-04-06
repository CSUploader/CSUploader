// <copyright file="ProtocolUploadProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

public class ProtocolUploadProgressEventArgs : OperationProgressEventArgs
{
    public ProtocolUploadProgressEventArgs(long size, long bytesProcessed, DateTime dateTimeStarted)
        : base(size, bytesProcessed, dateTimeStarted)
    {
    }

    protected ProtocolUploadProgressEventArgs()
        : base()
    {
    }
}
