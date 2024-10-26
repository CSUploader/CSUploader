// <copyright file="PackageStatusChangedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Upload
{
    public class PackageStatusChangedEventArgs : JobStatusChangedEventArgs
    {
        public PackageStatusChangedEventArgs(PackageDetails package, PackageJob packageJob, JobStatus? previousStatus, JobStatus newStatus)
            : base(previousStatus, newStatus)
        {
            Package = package;
            PackageJob = packageJob;
        }

        public PackageDetails Package { get; private set; }

        public PackageJob PackageJob { get; private set; }
    }
}
