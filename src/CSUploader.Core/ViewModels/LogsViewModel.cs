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
    public partial bool AutoScroll { get; set; } = true;

    /// <summary>
    /// Exposed to the view's code-behind so the column-toggle menu can persist visibility
    /// via the head-side <c>DataGridColumnVisibilityPersistence</c> helper. Optional in tests.
    /// </summary>
    internal Dal.SettingRepository? SettingRepo { get; }

    /// <summary>
    /// Exposed to the view's code-behind so the "Reset columns" entry can prompt via
    /// the standard opt-out confirmation flow.
    /// </summary>
    internal Services.IDialogService DialogServiceForView { get; }

    public LogsViewModel(Services.IDialogService dialogService, Dal.SettingRepository? settingRepo = null)
    {
        DialogServiceForView = dialogService;
        SettingRepo = settingRepo;
    }

    /// <summary>
    /// Retention cap per tab. The collections were unbounded, and a long session with many concurrent
    /// uploads accumulates hundreds of HTTP rows per minute — each retaining its full
    /// <see cref="Lib.Net.Http.HttpTransaction"/> (headers + body) — so an hours-long run grew to
    /// 100k+ rows and hundreds of MB. The Logs tab is a live-diagnostics view, not an archive: keep the
    /// most recent entries and drop the oldest. Internal so tests can size their fixtures to it.
    /// </summary>
    internal const int MaxEntriesPerTab = 5000;

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
                AddCapped(StatusLogs, entry);
                break;
            case LogType.Http:
                AddCapped(HttpLogs, entry);
                break;
            case LogType.Error:
                AddCapped(ErrorLogs, entry);
                break;
            case LogType.UI:
                AddCapped(UILogs, entry);
                break;
        }
    }

    /// <summary>Appends and, past the cap, drops the oldest entry. At steady state that is one
    /// <c>RemoveAt(0)</c> per append — a bounded reference shift, and the grids handle the index-0
    /// removal like any other collection change (auto-scroll stays pinned to the end).</summary>
    private static void AddCapped(ObservableCollection<LogEntryViewModel> logs, LogEntryViewModel entry)
    {
        logs.Add(entry);
        if (logs.Count > MaxEntriesPerTab)
        {
            logs.RemoveAt(0);
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
