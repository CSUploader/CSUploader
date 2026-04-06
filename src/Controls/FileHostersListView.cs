// <copyright file="FileHostersListView.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using BrightIdeasSoftware;
using CSUploader.Controls.Models;

namespace CSUploader.Controls;

public partial class FileHostersListView : UserControl
{
    /// <summary>
    /// String for anonymous upload.
    /// </summary>
    private static readonly string NoAccount = "(no account)";

    public FileHostersListView()
    {
        InitializeComponent();

        InitializeListView();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ImageList FileHostersImageList { get; set; } = new();

    public void SetHeaderFormatStyle(HeaderFormatStyle headerFormatStyle)
    {
        olvFileHosters.HeaderFormatStyle = headerFormatStyle;
    }

    public void SetItems(IEnumerable<FileHosterListViewModel> items)
    {
        olvFileHosters.SetObjects(items);
    }

    public FileHosterListViewModel[] GetSelectedItems()
    {
        return olvFileHosters.CheckedObjects.Cast<FileHosterListViewModel>().ToArray();
    }

    private void InitializeListView()
    {
        olvFileHosters.CheckedAspectName = nameof(FileHosterListViewModel.Use);

        // Columns
        olvFileHoster.ImageGetter = (object rowObject) =>
        {
            if (rowObject is not FileHosterListViewModel m)
            {
                return string.Empty;
            }

            return FileHostersImageList.Images.Keys.Cast<string>().Where(k => k.StartsWith($"filehoster_{m.FileHoster?.Name}.", StringComparison.OrdinalIgnoreCase)).Select(k => FileHostersImageList.Images[k]).FirstOrDefault();
        };
        olvFileHoster.AspectGetter = (object model) => model is FileHosterListViewModel m ? m.FileHoster?.Name : string.Empty;
        olvFileHosterLogin.AspectGetter = (object model) =>
        {
            FileHosterListViewModel m = (FileHosterListViewModel)model;
            return m.Accounts.Length != 0 && m.SelectedAccount != null ? m.Accounts.FirstOrDefault(a => ReferenceEquals(a, m.SelectedAccount))?.Username : NoAccount;
        };

        // Events
        olvFileHosters.CellEditStarting += OlvFileHosters_CellEditStarting;
        olvFileHosters.CellEditFinishing += OlvFileHosters_CellEditFinishing;
    }

    private void OlvFileHosters_CellEditStarting(object? sender, CellEditEventArgs e)
    {
        if (e.Column == olvFileHosterLogin)
        {
            ComboBox comboBox = new()
            {
                Font = olvFileHosters.Font,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            FileHosterListViewModel m = (FileHosterListViewModel)e.RowObject;
            comboBox.SetBounds(e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, e.CellBounds.Height);
            comboBox.Items.Add(NoAccount);
            comboBox.SelectedIndex = 0;
            if (m.Accounts.Length != 0)
            {
                comboBox.Items.AddRange(m.Accounts);
                comboBox.SelectedIndex = comboBox.Items.IndexOf(e.Column.GetValue(e.RowObject));
            }

            e.Control = comboBox;
        }
    }

    private void OlvFileHosters_CellEditFinishing(object? sender, CellEditEventArgs e)
    {
        if (e.Column == olvFileHosterLogin && e.Control is ComboBox c)
        {
            e.NewValue = c.Text;
            e.Column.PutValue(e.RowObject, c.Text);

            FileHosterListViewModel model = (FileHosterListViewModel)e.RowObject;
            model.SelectedAccount = c.Items.IndexOf(e.NewValue) > 0 ? model.Accounts[c.Items.IndexOf(e.NewValue)] : null;
        }
    }
}
