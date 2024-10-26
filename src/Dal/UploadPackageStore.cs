// <copyright file="UploadPackageStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class UploadPackageStore : Store<UploadPackageDbm>
    {
        public async Task<UploadPackageDbm> FindAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindFirstOrDefaultAsync(fu => fu.Id == id, cancellationToken);
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fu => fu.Id == id, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fu => ids.Contains(fu.Id), cancellationToken);
        }
    }
}
