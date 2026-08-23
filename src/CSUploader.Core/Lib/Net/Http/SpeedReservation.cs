// <copyright file="SpeedReservation.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// A grant from a <see cref="SpeedLimiter"/>, carrying the limiter that made it so a refund reaches
/// the right bucket even when acquisitions overlap or the governing scope changed in between.
/// <para>
/// ONE-SHOT: refund exactly once. Two refunds of a copied reservation would fill the bucket twice
/// with no elapsed time, and capacity clamping does not prevent that because another stream can
/// spend the tokens in between. <see cref="ThrottledStream"/> refunds once in a <c>finally</c>;
/// MEGA's send loop takes a reservation and sends exactly it, so it has nothing to give back.
/// </para>
/// <para>
/// <see cref="None"/> means NO GRANT (zero bytes). It is not the unlimited case — unlimited is
/// <c>{ Limiter: null, Bytes: requested }</c>, which grants everything and needs no refund. The
/// distinction matters: <see cref="SpeedBudget"/> trusts an uncharged grant only from the static
/// <see cref="SpeedLimiter.Unlimited"/>.
/// </para>
/// </summary>
internal readonly record struct SpeedReservation(SpeedLimiter? Limiter, int Bytes)
{
    /// <summary>No grant at all — zero bytes, nothing charged, nothing to refund.</summary>
    public static SpeedReservation None => default;

    public void Refund(int unusedBytes)
    {
        if (Limiter is not null && unusedBytes > 0)
        {
            Limiter.Refund(unusedBytes);
        }
    }
}
