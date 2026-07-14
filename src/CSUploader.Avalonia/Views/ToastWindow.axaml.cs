// <copyright file="ToastWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// Avalonia bottom-right completion-toast window (port of the WPF ToastWindow, rule 42). Auto-dismisses
/// after 5s; hovering pauses the timer; a body click runs ActivateCommand; the close button runs CloseCommand.
/// Positioned by AvaloniaToastHost via Window.Position (physical px).
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);
    private readonly DispatcherTimer? _dismissTimer;
    private readonly ToastViewModel? _viewModel;

    // Loader/design-time ctor (AVLN3001); the app always uses the VM overload. DataContext stays null here.
    public ToastWindow()
    {
        InitializeComponent();
    }

    public ToastWindow(ToastViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoDismissDelay };
        _dismissTimer.Tick += OnDismissTick;

        // WPF used Loaded -> Start; Avalonia's Window lifecycle event is Opened.
        Opened += (_, _) => _dismissTimer.Start();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    // Test seam (InternalsVisibleTo -> CSUploader.Avalonia.Tests): the auto-dismiss timer's armed state, so
    // a headless test can assert it arms on Opened, pauses on hover and stops on Closed WITHOUT a 5s
    // wall-clock wait (headless does not advance DispatcherTimer virtual time). Same pattern as
    // AvaloniaDialogService / MessageBoxWindow expose for their headless tests.
    internal bool IsAutoDismissRunning => _dismissTimer?.IsEnabled == true;

    private void OnDismissTick(object? sender, EventArgs e)
    {
        _dismissTimer!.Stop();
        Close();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => PauseAutoDismiss();

    private void OnPointerExited(object? sender, PointerEventArgs e) => RestartAutoDismiss();

    // Pointer-enter pauses the countdown; pointer-exit restarts it from zero (WPF ToastWindow parity). Split
    // into internal parameterless seams so the headless test drives them without synthesizing a
    // PointerEventArgs (which the desktop input stack, not the test, normally produces).
    internal void PauseAutoDismiss() => _dismissTimer?.Stop();

    internal void RestartAutoDismiss()
    {
        _dismissTimer?.Stop();
        _dismissTimer?.Start();
    }

    private void OnBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        // Left-button guard (rule 10). The close button handles its own press, so a close click does NOT
        // bubble to this window handler (Avalonia stops bubbling at a handled event); OnCloseClicked's
        // e.Handled is belt-and-braces parity with the WPF stop-propagation.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && _viewModel?.ActivateCommand.CanExecute(null) == true)
        {
            _viewModel.ActivateCommand.Execute(null);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
