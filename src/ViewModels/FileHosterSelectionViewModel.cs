// <copyright file="FileHosterSelectionViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;

namespace CSUploader.ViewModels;

public partial class FileHosterSelectionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool use;

    [ObservableProperty]
    private FileHosterLoginDto? selectedAccount;

    public FileHosterSelectionViewModel(string fileHosterName, FileHosterLoginDto[] accounts)
    {
        FileHosterName = fileHosterName;
        Accounts = accounts;

        if (accounts.Length > 0)
        {
            SelectedAccount = accounts[0];
        }
    }

    public string FileHosterName { get; }

    public FileHosterLoginDto[] Accounts { get; }

    public bool HasAccounts => Accounts.Length > 0;

    public string AccountDisplayText => HasAccounts
        ? (SelectedAccount?.Username ?? "(select account)")
        : "(anonymous)";
}
