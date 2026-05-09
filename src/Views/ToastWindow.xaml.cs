// <copyright file="ToastWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class ToastWindow : Window
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _dismissTimer;
    private readonly ToastViewModel _viewModel;

    public ToastWindow(ToastViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = AutoDismissDelay,
        };
        _dismissTimer.Tick += OnDismissTick;

        Loaded += (_, _) => _dismissTimer.Start();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void OnDismissTick(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        Close();
    }

    private void OnMouseEntered(object sender, MouseEventArgs e) => _dismissTimer.Stop();

    private void OnMouseLeft(object sender, MouseEventArgs e)
    {
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void OnBodyClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ActivateCommand.CanExecute(null))
        {
            _viewModel.ActivateCommand.Execute(null);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        // Stop click propagation so OnBodyClicked doesn't also activate the window.
        e.Handled = true;
        Close();
    }
}
