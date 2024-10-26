// <copyright file="ProtocolUploadFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net
{
    public class ProtocolUploadFinishedEventArgs : OperationFinishedEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolUploadFinishedEventArgs"/> class.
        /// </summary>
        /// <param name="success">if set to <c>true</c> [success].</param>
        /// <param name="result">The result.</param>
        /// <param name="startDateTime">The start date time.</param>
        public ProtocolUploadFinishedEventArgs(bool success, string? result, DateTime startDateTime)
            : base(success, startDateTime)
        {
            Result = result;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolUploadFinishedEventArgs"/> class.
        /// </summary>
        protected ProtocolUploadFinishedEventArgs()
            : base()
        {
        }

        /// <summary>
        /// Gets or sets the result from the remote server, or error message if success is false.
        /// </summary>
        public string? Result { get; protected set; }
    }
}
