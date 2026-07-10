// <copyright file="RapidgatorAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for Rapidgator. Cached inside <see cref="RapidgatorPipeline"/>
/// keyed by <see cref="Dal.FileHosterLoginDto.Id"/> so files for the same account skip the login round-trip.
/// </summary>
internal sealed record RapidgatorAuthState(string Token, int PrimaryFolderId);
