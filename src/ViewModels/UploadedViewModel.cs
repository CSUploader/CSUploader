// <copyright file="UploadedViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;

namespace CSUploader.ViewModels;

public partial class UploadedViewModel : ObservableObject
{
    private readonly UploadPackageManager _uploadPackageManager;

    public UploadedViewModel(UploadPackageManager uploadPackageManager)
    {
        _uploadPackageManager = uploadPackageManager;
    }

    public ObservableCollection<UploadPackageDto> Packages { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        UploadPackageDto[] packages = await _uploadPackageManager.GetAllAsync(cancellationToken);

        Packages.Clear();

        foreach (UploadPackageDto package in packages)
        {
            Packages.Add(package);
        }
    }
}
