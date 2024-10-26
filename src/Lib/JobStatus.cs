// <copyright file="JobStatus.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib
{
    /// <summary>
    /// Job status.
    /// </summary>
    [Flags]
    public enum JobStatus
    {
        /// <summary>
        /// Idle.
        /// </summary>
        Idle = 0x00,

        /// <summary>
        /// The job is queued.
        /// </summary>
        Queued = 0x01,

        /// <summary>
        /// The job is running.
        /// </summary>
        Running = 0x02,

        /// <summary>
        /// The job is paused.
        /// </summary>
        Paused = 0x04,

        /// <summary>
        /// The job is cancelled.
        /// </summary>
        Cancelled = 0x08,

        /// <summary>
        /// The job failed.
        /// </summary>
        Failed = 0x10,

        /// <summary>
        /// The job is successful.
        /// </summary>
        Success = 0x20
    }
}
