// <copyright file="AutoMapper.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using AutoMapper;

namespace CSUploader.Dal
{
    public class AutoMapper
    {
        private static readonly Lazy<AutoMapper> Lazy = new(() => new AutoMapper());

        public static IMapper DefaultMapper => Lazy.Value.defaultMapper;

        public static IMapper InsertUpdateMapper => Lazy.Value.insertUpdateMapper;

        private readonly IMapper defaultMapper;

        private readonly IMapper insertUpdateMapper;

        private AutoMapper()
        {
            MapperConfiguration defaultConfig = new(cfg =>
            {
                cfg.CreateMap<FileHosterLoginDbm, FileHosterLoginDto>()
                    .ReverseMap();

                cfg.CreateMap<SettingDbm, SettingDto>()
                    .ReverseMap();

                cfg.CreateMap<UploadPackageDbm, UploadPackageDto>()
                    .ReverseMap();

                cfg.CreateMap<UploadPackageFileDbm, UploadPackageFileDto>()
                    .ReverseMap();
            });
            defaultMapper = new Mapper(defaultConfig);

            MapperConfiguration insertUpdateConfig = new(cfg =>
            {
                cfg.CreateMap<FileHosterLoginDbm, FileHosterLoginDto>()
                    .ReverseMap();

                cfg.CreateMap<SettingDbm, SettingDto>()
                    .ReverseMap();

                cfg.CreateMap<UploadPackageDbm, UploadPackageDto>()
                    .ReverseMap();

                cfg.CreateMap<UploadPackageFileDbm, UploadPackageFileDto>()
                    .ReverseMap();
            });
            insertUpdateMapper = new Mapper(insertUpdateConfig);
        }
    }
}
