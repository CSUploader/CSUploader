// <copyright file="FileHosterLoginStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class FileHosterLoginStore : Store<FileHosterLoginDbm>
    {
        public async Task<FileHosterLoginDbm> FindAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindFirstOrDefaultAsync(fh => fh.Id == id, cancellationToken);
        }

        public async Task<FileHosterLoginDbm[]> FindAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await FindAsync(fh => fh.FileHosterName == name, cancellationToken);
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fh => fh.Id == id, cancellationToken);
        }

        public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return await DeleteAsync(fh => ids.Contains(fh.Id), cancellationToken);
        }
    }
}
