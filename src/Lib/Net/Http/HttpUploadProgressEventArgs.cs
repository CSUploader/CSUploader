// <copyright file="HttpUploadProgressEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;

namespace CSUploader.Lib.Net.Http
{
    public class HttpUploadProgressEventArgs : ProtocolUploadProgressEventArgs
    {
        public HttpUploadProgressEventArgs(long size, long bytesProcessed, DateTime dateTimeStarted)
            : base(size, bytesProcessed, dateTimeStarted)
        {
        }

        public HttpUploadProgressEventArgs(DateTime startDateTime, UploadProgressChangedEventArgs e)
            : base(e.TotalBytesToSend, e.BytesSent, startDateTime)
        {
        }

        protected HttpUploadProgressEventArgs()
            : base()
        {
        }
    }
}
