// <copyright file="Repository.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public abstract class Repository<TEntity, TDto>(IDbContextFactory<CSUploaderDbContext> dbFactory)
    where TEntity : class, new()
    where TDto : class, new()
{
    protected IDbContextFactory<CSUploaderDbContext> DbFactory { get; } = dbFactory;

    public virtual async Task<TDto[]> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<TEntity> qry = GetQuery(db);
        TEntity[] entities = await qry.ToArrayAsync(cancellationToken);
        return [.. entities.Select(MapToDto)];
    }

    public virtual async Task<int> InsertAsync(TDto dto, CancellationToken cancellationToken = default)
    {
        TEntity entity = MapToDbm(dto);
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<TEntity>().Add(entity);
        int ret = await db.SaveChangesAsync(cancellationToken);
        MapToDto(entity, dto);
        return ret;
    }

    public virtual async Task<int> InsertAsync(IEnumerable<TDto> dtos, CancellationToken cancellationToken = default)
    {
        TDto[] dtoArray = [.. dtos];
        TEntity[] entities = [.. dtoArray.Select(MapToDbm)];
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<TEntity>().AddRange(entities);
        int ret = await db.SaveChangesAsync(cancellationToken);
        for (int i = 0; i < entities.Length; i++)
        {
            MapToDto(entities[i], dtoArray[i]);
        }

        return ret;
    }

    public virtual async Task<int> UpdateAsync(TDto dto, CancellationToken cancellationToken = default)
    {
        TEntity entity = MapToDbm(dto);
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<TEntity>().Update(entity);
        int ret = await db.SaveChangesAsync(cancellationToken);
        MapToDto(entity, dto);
        return ret;
    }

    public virtual async Task<int> DeleteAsync(TDto dto, CancellationToken cancellationToken = default)
    {
        TEntity entity = MapToDbm(dto);
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<TEntity>().Remove(entity);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task<int> DeleteAsync(IEnumerable<TDto> dtos, CancellationToken cancellationToken = default)
    {
        TEntity[] entities = [.. dtos.Select(MapToDbm)];
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        db.Set<TEntity>().RemoveRange(entities);
        return await db.SaveChangesAsync(cancellationToken);
    }

    protected virtual IQueryable<TEntity> GetQuery(CSUploaderDbContext db) => db.Set<TEntity>();

    protected async Task<TEntity[]> FindAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<TEntity> qry = GetQuery(db);
        return await qry.Where(predicate).ToArrayAsync(cancellationToken);
    }

    protected async Task<TEntity> FindFirstAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<TEntity> qry = GetQuery(db);
        return await qry.FirstAsync(predicate, cancellationToken);
    }

    protected async Task<TEntity?> FindFirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        IQueryable<TEntity> qry = GetQuery(db);
        return await qry.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    protected async Task<int> DeleteByPredicateAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
    {
        using CSUploaderDbContext db = DbFactory.CreateDbContext();
        return await db.Set<TEntity>().Where(predicate).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Maps a database model to a new DTO.
    /// </summary>
    protected abstract TDto MapToDto(TEntity entity);

    /// <summary>
    /// Maps a database model back into an existing DTO (for populating generated IDs after insert/update).
    /// </summary>
    protected abstract void MapToDto(TEntity entity, TDto dto);

    /// <summary>
    /// Maps a DTO to a new database model.
    /// </summary>
    protected abstract TEntity MapToDbm(TDto dto);
}
