// <copyright file="LogDetailsWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// Read-only modal that shows one log row's metadata (time, thread, file, function, line) plus its full
/// message on Text/Html tabs — port of the WPF <c>LogDetailsWindow</c>. DataContext is a Core
/// <see cref="LogEntryViewModel"/>, bound OneWay.
/// </summary>
/// <remarks>
/// No <see cref="IDialogService"/> member: Phase 5's LogsView (Enter / double-click a row) is the production
/// opener; until then the dev gallery button is the only path that constructs it. The field styles are the
/// port-rule-12 exemplar (prep item 4): the WPF window-local keyed <c>FieldLabel</c>/<c>FieldValue</c> styles
/// became <c>&lt;Window.Styles&gt;</c> class selectors with <c>BasedOn</c> dropped.
/// </remarks>
public partial class LogDetailsWindow : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses the
    // LogEntryViewModel overload. No DataContext set here — the OneWay bindings resolve against null harmlessly.
    public LogDetailsWindow()
    {
        InitializeComponent();
    }

    public LogDetailsWindow(LogEntryViewModel logEntry)
    {
        InitializeComponent();
        DataContext = logEntry;
    }

    // Close on click. WPF's IsCancel auto-closed the window; Avalonia's IsCancel only routes Esc to Click
    // without closing (port rule 7), so both the click and Esc land here.
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
