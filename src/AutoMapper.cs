// <copyright file="AutoMapper.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using AutoMapper;

namespace CSUploader
{
    public class AutoMapper
    {
        private static readonly Lazy<AutoMapper> Lazy = new(() => new AutoMapper());

        public static IMapper Mapper => Lazy.Value.mapper;

        private readonly IMapper mapper;

        public AutoMapper()
        {
            MapperConfiguration config = new(cfg =>
            {
            });

            mapper = new Mapper(config);
        }
    }
}
