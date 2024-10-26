// <copyright file="IProtocolHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net
{
    public interface IProtocolHandler
    {
        /// <summary>
        /// Uploads the file asynchronous.
        /// </summary>
        /// <param name="filePath">The file path.</param>
        /// <param name="endpoint">The endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="Task" /> representing the asynchronous operation.</returns>
        Task UploadFileAsync(string filePath, string endpoint, CancellationToken cancellationToken);
    }
}
