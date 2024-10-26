// <copyright file="UploadPackageFileManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class UploadPackageFileManager
        : StoreManager<UploadPackageFileDbm, UploadPackageFileDto, UploadPackageFileStore>
    {
        public UploadPackageFileManager(UploadPackageFileStore fileUploadStore)
            : base(fileUploadStore)
        {
        }

        public async Task<UploadPackageFileDto> FindAsync(int fileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UploadPackageFileDbm dbm = await Store.FindAsync(fileId, cancellationToken);

            return Map(dbm);
        }

        public async Task<int> DeleteAsync(int fileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(fileId, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(fileIds, cancellationToken);
        }
    }
}
