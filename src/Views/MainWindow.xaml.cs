// <copyright file="MainWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;
using System.Windows;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        InitializeComponent();

        DataContext = _serviceProvider.GetRequiredService<MainViewModel>();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuPreferences_Click(object sender, RoutedEventArgs e)
    {
        // Switch to the Settings tab
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedTabIndex = 3;
        }
    }

    private void MenuClearHistory_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            "Are you sure you want to clear the upload history?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            // TODO: Implement clear history via repository
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        MessageBox.Show(
            $"CSUploader v{version}\n\nA file upload manager for multiple hosting services.\n\nBuilt with .NET 10 and WPF.",
            "About CSUploader",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
