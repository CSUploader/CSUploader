// <copyright file="StoreManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

public abstract class StoreManager<DbmModel, DtoModel, StoreModel>
    where DbmModel : class, new()
    where DtoModel : class, new()
    where StoreModel : Store<DbmModel>
{
    protected StoreModel Store { get; }

    protected StoreManager(StoreModel store)
    {
        Store = store;
    }

    public virtual async Task<DtoModel[]> GetAllAsync(CancellationToken cancellationToken = default)
    {
        DbmModel[] dbmModels = await Store.GetAllAsync(cancellationToken);
        return dbmModels.Select(MapToDto).ToArray();
    }

    public virtual async Task<int> InsertAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
    {
        DbmModel dbmModel = MapToDbm(dtoModel);
        int ret = await Store.InsertAsync(dbmModel, cancellationToken);
        MapToDto(dbmModel, dtoModel);
        return ret;
    }

    public virtual async Task<int> InsertAsync(IEnumerable<DtoModel> dtoModels, CancellationToken cancellationToken = default)
    {
        DtoModel[] dtoArray = dtoModels.ToArray();
        DbmModel[] dbmModels = dtoArray.Select(MapToDbm).ToArray();
        int ret = await Store.InsertAsync(dbmModels, cancellationToken);
        for (int i = 0; i < dbmModels.Length; i++)
        {
            MapToDto(dbmModels[i], dtoArray[i]);
        }

        return ret;
    }

    public virtual async Task<int> UpdateAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
    {
        DbmModel dbmModel = MapToDbm(dtoModel);
        int ret = await Store.UpdateAsync(dbmModel, cancellationToken);
        MapToDto(dbmModel, dtoModel);
        return ret;
    }

    public virtual async Task<int> DeleteAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
    {
        DbmModel dbmModel = MapToDbm(dtoModel);
        return await Store.DeleteAsync(dbmModel, cancellationToken);
    }

    public virtual async Task<int> DeleteAsync(IEnumerable<DtoModel> dtoModels, CancellationToken cancellationToken = default)
    {
        DbmModel[] dbmModels = dtoModels.Select(MapToDbm).ToArray();
        return await Store.DeleteAsync(dbmModels, cancellationToken);
    }

    /// <summary>
    /// Maps a database model to a new DTO.
    /// </summary>
    protected abstract DtoModel MapToDto(DbmModel dbm);

    /// <summary>
    /// Maps a database model back into an existing DTO (for populating generated IDs after insert/update).
    /// </summary>
    protected abstract void MapToDto(DbmModel dbm, DtoModel dto);

    /// <summary>
    /// Maps a DTO to a new database model.
    /// </summary>
    protected abstract DbmModel MapToDbm(DtoModel dto);
}
