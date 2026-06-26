// <copyright file="MoneyPlatformAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>Cached per-account auth for a <see cref="MoneyPlatformPipeline"/> hoster: the
/// <c>accessToken</c> JWT plus any supplementary cookies (e.g. <c>pcId</c>) the signed-in requests
/// carry.</summary>
internal sealed record MoneyPlatformAuthState(string AccessToken, IReadOnlyDictionary<string, string>? AdditionalCookies);
