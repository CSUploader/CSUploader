// <copyright file="IcerBoxAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for IcerBox — the Bearer JWT returned by
/// <c>/api/v1/auth/login</c>. Cached inside <see cref="IcerBoxPipeline"/> keyed by
/// <see cref="Dal.FileHosterLoginDto.Id"/> so files for the same account skip the login
/// round-trip; invalidated (and re-fetched on the next attempt) when a call comes back 401/403.
/// </summary>
internal sealed record IcerBoxAuthState(string Token);
