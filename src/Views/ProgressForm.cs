// <copyright file="ProgressForm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using CSUploader.Lib;
using CSUploader.Lib.Extensions;

namespace CSUploader.Views;

public partial class ProgressForm : Form
{
    private CancellationTokenSource cancellationTokenSource = new();

    public ProgressForm(IWin32Window ownerWindow, string labelText, bool showCancelButton)
    {
        InitializeComponent();

        TextLabel.Text = $"{labelText}" + Environment.NewLine + $"Please wait...";

        ProgressBar.Step = 10;
        ProgressBar.Minimum = 0;
        ProgressBar.Maximum = 100;

        timer1.Interval = 200;
        timer1.Tick += Timer1_Tick;

        if (!showCancelButton)
        {
            CancelActionButton.Visible = false;
            Size = new Size(Size.Width, Size.Height - CancelActionButton.Size.Height);
        }

        Show(ownerWindow);
    }

    public static async Task ExecuteAsync(IWin32Window ownerWindow, string labelText, bool allowCancel, Func<ProgressForm, CancellationToken, Task> func)
    {
        await ExecuteAsync(ownerWindow, labelText, allowCancel, async (form, cancellationToken) =>
        {
            await func(form, cancellationToken);
            return true;
        });
    }

    public static async Task<T?> ExecuteAsync<T>(IWin32Window ownerWindow, string labelText, bool allowCancel, Func<ProgressForm, CancellationToken, Task<T?>> func)
    {
        ProgressForm progressForm = new(ownerWindow, labelText, allowCancel);
        CancellationToken cancellationToken = progressForm.cancellationTokenSource.Token;
        T? result = default;
        try
        {
            // Somehow calling func() will lag the progressform painting,
            // so we add a little delay at the beginning to fully paint the whole progressform
            await Task.Delay(20);

            if (allowCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Stopwatch sw = Stopwatch.StartNew();
            result = await func(progressForm, allowCancel ? cancellationToken : default);
            sw.Stop();
        }
        catch (Exception ex)
        {
            Logger.Current.Log(null, LogType.Error, $"Exception occurred: {ex.Message}" + Environment.NewLine + ex.ToString());
            if ((ex is OperationCanceledException) || ex.InnerException != null)
            {
                if (ex.InnerException is OperationCanceledException)
                {
                    throw ex.InnerException;
                }
                else if (ex.InnerException != null && ex.InnerException.Message.Contains("Operation cancelled by user", StringComparison.Ordinal))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            // Only log non-cancellation exceptions
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            // Rethrow so we don't lose the stack trace
            throw;
        }
        finally
        {
            progressForm.timer1.Stop();
            progressForm.Close();
        }

        return result;
    }

    private void ProgressForm_Load(object? sender, EventArgs e)
    {
        timer1.Start();

        this.AlwaysOnTop(true);
    }

    private void Timer1_Tick(object? sender, EventArgs e)
    {
        ProgressBar.PerformStep();
        if (ProgressBar.Value >= ProgressBar.Maximum)
        {
            ProgressBar.Value = 0;
        }
    }

    private void CancelActionButton_Click(object? sender, EventArgs e)
    {
        cancellationTokenSource.Cancel();
    }
}
