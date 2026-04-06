// <copyright file="HttpUploadFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

public class HttpUploadFinishedEventArgs : ProtocolUploadFinishedEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HttpUploadFinishedEventArgs"/> class.
    /// </summary>
    /// <param name="success">if set to <c>true</c> [success].</param>
    /// <param name="result">The result.</param>
    /// <param name="startDateTime">The start date time.</param>
    public HttpUploadFinishedEventArgs(bool success, string result, DateTime startDateTime)
        : base(success, result, startDateTime)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpUploadFinishedEventArgs"/> class.
    /// </summary>
    protected HttpUploadFinishedEventArgs()
    {
    }
}
