// <copyright file="UploadPackageStore.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class UploadPackageStore(IDbContextFactory<CSUploaderDbContext> dbFactory)
    : Store<UploadPackageDbm>(dbFactory)
{
    protected override IQueryable<UploadPackageDbm> GetQuery(CSUploaderDbContext db)
    {
        return db.Set<UploadPackageDbm>().Include(p => p.Files);
    }

    public async Task<UploadPackageDbm?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        return await FindFirstOrDefaultAsync(fu => fu.Id == id, cancellationToken);
    }

    public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        return await DeleteAsync(fu => fu.Id == id, cancellationToken);
    }

    public async Task<int> DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        return await DeleteAsync(fu => ids.Contains(fu.Id), cancellationToken);
    }
}
