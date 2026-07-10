// <copyright file="AccountCheckStatus.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal;

/// <summary>
/// Outcome category for an account-credential check, used by the Account Manager grid to
/// drive cell colour via <see cref="Converters.AccountCheckStatusToColorConverter"/>. Pairs
/// with <see cref="FileHosterLoginDto.StatusMessage"/>, which carries the human-readable
/// detail (e.g. "Premium until 2099", "Wrong password", "The SSL connection could not be
/// established...").
/// </summary>
/// <remarks>
/// <para>Replaces the older approach of sniffing the message text for keywords like
/// "Failed", "Error", "Premium" — that quietly painted any unrecognised string green
/// (e.g. raw network exception messages). The enum makes the intent explicit at the
/// call site and decouples colour from wording or translation.</para>
/// <para>This is a UI-only field — like <see cref="FileHosterLoginDto.StatusMessage"/>,
/// it isn't persisted to the database.</para>
/// </remarks>
public enum AccountCheckStatus
{
    /// <summary>Initial state — credentials haven't been verified yet (or were just loaded from disk).</summary>
    NotChecked,

    /// <summary>A verification round-trip is currently in flight.</summary>
    Checking,

    /// <summary>The verifier confirmed the credentials work.</summary>
    Valid,

    /// <summary>The verifier reported the credentials don't work, OR the request itself failed (network/SSL/timeout). User-visible these are equivalent: red cell + the message explains which.</summary>
    Failed,

    /// <summary>No pipeline is registered for this hoster, so the credentials can't be verified at all. Distinct from <see cref="Failed"/> because there's no "wrong" — we simply have no way to ask.</summary>
    Unsupported,
}
