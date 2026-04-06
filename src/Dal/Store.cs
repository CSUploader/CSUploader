// <copyright file="Store.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class Store<DbmModel>(IDbContextFactory<CSUploaderDbContext> dbFactory)
    where DbmModel : class
{
    protected IDbContextFactory<CSUploaderDbContext> DbFactory { get; } = dbFactory;

    public virtual async Task<DbmModel[]> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<DbmModel> qry = GetQuery(db);
        return await qry.ToArrayAsync(cancellationToken);
    }

    public virtual async Task<int> InsertAsync(DbmModel model, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<DbmModel>().Add(model);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<int> InsertAsync(IEnumerable<DbmModel> models, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<DbmModel>().AddRange(models);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<int> UpdateAsync(DbmModel model, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<DbmModel>().Update(model);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<int> DeleteAsync(DbmModel model, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<DbmModel>().Remove(model);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<int> DeleteAsync(IEnumerable<DbmModel> models, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<DbmModel>().RemoveRange(models);
        return await db.SaveChangesAsync(cancellationToken);
    }

    protected virtual IQueryable<DbmModel> GetQuery(CSUploaderDbContext db)
    {
        return db.Set<DbmModel>();
    }

    protected async Task<DbmModel[]> FindAsync(Expression<Func<DbmModel, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<DbmModel> qry = GetQuery(db);
        return await qry.Where(predicate).ToArrayAsync(cancellationToken);
    }

    protected async Task<DbmModel> FindFirstAsync(Expression<Func<DbmModel, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<DbmModel> qry = GetQuery(db);
        return await qry.FirstAsync(predicate, cancellationToken);
    }

    protected async Task<DbmModel?> FindFirstOrDefaultAsync(Expression<Func<DbmModel, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<DbmModel> qry = GetQuery(db);
        return await qry.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    protected async Task<int> DeleteAsync(Expression<Func<DbmModel, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<DbmModel>().Where(predicate).ExecuteDeleteAsync(cancellationToken);
    }
}
