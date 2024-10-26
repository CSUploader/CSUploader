// <copyright file="PackageAddedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload
{
    public class PackageAddedEventArgs : EventArgs
    {
        public PackageAddedEventArgs(PackageDetails? parentPackage, PackageDetails[] childPackages)
        {
            ParentPackage = parentPackage;
            ChildPackages = childPackages;
        }

        public PackageDetails? ParentPackage { get; private set; }

        public PackageDetails[] ChildPackages { get; private set; }
    }
}
