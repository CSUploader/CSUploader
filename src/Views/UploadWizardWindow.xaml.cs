// <copyright file="UploadWizardWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Windows;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

public partial class UploadWizardWindow : Window
{
    private readonly UploadWizardViewModel _vm;

    public UploadWizardWindow(UploadsViewModel uploadsVm)
    {
        InitializeComponent();

        // Phase 9 ledger fix (d): resolve the DI-registered (Transient) VM instead of hand-building its
        // seven-arg ctor. The uploadsVm parameter stays for call-site parity but is unused (as before).
        _vm = ((App)Application.Current).Services.GetRequiredService<UploadWizardViewModel>();

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
