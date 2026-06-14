// <copyright file="FileBoomAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for FileBoom. Cached inside <see cref="FileBoomPipeline"/>
/// keyed by <see cref="Dal.FileHosterLoginDto.Id"/> so files for the same account skip
/// the WebView2 sign-in round-trip.
/// </summary>
/// <param name="AccessToken">The user-scoped <c>accessToken</c> JWT cookie captured from
/// the WebView2 sign-in. Sent as <c>Cookie: accessToken=&lt;value&gt;</c> on every
/// <c>api.fboom.me/v1/*</c> request.</param>
/// <param name="AdditionalCookies">Optional supplementary cookies (e.g. <c>pcId</c>)
/// that the auth surface wants alongside <c>accessToken</c>. Null/empty when not needed.</param>
internal sealed record FileBoomAuthState(string AccessToken, IReadOnlyDictionary<string, string>? AdditionalCookies);
