// <copyright file="UploadPackageFileStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class UploadPackageFileStore : Store<UploadPackageFileDbm>
    {
        public async Task<UploadPackageFileDbm> FindAsync(int fileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindFirstOrDefaultAsync(fu => fu.Id == fileId, cancellationToken);
        }

        public async Task<int> DeleteAsync(int fileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fu => fu.Id == fileId, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> fileIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fu => fileIds.Contains(fu.Id), cancellationToken);
        }
    }
}
