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
    /// Executes an asynchronous operation while displaying a progress window.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="owner">The owner window.</param>
    /// <param name="label">The label text to display.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="cancellable">Whether to show the cancel button.</param>
    /// <returns>The result of the async operation.</returns>
    public static async Task<T> ExecuteAsync<T>(Window owner, string label, Func<CancellationToken, Task<T>> operation, bool cancellable = false)
    {
        ProgressWindow progressWindow = new()
        {
            Owner = owner,
        };

        progressWindow.LabelText.Text = label;

        if (cancellable)
        {
            progressWindow._cancellationTokenSource = new CancellationTokenSource();
            progressWindow.CancelButton.Visibility = Visibility.Visible;
        }

        CancellationToken token = progressWindow._cancellationTokenSource?.Token ?? CancellationToken.None;

        T result = default!;
        Exception? taskException = null;

        progressWindow.Loaded += async (_, _) =>
        {
            try
            {
                result = await operation(token);
            }
            catch (Exception ex)
            {
                taskException = ex;
            }
            finally
            {
                progressWindow.Close();
            }
        };

        progressWindow.ShowDialog();

        progressWindow._cancellationTokenSource?.Dispose();

        if (taskException != null)
        {
            throw taskException;
        }

        return result;
    }

    /// <summary>
    /// Executes an asynchronous operation (no return value) while displaying a progress window.
    /// </summary>
    /// <param name="owner">The owner window.</param>
    /// <param name="label">The label text to display.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="cancellable">Whether to show the cancel button.</param>
    public static async Task ExecuteAsync(Window owner, string label, Func<CancellationToken, Task> operation, bool cancellable = false)
    {
        await ExecuteAsync<object?>(owner, label, async ct =>
        {
            await operation(ct);
            return null;
        }, cancellable);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Cancelling...";
    }
}
