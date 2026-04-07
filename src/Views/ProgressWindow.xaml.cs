// <copyright file="ProgressWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;

namespace CSUploader.Views;

public partial class ProgressWindow : Window
{
    private CancellationTokenSource? _cancellationTokenSource;

    public ProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Executes an asynchronous operation while displaying a modal progress window.
    /// Uses a proper TaskCompletionSource pattern: the async work starts on the Loaded event,
    /// ShowDialog() blocks until Close() is called, and the result is returned after the dialog closes.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="owner">The owner window.</param>
    /// <param name="labelText">The label text to display.</param>
    /// <param name="allowCancel">Whether to show the cancel button.</param>
    /// <param name="func">The async operation to execute.</param>
    /// <returns>The result of the async operation, or default if cancelled.</returns>
    public static async Task<T?> ExecuteAsync<T>(Window owner, string labelText, bool allowCancel, Func<CancellationToken, Task<T?>> func)
    {
        ProgressWindow progressWindow = new()
        {
            Owner = owner,
            Title = "Please wait...",
        };

        progressWindow.LabelText.Text = labelText + Environment.NewLine + "Please wait...";

        if (!allowCancel)
        {
            progressWindow.CancelButton.Visibility = Visibility.Collapsed;
        }

        CancellationTokenSource cts = new();
        progressWindow._cancellationTokenSource = cts;

        T? result = default;
        Exception? capturedException = null;

        progressWindow.Loaded += async (_, _) =>
        {
            try
            {
                result = await func(allowCancel ? cts.Token : CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected, not an error.
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
            finally
            {
                progressWindow.Close();
            }
        };

        progressWindow.ShowDialog();
        cts.Dispose();

        if (capturedException is not null)
        {
            MessageBox.Show(capturedException.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return result;
    }

    /// <summary>
    /// Executes an asynchronous operation (no return value) while displaying a modal progress window.
    /// </summary>
    /// <param name="owner">The owner window.</param>
    /// <param name="labelText">The label text to display.</param>
    /// <param name="allowCancel">Whether to show the cancel button.</param>
    /// <param name="func">The async operation to execute.</param>
    public static async Task ExecuteAsync(Window owner, string labelText, bool allowCancel, Func<CancellationToken, Task> func)
    {
        await ExecuteAsync<bool>(owner, labelText, allowCancel, async ct =>
        {
            await func(ct);
            return true;
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
    }
}
