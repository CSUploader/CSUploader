// <copyright file="ManualTimeProvider.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests.TestSupport;

/// <summary>
/// A clock the test drives. <see cref="CSUploader.Lib.Net.Http.SpeedLimiter"/>'s behaviour is
/// entirely a function of elapsed time, so real-clock tests of it are slow and flaky; this makes
/// them exact and instant. Hand-rolled rather than pulling in
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> for one type.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    /// <summary>One tick is one nanosecond, so <see cref="Advance"/> converts without rounding.</summary>
    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
}
