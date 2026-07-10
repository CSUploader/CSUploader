// <copyright file="AlfafileAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for Alfafile. Cached inside
/// <see cref="AlfafilePipeline"/> keyed by <see cref="Dal.FileHosterLoginDto.Id"/> so files
/// for the same account skip the login round-trip. Alfafile doesn't expose a per-account
/// "primary folder" the way Rapidgator does, so <see cref="PrimaryFolderId"/> is always
/// <c>"0"</c> (the implicit root folder).
/// </summary>
/// <remarks>
/// Folder IDs on Alfafile are short slugs ("GCtX", "Adcs"), not integers like Rapidgator,
/// so the type is <see cref="string"/>.
/// </remarks>
internal sealed record AlfafileAuthState(string Token, string PrimaryFolderId);
