// <copyright file="ProxySettingItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.ViewModels;

/// <summary>
/// Row VM for the Connection Manager grid. Wraps a <see cref="ProxySettingDto"/> with
/// INPC so two-way bindings on cells flow back into the underlying DTO and survive
/// Save → DB round-trips.
/// </summary>
public partial class ProxySettingItem : ObservableObject
{
    public ProxySettingItem(ProxySettingDto dto)
    {
        Dto = dto;
    }

    public ProxySettingDto Dto { get; }

    public bool Enabled
    {
        get => Dto.Enabled;
        set
        {
            if (Dto.Enabled != value)
            {
                Dto.Enabled = value;
                OnPropertyChanged();
            }
        }
    }

    public ProxyType Type
    {
        get => Dto.Type;
        set
        {
            if (Dto.Type != value)
            {
                Dto.Type = value;
                OnPropertyChanged();
            }
        }
    }

    public string Host
    {
        get => Dto.Host;
        set
        {
            if (Dto.Host != value)
            {
                Dto.Host = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public int Port
    {
        get => Dto.Port;
        set
        {
            if (Dto.Port != value)
            {
                Dto.Port = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Username
    {
        get => Dto.Username;
        set
        {
            if (Dto.Username != value)
            {
                Dto.Username = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Password
    {
        get => Dto.Password;
        set
        {
            if (Dto.Password != value)
            {
                Dto.Password = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Human-readable status from the most recent connectivity test, e.g.
    /// "OK 320ms (1.2.3.4)" or "Failed: timeout". Empty when never tested.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestDetails))]
    [NotifyPropertyChangedFor(nameof(TestOutcome))]
    private string testStatus = string.Empty;

    /// <summary>
    /// Coarse pass/fail/untested classification derived from <see cref="TestStatus"/>,
    /// drives the status-icon column in the Connection Manager grid.
    /// </summary>
    public ProxyTestOutcome TestOutcome
    {
        get
        {
            if (string.IsNullOrEmpty(TestStatus))
            {
                return ProxyTestOutcome.Untested;
            }

            if (TestStatus.StartsWith("OK", StringComparison.Ordinal))
            {
                return ProxyTestOutcome.Ok;
            }

            if (TestStatus.StartsWith("Failed", StringComparison.Ordinal))
            {
                return ProxyTestOutcome.Failed;
            }

            // "Queued…" / "Testing…" — treat as in-progress, no icon yet.
            return ProxyTestOutcome.Untested;
        }
    }

    /// <summary>
    /// Full HTTP transaction (request + response, with headers) from the most recent
    /// test. Surfaced via the Connection Manager's Details button so the user gets
    /// the same diagnostic view as the Logs tab. Null when never tested.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestDetails))]
    private HttpTransaction? testTransaction;

    /// <summary>
    /// True when there's a captured transaction to show in the details modal — drives
    /// the visibility of the row's Details button.
    /// </summary>
    public bool HasTestDetails => TestTransaction is not null;

    /// <summary>
    /// True while a test is in flight, used to show "Testing…" in the grid and to
    /// disable the Test command on this row.
    /// </summary>
    [ObservableProperty]
    private bool isTesting;
}
