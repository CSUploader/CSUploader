// <copyright file="UploadsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadsViewModel : ObservableObject, IDisposable
{
    private readonly PackageManager _packageManager;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public UploadsViewModel(PackageManager packageManager)
    {
        _packageManager = packageManager;
        _packageManager.PackageAdded += PackageManager_PackageAdded;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AppSettings.DefaultUploadsTabPageRefreshTimer),
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    public ObservableCollection<Package> Packages { get; } = [];

    [RelayCommand]
    private void Start()
    {
        _packageManager.StartPackages();
    }

    [RelayCommand]
    private void Pause()
    {
        _packageManager.PausePackages(resume: _packageManager.IsPaused);
    }

    [RelayCommand]
    private void Stop()
    {
        _packageManager.StopPackages();
    }

    [RelayCommand]
    private void Retry(PackageDetails? packageDetails)
    {
        if (packageDetails is not null)
        {
            _packageManager.StartPackage(packageDetails);
        }
    }

    [RelayCommand]
#pragma warning disable CA1822 // Must be instance method for RelayCommand
    private void StopSelected(PackageDetails? packageDetails)
#pragma warning restore CA1822
    {
        if (packageDetails is not null)
        {
            PackageManager.StopPackage(packageDetails);
        }
    }

    [RelayCommand]
    private void Remove(PackageDetails? packageDetails)
    {
        if (packageDetails is not null)
        {
            _packageManager.RemovePackage(packageDetails);

            if (packageDetails is Package package)
            {
                Packages.Remove(package);
            }
        }
    }

    private void PackageManager_PackageAdded(object? sender, PackageAddedEventArgs e)
    {
        if (e.ChildPackages is null)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (PackageDetails packageDetails in e.ChildPackages)
            {
                if (packageDetails is Package package && !Packages.Contains(package))
                {
                    Packages.Add(package);
                }
            }
        });
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Force the UI to refresh bound package properties.
        // Packages expose plain properties (Speed, Progress, etc.) that don't raise
        // PropertyChanged, so we nudge the collection to trigger re-evaluation.
        OnPropertyChanged(nameof(Packages));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _refreshTimer.Stop();
            _packageManager.PackageAdded -= PackageManager_PackageAdded;
        }

        _disposed = true;
    }
}
