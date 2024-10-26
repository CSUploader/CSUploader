// <copyright file="FileHosterLoginManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class FileHosterLoginManager
        : StoreManager<FileHosterLoginDbm, FileHosterLoginDto, FileHosterLoginStore>
    {
        public FileHosterLoginManager(FileHosterLoginStore fileHosterStore)
            : base(fileHosterStore)
        {
        }

        public async Task<FileHosterLoginDto> FindAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileHosterLoginDbm dbm = await Store.FindAsync(id, cancellationToken);
            return Map(dbm);
        }

        public async Task<FileHosterLoginDto[]> FindAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileHosterLoginDbm[] dbms = await Store.FindAsync(name, cancellationToken);

            return Map(dbms);
        }

        public async Task<FileHosterLoginDto?> FindAsync(string name, string username, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileHosterLoginDbm? dbm = (await Store.FindAsync(name, cancellationToken)).FirstOrDefault(f => f.Username == username);

            return dbm != null ? Map(dbm) : null;
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
