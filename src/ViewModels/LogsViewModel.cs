// <copyright file="LogsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Lib;

namespace CSUploader.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool autoScroll = true;

    public ObservableCollection<LogEntryViewModel> StatusLogs { get; } = [];

    public ObservableCollection<LogEntryViewModel> HttpLogs { get; } = [];

    public ObservableCollection<LogEntryViewModel> ErrorLogs { get; } = [];

    public ObservableCollection<LogEntryViewModel> UILogs { get; } = [];

    public void AddLogEntry(LogEvent logEvent)
    {
        LogEntryViewModel entry = new(logEvent);

        switch (logEvent.LogType)
        {
            case LogType.Status:
                StatusLogs.Add(entry);
                break;
            case LogType.Http:
                HttpLogs.Add(entry);
                break;
            case LogType.Error:
                ErrorLogs.Add(entry);
                break;
            case LogType.UI:
                UILogs.Add(entry);
                break;
        }
    }

    [RelayCommand]
    private void ClearStatusLogs() => StatusLogs.Clear();

    [RelayCommand]
    private void ClearHttpLogs() => HttpLogs.Clear();

    [RelayCommand]
    private void ClearErrorLogs() => ErrorLogs.Clear();

    [RelayCommand]
    private void ClearUILogs() => UILogs.Clear();
}
