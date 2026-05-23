// <copyright file="FileHosterSelectionViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib.Localization;

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

    public FileHosterLoginDto[] Accounts { get; private set; }

    public bool HasAccounts => Accounts.Length > 0;

    public string AccountDisplayText => HasAccounts
        ? (SelectedAccount?.Username ?? Localizer.Instance["Wizard_Step2_AccountSelect"])
        : Localizer.Instance["Wizard_Step2_AccountAnonymous"];

    /// <summary>
    /// Replaces the available accounts (e.g. after the user adds one through the
    /// wizard's "Add account…" link). Preserves the current selection if it survives,
    /// otherwise selects the first account, and fires change notifications so the
    /// row's account picker, "Use" gate, and display text refresh in place.
    /// </summary>
    public void SetAccounts(FileHosterLoginDto[] accounts)
    {
        Accounts = accounts;
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(HasAccounts));

        int? previousId = SelectedAccount?.Id;
        FileHosterLoginDto? next = previousId is int id
            ? Array.Find(accounts, a => a.Id == id)
            : null;

        SelectedAccount = next ?? (accounts.Length > 0 ? accounts[0] : null);

        if (!HasAccounts && Use)
        {
            Use = false;
        }

        OnPropertyChanged(nameof(AccountDisplayText));
    }

    partial void OnUseChanged(bool value)
    {
        // Defense in depth: the wizard XAML disables the Use checkbox when there are no
        // accounts, but a binding glitch or a rehydrated sticky selection should not be
        // able to put the row into the "checked but anonymous" state — that flow falls
        // back to a blank login DTO at upload time and the upload is guaranteed to fail.
        if (value && !HasAccounts)
        {
            Use = false;
        }
    }

    partial void OnSelectedAccountChanged(FileHosterLoginDto? value)
    {
        OnPropertyChanged(nameof(AccountDisplayText));
    }
}
