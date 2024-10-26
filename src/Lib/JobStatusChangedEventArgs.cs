// <copyright file="JobStatusChangedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib
{
    public class JobStatusChangedEventArgs : EventArgs
    {
        public JobStatusChangedEventArgs(JobStatus? previousStatus, JobStatus newStatus)
        {
            PreviousStatus = previousStatus;
            NewStatus = newStatus;
        }

        public JobStatus? PreviousStatus { get; private set; }

        public JobStatus NewStatus { get; private set; }
    }
}
