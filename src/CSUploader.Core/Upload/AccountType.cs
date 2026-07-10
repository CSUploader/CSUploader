// <copyright file="AccountType.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Account tier surfaced in the Accounts grid. Persisted as its integer value
/// (<c>HasConversion&lt;int&gt;</c>), so new members MUST be appended — never reordered — to keep existing
/// rows valid. Most hosters only distinguish <see cref="Free"/> vs <see cref="Premium"/>; ufile.io adds
/// <see cref="Pro"/> and <see cref="Business"/>.
/// </summary>
public enum AccountType
{
    Free,

    Premium,

    Pro,

    Business,
}
