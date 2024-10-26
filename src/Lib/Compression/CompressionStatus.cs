// <copyright file="CompressionStatus.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Compression
{
    public enum CompressionStatus
    {
        /// <summary>
        /// Compression not required
        /// </summary>
        Invalid,

        /// <summary>
        /// Compression required but not done yet
        /// </summary>
        Uncompressed,

        /// <summary>
        /// Compression completed
        /// </summary>
        Compressed
    }
}
