// <copyright file="FileHosterListViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;

namespace CSUploader.Controls.Models
{
    public class FileHosterListViewModel
    {
        public bool Use { get; set; }

        public FileHosterClient? FileHoster { get; set; }

        public FileHosterLoginDto[] Accounts { get; set; } = Array.Empty<FileHosterLoginDto>();

        public FileHosterLoginDto? SelectedAccount { get; set; }
    }
}
