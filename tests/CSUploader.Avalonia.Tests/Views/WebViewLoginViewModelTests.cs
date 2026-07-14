// <copyright file="WebViewLoginViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The bridge-readable navigation-state VM behind the Avalonia WebView login window (Phase 8 Task 2). ava_vm
/// reads these to confirm the window opened, initialized, navigated and completed — the only agent-verifiable
/// surface, since the WebView content is a native HWND (design line 88). Plain observable state, no commands.
/// </summary>
public class WebViewLoginViewModelTests
{
    [Fact]
    public void Status_Set_RaisesPropertyChanged()
    {
        var vm = new WebViewLoginViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Status = "Loading";

        Assert.Equal("Loading", vm.Status);
        Assert.Contains(nameof(WebViewLoginViewModel.Status), raised);
    }

    [Fact]
    public void RecordNavigationCompleted_IncrementsCountAndUrl()
    {
        var vm = new WebViewLoginViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecordNavigationCompleted("https://example.test/1");
        vm.RecordNavigationCompleted("https://example.test/2");

        Assert.Equal(2, vm.NavigationCompletedCount);
        Assert.Equal("https://example.test/2", vm.LastNavigationUrl);
        Assert.Contains(nameof(WebViewLoginViewModel.NavigationCompletedCount), raised);
        Assert.Contains(nameof(WebViewLoginViewModel.LastNavigationUrl), raised);
    }

    [Fact]
    public void SamePropertyValue_DoesNotReRaise()
    {
        var vm = new WebViewLoginViewModel { IsInitialized = true };
        int count = 0;
        vm.PropertyChanged += (_, _) => count++;

        vm.IsInitialized = true; // unchanged

        Assert.Equal(0, count);
    }
}
