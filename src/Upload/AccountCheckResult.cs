// <copyright file="AccountCheckResult.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

public record AccountCheckResult(
    bool IsValid,
    AccountType AccountType,
    string? Message = null,
    DateTime? PremiumExpiry = null);
