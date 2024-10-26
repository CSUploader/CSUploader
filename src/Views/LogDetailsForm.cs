// <copyright file="LogDetailsForm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Controls.Models;

namespace CSUploader.Views
{
    public partial class LogDetailsForm : Form
    {
        public LogDetailsForm(LogListViewModel listViewLogItem)
        {
            InitializeComponent();

            ListViewLogItem = listViewLogItem;
        }

        private LogListViewModel ListViewLogItem { get; set; }

        private void LogDetailsForm_Load(object sender, EventArgs e)
        {
            textBoxDateTime.Text = ListViewLogItem.DateTime.ToString("yyyy/MM/dd HH:mm:ss");
            textBoxFilename.Text = ListViewLogItem.Filename;
            textBoxFunction.Text = ListViewLogItem.Function;
            textBoxLineNumber.Text = ListViewLogItem.LineNumber.ToString();
            textBoxThreadId.Text = ListViewLogItem.ThreadId.ToString();
            textBoxMessage.Text = ListViewLogItem.Message;
            try
            {
                webBrowserMessage.DocumentText = ListViewLogItem.Message;
            }
            catch (Exception ex)
            {
                webBrowserMessage.DocumentText = $"Error loading text as HTML: {ex}";
            }

            ActiveControl = tabControlMessage;
        }

        private void buttonClose_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
