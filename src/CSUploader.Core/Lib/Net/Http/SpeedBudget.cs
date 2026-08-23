// <copyright file="SpeedBudget.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// What a throttled stream holds: a way to find the budget governing it right now, and the patience
/// to wait for capacity.
/// <para>
/// The owning scope is re-resolved on EVERY iteration and re-confirmed after EVERY grant, because a
/// user can set or clear a file or package limit while that file is uploading
/// (<c>UploadsViewModel.SetSpeedLimit</c>). Resolving once made a new override invisible to a
/// running transfer; re-resolving without confirming still let a cleared override's dead bucket
/// report "unlimited" and hand over the whole request.
/// </para>
/// </summary>
public sealed class SpeedBudget(Func<SpeedLimiter> resolveActiveLimiter)
{
    /// <summary>Ceiling on one sleep, so a raised limit is noticed promptly and a sibling's refund
    /// becomes usable soon after, without needing a waiter queue.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(1);

    /// <summary>A budget that never throttles.</summary>
    public static SpeedBudget Unlimited { get; } = new(() => SpeedLimiter.Unlimited);

    /// <summary>The scope currently in force. Exposed so callers can assert which bucket a budget
    /// resolves to without acquiring from it.</summary>
    internal SpeedLimiter CurrentLimiter => resolveActiveLimiter();

    /// <summary>
    /// Reserves up to <paramref name="requestedBytes"/> from whichever bucket governs this stream,
    /// waiting for capacity. The caller MUST refund any part of the grant it does not move.
    /// </summary>
    internal async ValueTask<SpeedReservation> AcquireAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        // A zero-length read must return immediately. Without this the loop below spins forever:
        // TryAcquire answers None (Bytes 0) for a zero request, which never satisfies the exit.
        // Stream.ReadAsync(Memory<byte>.Empty) is legal and callers do it.
        if (requestedBytes <= 0)
        {
            return SpeedReservation.None;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SpeedLimiter active = resolveActiveLimiter();
            SpeedReservation reservation = active.TryAcquire(requestedBytes);
            TimeSpan wait;

            if (reservation.Bytes > 0)
            {
                // Reference identity alone is NOT enough — it has an ABA hole. If the user clears an
                // override, the file's own limiter momentarily reads null and hands back the whole
                // request UNCHARGED via the unlimited fast path; restore the override before this
                // check and the side table returns that same instance, so identity matches and an
                // 81,920-byte grant sails past a 10 kB capacity. An uncharged grant is therefore
                // only trustworthy from the STATIC unlimited limiter, which can never become
                // limited. A charged grant is fine either way: real tokens left a real bucket.
                bool grantIsTrustworthy = reservation.Limiter is not null
                    || ReferenceEquals(active, SpeedLimiter.Unlimited);

                if (grantIsTrustworthy && ReferenceEquals(resolveActiveLimiter(), active))
                {
                    return reservation;
                }

                // Give the bytes back to the scope that lent them — nothing was read, so the whole
                // grant is unused — and retry against the scope now in force. Yield first: a
                // resolver that flaps every call would otherwise spin a core until cancellation.
                reservation.Refund(reservation.Bytes);
                wait = MinWait;
            }
            else
            {
                wait = active.EstimateWait(requestedBytes);
                if (wait > MaxWait)
                {
                    wait = MaxWait;
                }
                else if (wait < MinWait)
                {
                    // Floors POSITIVE sub-millisecond waits too. Flooring only "<= 0" lets a 0.2 ms
                    // estimate become an effectively zero delay and spin hot.
                    wait = MinWait;
                }
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
