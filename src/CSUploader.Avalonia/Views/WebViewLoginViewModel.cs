// <copyright file="WebViewLoginViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CSUploader.Views;

/// <summary>
/// Bridge-readable navigation-state mirror for <see cref="WebViewLoginWindow"/> (design line 88: navigation
/// events "exposed on the login VM so ava_vm reads them"). The window's code-behind sets these; the header /
/// status strip bind to <see cref="Header"/> / <see cref="Status"/>. No commands — Cancel is a code-behind
/// <c>Click</c>; the WebView completion touches the native controller and cannot be a VM command.
/// </summary>
public sealed class WebViewLoginViewModel : INotifyPropertyChanged
{
    private string _header = string.Empty;
    private string _status = string.Empty;
    private bool _isInitialized;
    private string? _lastNavigationUrl;
    private int _navigationCompletedCount;
    private bool _isCompleted;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Hoster header line ("Sign in to X"). Bound to the header TextBlock.</summary>
    public string Header { get => _header; set => Set(ref _header, value); }

    /// <summary>Current status-strip text (initializing / loading URL / current source / cookie-read error).</summary>
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>True once the environment + controller are created and the first navigation is kicked off.</summary>
    public bool IsInitialized { get => _isInitialized; set => Set(ref _isInitialized, value); }

    /// <summary>Most recent navigated URL (SourceChanged / NavigationCompleted).</summary>
    public string? LastNavigationUrl { get => _lastNavigationUrl; set => Set(ref _lastNavigationUrl, value); }

    /// <summary>Count of NavigationCompleted events — ava_vm's "did navigation actually happen" signal.</summary>
    public int NavigationCompletedCount { get => _navigationCompletedCount; set => Set(ref _navigationCompletedCount, value); }

    /// <summary>True once a session cookie / probe value was captured (sign-in success).</summary>
    public bool IsCompleted { get => _isCompleted; set => Set(ref _isCompleted, value); }

    /// <summary>Bumps <see cref="NavigationCompletedCount"/> and records the URL in one call (window use).</summary>
    public void RecordNavigationCompleted(string? url)
    {
        NavigationCompletedCount++;
        LastNavigationUrl = url;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
