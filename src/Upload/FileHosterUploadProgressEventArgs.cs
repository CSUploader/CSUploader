// <copyright file="FileHosterUploadProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload
{
    public class FileHosterUploadProgressEventArgs : ProtocolUploadProgressEventArgs
    {
        public FileHosterUploadProgressEventArgs(HttpUploadProgressEventArgs e)
            : base(e.Size, e.BytesProcessed, e.DateTimeStarted)
        {
        }

        public FileHosterUploadProgressEventArgs(DateTime startDateTime, UploadProgressChangedEventArgs e)
            : base(e.TotalBytesToSend, e.BytesSent, startDateTime)
        {
        }

        public FileHosterUploadProgressEventArgs(long size, long bytesUploaded, DateTime dateTimeStarted)
            : base(size, bytesUploaded, dateTimeStarted)
        {
        }
    }
}
