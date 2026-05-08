// <copyright file="FileHosterSelectionViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

public class FileHosterSelectionViewModelTests
{
    [Fact]
    public void Ctor_NoAccounts_ReportsAnonymousState()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        Assert.False(vm.HasAccounts);
        Assert.Null(vm.SelectedAccount);
        Assert.False(vm.Use);
    }

    [Fact]
    public void Ctor_WithAccounts_PreselectsFirst()
    {
        FileHosterLoginDto[] accounts = [Login(1, "alice"), Login(2, "bob")];

        FileHosterSelectionViewModel vm = new("Rapidgator", accounts);

        Assert.True(vm.HasAccounts);
        Assert.Same(accounts[0], vm.SelectedAccount);
    }

    [Fact]
    public void Use_SettingTrueWithoutAccounts_CoercesBackToFalse()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        vm.Use = true;

        Assert.False(vm.Use);
    }

    [Fact]
    public void SetAccounts_AddingFirstAccount_ExposesItAndKeepsUseDecision()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", []);

        vm.SetAccounts([Login(7, "alice")]);

        Assert.True(vm.HasAccounts);
        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal("alice", vm.SelectedAccount!.Username);
    }

    [Fact]
    public void SetAccounts_PreservesPreviousSelectionWhenStillPresent()
    {
        FileHosterLoginDto[] before = [Login(1, "alice"), Login(2, "bob")];
        FileHosterSelectionViewModel vm = new("Rapidgator", before);
        vm.SelectedAccount = before[1]; // bob

        FileHosterLoginDto[] after = [Login(1, "alice"), Login(2, "bob"), Login(3, "carol")];
        vm.SetAccounts(after);

        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal(2, vm.SelectedAccount!.Id);
    }

    [Fact]
    public void SetAccounts_WhenSelectionDisappears_FallsBackToFirst()
    {
        FileHosterLoginDto[] before = [Login(1, "alice"), Login(2, "bob")];
        FileHosterSelectionViewModel vm = new("Rapidgator", before);
        vm.SelectedAccount = before[1]; // bob

        vm.SetAccounts([Login(5, "dave")]);

        Assert.NotNull(vm.SelectedAccount);
        Assert.Equal(5, vm.SelectedAccount!.Id);
    }

    [Fact]
    public void SetAccounts_RemovingAllAccounts_AlsoTurnsOffUse()
    {
        FileHosterSelectionViewModel vm = new("Rapidgator", [Login(1, "alice")]);
        vm.Use = true;

        vm.SetAccounts([]);

        Assert.False(vm.HasAccounts);
        Assert.False(vm.Use);
        Assert.Null(vm.SelectedAccount);
    }

    private static FileHosterLoginDto Login(int id, string username) => new()
    {
        Id = id,
        FileHosterName = "Rapidgator",
        Username = username,
        Password = "pw",
    };
}
