// <copyright file="StoreManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public abstract class StoreManager<DbmModel, DtoModel, StoreModel>
        where DbmModel : class
        where DtoModel : class
        where StoreModel : Store<DbmModel>
    {
        protected StoreModel Store { get; private set; }

        public StoreManager(StoreModel store)
        {
            Store = store;
        }

        public virtual async Task<DtoModel[]> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DbmModel[] dbmModels = await Store.GetAllAsync(cancellationToken);
            return Map(dbmModels);
        }

        public virtual async Task<int> InsertAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DbmModel dbmModel = Map(dtoModel);
            int ret = await Store.InsertAsync(dbmModel, cancellationToken);
            Map(dbmModel, dtoModel);

            return ret;
        }

        public virtual async Task<int> InsertAsync(IEnumerable<DtoModel> dtoModels, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<DtoModel, DbmModel> models = MapToDictionary(dtoModels);
            DbmModel[] dbmModels = models.Select(m => m.Value).ToArray();
            int ret = await Store.InsertAsync(dbmModels, cancellationToken);
            Map(dbmModels, dtoModels);
            return ret;
        }

        public virtual async Task<int> UpdateAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DbmModel dbmModel = Map(dtoModel);
            int ret = await Store.UpdateAsync(dbmModel, cancellationToken);
            Map(dbmModel, dtoModel);

            return ret;
        }

        public virtual async Task<int> DeleteAsync(DtoModel dtoModel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DbmModel dbmModel = Map(dtoModel);
            return await Store.DeleteAsync(dbmModel, cancellationToken);
        }

        public virtual async Task<int> DeleteAsync(IEnumerable<DtoModel> dtoModels, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<DtoModel, DbmModel> models = MapToDictionary(dtoModels);
            DbmModel[] dbmModels = models.Select(m => m.Value).ToArray();
            return await Store.DeleteAsync(dbmModels, cancellationToken);
        }

        protected virtual Dto Map<Dbm, Dto>(Dbm dbmModel)
            where Dbm : class
            where Dto : class
        {
            return AutoMapper.DefaultMapper.Map<Dbm, Dto>(dbmModel);
        }

        protected virtual void Map<Dbm, Dto>(Dbm dbmModel, Dto dtoModel)
            where Dbm : class
            where Dto : class
        {
            AutoMapper.InsertUpdateMapper.Map(dbmModel, dtoModel);
        }

        protected virtual void Map<Dbm, Dto>(IEnumerable<Dbm> dbmModel, IEnumerable<Dto> dtoModel)
            where Dbm : class
            where Dto : class
        {
            AutoMapper.InsertUpdateMapper.Map(dbmModel, dtoModel);
        }

        protected virtual DtoModel Map(DbmModel dbmModel) => AutoMapper.DefaultMapper.Map<DbmModel, DtoModel>(dbmModel);

        protected virtual DtoModel[] Map(IEnumerable<DbmModel> dbmModels) => AutoMapper.DefaultMapper.Map<DbmModel[], DtoModel[]>(dbmModels.ToArray());

        protected virtual DbmModel Map(DtoModel dtoModel) => AutoMapper.DefaultMapper.Map<DtoModel, DbmModel>(dtoModel);

        protected virtual DbmModel[] Map(IEnumerable<DtoModel> dtoModel) => AutoMapper.DefaultMapper.Map<DtoModel[], DbmModel[]>(dtoModel.ToArray());

        protected virtual Dictionary<DtoModel, DbmModel> MapToDictionary(IEnumerable<DtoModel> dtoModels)
        {
            Dictionary<DtoModel, DbmModel> models = new();
            foreach (DtoModel dtoModel in dtoModels)
            {
                DbmModel dbmModel = Map(dtoModel);
                models.Add(dtoModel, dbmModel);
            }

            return models;
        }
    }
}
