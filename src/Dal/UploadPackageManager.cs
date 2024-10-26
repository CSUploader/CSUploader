// <copyright file="UploadPackageManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class UploadPackageManager
        : StoreManager<UploadPackageDbm, UploadPackageDto, UploadPackageStore>
    {
        public UploadPackageManager(UploadPackageStore uploadPackageStore)
            : base(uploadPackageStore)
        {
        }

        public async Task<UploadPackageDto> FindAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            UploadPackageDbm dbm = await Store.FindAsync(id, cancellationToken);

            return Map(dbm);
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(id, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await Store.DeleteAsync(ids, cancellationToken);
        }
    }
}
