// <copyright file="ProxyTestOutcome.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.ViewModels;

/// <summary>
/// Coarse classification of a proxy's most recent test result, used to drive the
/// status icon (green check / red X / nothing) in the Connection Manager grid.
/// </summary>
public enum ProxyTestOutcome
{
    Untested,
    Ok,
    Failed,
}
