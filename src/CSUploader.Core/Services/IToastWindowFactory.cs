// <copyright file="IToastWindowFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.ViewModels;

namespace CSUploader.Services;

public interface IToastWindowFactory
{
    /// <summary>
    /// Builds an <see cref="IToastHost"/> for the given view-model. The host is not yet
    /// shown — the service positions it before calling <see cref="IToastHost.Show"/>.
    /// </summary>
    public IToastHost Create(ToastViewModel viewModel);
}
