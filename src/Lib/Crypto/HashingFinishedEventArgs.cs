// <copyright file="HashingFinishedEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Crypto;

public class HashingFinishedEventArgs : OperationFinishedEventArgs
{
    public HashingFinishedEventArgs(bool success, DateTime startDateTime, byte[]? hash)
        : base(success, startDateTime)
    {
        Hash = hash;
    }

    public HashingFinishedEventArgs(string error, DateTime startDateTime)
        : base(false, startDateTime)
    {
        Error = error;
    }

    /// <summary>
    /// Gets or sets the error.
    /// </summary>
    /// <value>
    /// The error.
    /// </value>
    public string? Error { get; protected set; }

    /// <summary>
    /// Gets or sets the hash.
    /// </summary>
    /// <value>
    /// The hash.
    /// </value>
    public byte[]? Hash { get; protected set; }
}
