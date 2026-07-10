// <copyright file="ByteUnitSymbol.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;

namespace CSUploader.Lib;

public enum ByteUnitSymbol
{
    // Both
    [Description("Byte(s)")]
    B = ByteBase.Binary,

    // Binary IEC
    [Description("Kibibyte(s)")]
    KiB,

    [Description("Mebibyte(s)")]
    MiB,

    [Description("Gibibyte(s)")]
    GiB,

    [Description("Tebibyte(s)")]
    TiB,

    [Description("Pebibyte(s)")]
    PiB,

    [Description("Exbibyte(s)")]
    EiB,

    [Description("Zebibyte(s)")]
    ZiB,

    [Description("Yobibyte(s)")]
    YiB,

    // Decimal Metric
    [Description("Byte(s)")]
    Byte = ByteBase.Decimal,

    [Description("Kilobyte(s)")]
    kB,

    [Description("Megabyte(s)")]
    MB,

    [Description("Gigabyte(s)")]
    GB,

    [Description("Terabyte(s)")]
    TB,

    [Description("Petabyte(s)")]
    PB,

    [Description("Exabyte(s)")]
    EB,

    [Description("Zettabyte(s)")]
    ZB,

    [Description("Yottabyte(s)")]
    YB
}
