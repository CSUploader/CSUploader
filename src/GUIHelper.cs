// <copyright file="GUIHelper.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using CSUploader.Lib;

namespace CSUploader
{
    public static class GUIHelper
    {
        public static void SetItems<T>(ComboBox cb, IDictionary<T, string> items, T? selected = null)
            where T : struct
        {
            cb.Items.Clear();
            int selectedIndex = 0;

            foreach (KeyValuePair<T, string> kvp in items)
            {
                cb.Items.Add(kvp.Value);
                if (selected.HasValue && kvp.Key.Equals(selected.Value))
                {
                    selectedIndex = cb.Items.Count - 1;
                }
            }

            if (selected.HasValue)
            {
                cb.SelectedIndex = selectedIndex;
            }
        }

        public static bool ValidateFilePath(TextBox tb, string errorMessage)
        {
            string filePath = tb.Text;
            if (!File.Exists(filePath))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseDirectoryPath(TextBox tb, out string directoryPath, string errorMessage)
        {
            directoryPath = tb.Text;
            if (!Directory.Exists(directoryPath))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseRegularExpression(TextBox tb, [NotNullWhen(true)] out Regex? regex, string errorMessage)
        {
            try
            {
                regex = new Regex(tb.Text);
            }
            catch
            {
                regex = null;
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseIPAddress(TextBox tb, [NotNullWhen(true)] out IPAddress? val, string errorMessage)
        {
            val = new IPAddress(0);
            if (!ParseString(tb, out string str, errorMessage))
            {
                return false;
            }

            if (!IPAddress.TryParse(str, out val))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseString(TextBox tb, out string val, string errorMessage)
        {
            val = tb.Text;
            if (string.IsNullOrEmpty(val))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseUInt16(TextBox tb, out ushort val, string errorMessage)
        {
            if (!ushort.TryParse(tb.Text, out val))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseInt32(TextBox tb, out int val, string errorMessage)
        {
            if (!int.TryParse(tb.Text, out val))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseInt32(ComboBox cb, out int val, string errorMessage)
        {
            if (!int.TryParse(cb.Text, out val))
            {
                Error(cb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseDouble(TextBox tb, out double val, string errorMessage)
        {
            if (!double.TryParse(tb.Text, out val))
            {
                Error(tb, errorMessage);
                return false;
            }

            return true;
        }

        public static bool ParseSize(TextBox tb, out long value, string errorMessage)
        {
            value = 0;

            string str = tb.Text.Trim();

            if (!ByteUnit.TryParseSize(str, out ByteUnit? byteUnit))
            {
                Error(tb, errorMessage);
                return false;
            }

            value = (long)byteUnit.Bytes;

            return true;
        }

        public static bool ParseSize(ComboBox cb, out long value, string errorMessage)
        {
            value = 0;

            string str = cb.Text.Trim();

            if (!ByteUnit.TryParseSize(str, out ByteUnit? byteUnit))
            {
                Error(cb, errorMessage);
                return false;
            }

            value = (long)byteUnit.Bytes;

            return true;
        }

        public static void Error(Control? ctrl, string errorMessage)
        {
            if (ctrl != null)
            {
                SetFocus(ctrl);
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                MessageBox.Show(errorMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void SetFocus(Control ctrl)
        {
            if (ctrl == null)
            {
                return;
            }

            Stack<Control> parents = new();
            Control parent = ctrl;
            do
            {
                parents.Push(parent);
            } while ((parent = parent.Parent) != null);

            TabControl? parentTabControl = null;
            do
            {
                Control parentCtrl = parents.Pop();
                if (parentCtrl == null)
                {
                    continue;
                }
                else if (parentCtrl is TabControl tc)
                {
                    parentTabControl = tc;
                }
                else if (parentTabControl != null && parentCtrl is TabPage tp)
                {
                    parentTabControl.SelectedTab = tp;
                    parentTabControl = null;
                }

                parentCtrl.Focus();
            }
            while (parents.Any());
        }

        public static void UpdateListView(ListView listView, ListViewItem listViewItem)
        {
            listView.BeginUpdate();
            listView.Items.Add(listViewItem);
            listView.EndUpdate();
        }

        public static void UpdateListView(ListView listView, IEnumerable<ListViewItem> listViewItems)
        {
            listView.BeginUpdate();
            listView.Items.AddRange(listViewItems.ToArray());
            listView.EndUpdate();
        }

        public static void BrowseOpenFile(TextBox tb, string title, string filter)
        {
            OpenFileDialog openFileDialog = new()
            {
                Title = title,
                Filter = filter,
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                tb.Text = openFileDialog.FileName;
            }
        }

        public static void BrowseSaveFile(TextBox tb, string title, string filter)
        {
            SaveFileDialog saveFileDialog = new()
            {
                Title = title,
                Filter = filter
            };

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                tb.Text = saveFileDialog.FileName;
            }
        }

        public static bool ConfirmDialog(string title, string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            DialogResult result = MessageBox.Show(text, title, buttons, icon);
            return result == DialogResult.OK || result == DialogResult.Yes;
        }
    }
}
