// <copyright file="ByteUnitPrefix.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib
{
    public enum ByteUnitPrefix
    {
        // ^0
        Byte = ByteUnitSymbol.B,

        // ^1
        Kilo = ByteUnitSymbol.kB,
        Kibi = ByteUnitSymbol.KiB,

        // ^2
        Mega = ByteUnitSymbol.MB,
        Mebi = ByteUnitSymbol.MiB,

        // ^3
        Giga = ByteUnitSymbol.GB,
        Gibi = ByteUnitSymbol.GiB,

        // ^4
        Tera = ByteUnitSymbol.TB,
        Tebi = ByteUnitSymbol.TiB,

        // ^5
        Pera = ByteUnitSymbol.PB,
        Pebi = ByteUnitSymbol.PiB,

        // ^6
        Exa = ByteUnitSymbol.EB,
        Exbi = ByteUnitSymbol.EiB,

        // ^7
        Zetta = ByteUnitSymbol.ZB,
        Zebi = ByteUnitSymbol.ZiB,

        // ^8
        Yotta = ByteUnitSymbol.YB,
        Yobi = ByteUnitSymbol.YiB
    }
}
