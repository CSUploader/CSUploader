// <copyright file="UploadWizardMode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.ViewModels;

/// <summary>
/// Controls how the upload wizard's first step behaves: pick a folder and walk it,
/// or pick individual files via a multi-select dialog.
/// </summary>
public enum UploadWizardMode
{
    Directory,
    Files,
}
