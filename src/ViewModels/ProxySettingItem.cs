// <copyright file="ProxySettingItem.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib.Net;

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

    public int ProblemsCount => Dto.ProblemsCount;
}
