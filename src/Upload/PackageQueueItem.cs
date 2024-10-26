// <copyright file="PackageQueueItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload
{
    public class PackageQueueItem
    {
        public PackageDetails PackageDetails { get; set; }

        public PackageJob PackageJob { get; set; }

        public PackageQueueItem(PackageDetails packageDetails, PackageJob packageJob)
        {
            PackageDetails = packageDetails;
            PackageJob = packageJob;
        }
    }
}
