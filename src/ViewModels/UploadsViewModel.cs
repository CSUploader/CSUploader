// <copyright file="UploadsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Lib;
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

    // ── Summary properties for status bar ──

    public int PackageCount => Packages.Count;

    public int FileCount => Packages.Sum(p => p.Count());

    public string TotalBytes => ByteUnit.FromBytes(
        Packages.Sum(p => p.Size ?? 0), ByteBase.Binary).ToFriendlyString();

    public string BytesLoaded => ByteUnit.FromBytes(
        Packages.Sum(p => p.BytesLoaded ?? 0), ByteBase.Binary).ToFriendlyString();

    public string RemainingBytes => ByteUnit.FromBytes(
        Packages.Sum(p => p.BytesRemaining ?? 0), ByteBase.Binary).ToFriendlyString();

    public string UploadSpeed
    {
        get
        {
            long speed = Packages.Sum(p => p.Speed ?? 0);
            return speed > 0
                ? ByteUnit.FromBytes(speed, ByteBase.Binary).ToFriendlyString() + "/s"
                : "0 B/s";
        }
    }

    public int RunningUploads => Packages.Sum(p =>
        p.Count(pf => pf.Status?.Status == JobStatus.Running));

    public string Eta
    {
        get
        {
            long remaining = Packages.Sum(p => p.BytesRemaining ?? 0);
            long speed = Packages.Sum(p => p.Speed ?? 0);
            if (speed <= 0 || remaining <= 0)
            {
                return "~";
            }

            TimeSpan eta = TimeSpan.FromSeconds(remaining / (double)speed);
            return eta.Hours > 0
                ? eta.ToString(@"h\h\:mm\m\:ss\s", CultureInfo.InvariantCulture)
                : eta.Minutes > 0
                    ? eta.ToString(@"mm\m\:ss\s", CultureInfo.InvariantCulture)
                    : eta.ToString(@"ss\s", CultureInfo.InvariantCulture);
        }
    }

    // ── Commands ──

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
        // Force UI to re-read all properties (Package/PackageFile don't raise PropertyChanged)
        OnPropertyChanged(nameof(Packages));

        // Refresh summary stats
        OnPropertyChanged(nameof(PackageCount));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(BytesLoaded));
        OnPropertyChanged(nameof(RemainingBytes));
        OnPropertyChanged(nameof(UploadSpeed));
        OnPropertyChanged(nameof(RunningUploads));
        OnPropertyChanged(nameof(Eta));
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
