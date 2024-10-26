// <copyright file="DbManagerFileUpload.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class DbManager
    {
        public virtual Task<UploadPackageFileDto[]> GetFileUploadsAsync(CancellationToken cancellationToken = default)
        {
            return UploadPackageFileManager.GetAllAsync(cancellationToken);
        }
    }
}
