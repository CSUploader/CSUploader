// <copyright file="PartParallelism.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Reconciles what a hoster says its protocol allows with what the user is willing to spend.
/// </summary>
public static class PartParallelism
{
    /// <summary>
    /// The lesser of the two, never below 1. A hoster's declaration is a statement about its
    /// PROTOCOL — whether parts are order-independent at all — so the user's ceiling can only lower
    /// it, never raise it past what the host can correctly accept.
    /// </summary>
    public static int Effective(int hosterDeclares, int userCeiling)
        => Math.Max(1, Math.Min(hosterDeclares, userCeiling));
}
