// <copyright file="FileHosterUploadFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload
{
    public class FileHosterUploadFinishedEventArgs : ProtocolUploadFinishedEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FileHosterUploadFinishedEventArgs"/> class.
        /// </summary>
        /// <param name="e">The <see cref="HttpUploadFinishedEventArgs"/> instance containing the event data.</param>
        public FileHosterUploadFinishedEventArgs(HttpUploadFinishedEventArgs e)
            : base()
        {
            Success = e.Success;
            TimeElapsed = e.TimeElapsed;
            DateTimeFinished = e.DateTimeFinished;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileHosterUploadFinishedEventArgs"/> class.
        /// </summary>
        /// <param name="e">The <see cref="HttpUploadFinishedEventArgs"/> instance containing the event data.</param>
        /// <param name="fileInfo">The file information.</param>
        public FileHosterUploadFinishedEventArgs(HttpUploadFinishedEventArgs e, FileHosterFileInfo fileInfo)
            : base(e.Success, e.Result, e.DateTimeFinished)
        {
            FileInfo = fileInfo;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileHosterUploadFinishedEventArgs"/> class.
        /// </summary>
        /// <param name="success">if set to <c>true</c> [success].</param>
        /// <param name="result">The result.</param>
        /// <param name="dateTimeFinished">The date time finished.</param>
        public FileHosterUploadFinishedEventArgs(bool success, string result, DateTime dateTimeFinished)
            : base(success, result, dateTimeFinished)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileHosterUploadFinishedEventArgs"/> class.
        /// </summary>
        /// <param name="success">if set to <c>true</c> [success].</param>
        /// <param name="result">The result.</param>
        /// <param name="dateTimeFinished">The date time finished.</param>
        /// <param name="fileInfo">The file information.</param>
        public FileHosterUploadFinishedEventArgs(bool success, string result, DateTime dateTimeFinished, FileHosterFileInfo fileInfo)
            : this(success, result, dateTimeFinished)
        {
            FileInfo = fileInfo;
        }

        /// <summary>
        /// Gets or sets the information about the file.
        /// </summary>
        /// <remarks>
        /// This is only set when uploading was successful.
        /// </remarks>
        public FileHosterFileInfo? FileInfo { get; protected set; }
    }
}
