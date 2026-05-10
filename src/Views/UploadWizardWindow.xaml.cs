// <copyright file="UploadWizardWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Windows;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

public partial class UploadWizardWindow : Window
{
    private readonly UploadWizardViewModel _vm;

    public UploadWizardWindow(UploadsViewModel uploadsVm)
    {
        InitializeComponent();

        IServiceProvider sp = ((App)Application.Current).Services;
        _vm = new UploadWizardViewModel(
            sp.GetRequiredService<PackageManager>(),
            sp.GetRequiredService<FileHosterLoginRepository>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IAppLogger>(),
            sp.GetRequiredService<AppSettings>());

        _vm.PropertyChanged += Vm_PropertyChanged;
        DataContext = _vm;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UploadWizardViewModel.Completed) && _vm.Completed)
        {
            DialogResult = true;
        }
    }
}
