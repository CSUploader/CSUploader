// <copyright file="ByteUnit.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CSUploader.Lib;

[JsonConverter(typeof(ByteUnitJsonConverter))]
public class ByteUnit
{
    static ByteUnit()
    {
        // Make sure to set the static variables before the static dictionaries are used
        // (Static variables are initialized on first use; if the dictionary is used before the variable, you'll get an empty dictionary)

        // Both
        B = CreateByteUnit(1, ByteBase.Binary | ByteBase.Decimal, ByteUnitPrefix.Byte, ByteUnitSymbol.B);

        // Binary IEC
        KiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Kibi, ByteUnitSymbol.KiB);
        MiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Mebi, ByteUnitSymbol.MiB);
        GiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Gibi, ByteUnitSymbol.GiB);
        TiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Tebi, ByteUnitSymbol.TiB);
        PiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Pebi, ByteUnitSymbol.PiB);
        EiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Exbi, ByteUnitSymbol.EiB);
        ZiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Zebi, ByteUnitSymbol.ZiB);
        YiB = CreateByteUnit(1, ByteBase.Binary, ByteUnitPrefix.Yobi, ByteUnitSymbol.YiB);

        // Decimal Metric
        kB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Kilo, ByteUnitSymbol.kB);
        MB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Mega, ByteUnitSymbol.MB);
        GB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Giga, ByteUnitSymbol.GB);
        TB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Tera, ByteUnitSymbol.TB);
        PB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Pera, ByteUnitSymbol.PB);
        EB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Exa, ByteUnitSymbol.EB);
        ZB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Zetta, ByteUnitSymbol.ZB);
        YB = CreateByteUnit(1, ByteBase.Decimal, ByteUnitPrefix.Yotta, ByteUnitSymbol.YB);
    }

    public ByteUnit(double bytes)
    {
        Bytes = bytes;
        Base = ByteBase.Decimal;
    }

    public ByteUnit(double bytes, ByteBase byteBase)
    {
        Bytes = bytes;
        Base = byteBase;
    }

    public ByteUnit(double count, ByteUnitSymbol byteUnitSymbol)
    {
        Bytes = GetBytes(count, byteUnitSymbol);
        Base = ByteUnitSymbolTable[byteUnitSymbol].Base;
    }

    public ByteUnit(double count, ByteUnitPrefix byteUnitPrefix)
    {
        Bytes = GetBytes(count, byteUnitPrefix);
        Base = ByteUnitPrefixTable[byteUnitPrefix].Base;
    }

    private ByteUnit(double count, ByteBase byteBase, ByteUnitPrefix byteUnitPrefix, ByteUnitSymbol byteUnitSymbol)
    {
        double multiplier = GetMultiplier(byteBase, byteUnitPrefix);
        Base = byteBase;
        Bytes = count * multiplier;
    }

    // Both
    public static ByteUnit B { get; private set; }

    // Binary IEC
    public static ByteUnit KiB { get; private set; }

    public static ByteUnit MiB { get; private set; }

    public static ByteUnit GiB { get; private set; }

    public static ByteUnit TiB { get; private set; }

    public static ByteUnit PiB { get; private set; }

    public static ByteUnit EiB { get; private set; }

    public static ByteUnit ZiB { get; private set; }

    public static ByteUnit YiB { get; private set; }

    // Decimal Metric
#pragma warning disable SA1300 // Element should begin with upper-case letter
    public static ByteUnit kB { get; private set; }
#pragma warning restore SA1300 // Element should begin with upper-case letter

    public static ByteUnit MB { get; private set; }

    public static ByteUnit GB { get; private set; }

    public static ByteUnit TB { get; private set; }

    public static ByteUnit PB { get; private set; }

    public static ByteUnit EB { get; private set; }

    public static ByteUnit ZB { get; private set; }

    public static ByteUnit YB { get; private set; }

    public ByteBase Base { get; set; }

    public ByteUnitPrefix Prefix
    {
        get
        {
            KeyValuePair<ByteUnit, Tuple<ByteUnitPrefix, ByteUnitSymbol>> previousByteUnit = ByteUnits.First();
            foreach (KeyValuePair<ByteUnit, Tuple<ByteUnitPrefix, ByteUnitSymbol>> byteUnit in ByteUnits.Where(b => b.Key.Base.HasFlag(Base)))
            {
                if (Bytes < byteUnit.Key.Bytes)
                {
                    break;
                }

                previousByteUnit = byteUnit;
            }

            return previousByteUnit.Value.Item1;
        }
    }

    public ByteUnitSymbol Symbol
    {
        get
        {
            KeyValuePair<ByteUnit, Tuple<ByteUnitPrefix, ByteUnitSymbol>> previousByteUnit = ByteUnits.First();
            foreach (KeyValuePair<ByteUnit, Tuple<ByteUnitPrefix, ByteUnitSymbol>> byteUnit in ByteUnits.Where(b => b.Key.Base.HasFlag(Base)))
            {
                if (Bytes < byteUnit.Key.Bytes)
                {
                    break;
                }

                previousByteUnit = byteUnit;
            }

            return previousByteUnit.Value.Item2;
        }
    }

    public double Multiplier => GetMultiplier(Base, Prefix);

    public double Count => Bytes / Multiplier;

    public double Bytes { get; private set; }

    public double KiloBytes => Bytes / kB.Bytes;

    public double MegaBytes => Bytes / MB.Bytes;

    public double GigaBytes => Bytes / GB.Bytes;

    public double TeraBytes => Bytes / TB.Bytes;

    public double PetaBytes => Bytes / PB.Bytes;

    public double ExaBytes => Bytes / EB.Bytes;

    public double ZettaBytes => Bytes / ZB.Bytes;

    public double YottaBytes => Bytes / YB.Bytes;

    public double KibiBytes => Bytes / KiB.Bytes;

    public double MebiBytes => Bytes / MiB.Bytes;

    public double GibiBytes => Bytes / GiB.Bytes;

    public double TebiBytes => Bytes / TiB.Bytes;

    public double PebiBytes => Bytes / PiB.Bytes;

    public double ExiBytes => Bytes / EiB.Bytes;

    public double ZebiBytes => Bytes / ZiB.Bytes;

    public double YobiBytes => Bytes / YiB.Bytes;

    private static Dictionary<ByteUnit, Tuple<ByteUnitPrefix, ByteUnitSymbol>> ByteUnits { get; } = [];

    private static Dictionary<ByteUnitSymbol, ByteUnit> ByteUnitSymbolTable =>
        ByteUnits.Select(b => new KeyValuePair<ByteUnitSymbol, ByteUnit>(b.Value.Item2, b.Key)).ToDictionary(k => k.Key, v => v.Value);

    private static Dictionary<ByteUnitPrefix, ByteUnit> ByteUnitPrefixTable =>
        ByteUnits.Select(b => new KeyValuePair<ByteUnitPrefix, ByteUnit>(b.Value.Item1, b.Key)).ToDictionary(k => k.Key, v => v.Value);

    private static Dictionary<ByteUnitSymbol, Regex> ByteUnitSymbolRegices =>
        ByteUnitSymbolTable.Select(b =>
        {
            string? symbol = Enum.GetName(typeof(ByteUnitSymbol), b.Key);
            Regex regex = new($@"([\d\.,]+)\s*{symbol}{(b.Key is not ByteUnitSymbol.B and not ByteUnitSymbol.Byte ? "?" : string.Empty)}", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
            return new KeyValuePair<ByteUnitSymbol, Regex>(b.Key, regex);
        }).ToDictionary(key => key.Key, value => value.Value);

    public static implicit operator ByteUnit(int bytes)
    {
        return FromBytes(bytes, ByteBase.Decimal);
    }

    public static implicit operator ByteUnit(double bytes)
    {
        return FromBytes(bytes, ByteBase.Decimal);
    }

    public static ByteUnit operator +(ByteUnit byteUnit1, ByteUnit byteUnit2)
    {
        double bytes = byteUnit1.Bytes + byteUnit2.Bytes;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator +(ByteUnit byteUnit1, long bytes2)
    {
        double bytes = byteUnit1.Bytes + bytes2;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator -(ByteUnit byteUnit1, ByteUnit byteUnit2)
    {
        double bytes = byteUnit1.Bytes - byteUnit2.Bytes;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator -(ByteUnit byteUnit1, long bytes2)
    {
        double bytes = byteUnit1.Bytes - bytes2;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator /(ByteUnit byteUnit1, ByteUnit byteUnit2)
    {
        double bytes = byteUnit1.Bytes / byteUnit2.Bytes;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator /(ByteUnit byteUnit1, long bytes2)
    {
        double bytes = byteUnit1.Bytes / bytes2;
        return FromBytes(bytes, byteUnit1.Base);
    }

    public static ByteUnit operator ++(ByteUnit byteUnit)
    {
        double bytes = byteUnit.Bytes + 1;
        return FromBytes(bytes, byteUnit.Base);
    }

    public static ByteUnit operator --(ByteUnit byteUnit)
    {
        double bytes = byteUnit.Bytes - 1;
        return FromBytes(bytes, byteUnit.Base);
    }

    public static double GetBytes(double unitCount, ByteUnitPrefix byteUnitPrefix)
    {
        double multiplier = GetMultiplier(byteUnitPrefix);
        return unitCount * multiplier;
    }

    public static double GetBytes(double unitCount, ByteUnitSymbol byteUnitSymbol)
    {
        ByteUnit byteUnit = ByteUnitSymbolTable[byteUnitSymbol];
        return unitCount * byteUnit.Bytes;
    }

    public static ByteUnit? ParseSize(string size)
    {
        foreach (KeyValuePair<ByteUnitSymbol, ByteUnit> byteUnit in ByteUnitSymbolTable)
        {
            if (TryParseSize(size, byteUnit.Key, out double unitCount))
            {
                return new ByteUnit(unitCount, byteUnit.Key);
            }
        }

        // If no byte unit symbol specified, try to parse it as a number (of bytes)
        if (long.TryParse(size, out long sizeBytes))
        {
            return new ByteUnit(sizeBytes);
        }

        return null;
    }

    public static bool TryParseSize(string size, [NotNullWhen(true)] out ByteUnit? byteUnit)
    {
        byteUnit = null;

        foreach (KeyValuePair<ByteUnitSymbol, ByteUnit> byteUnitValue in ByteUnitSymbolTable)
        {
            if (TryParseSize(size, byteUnitValue.Key, out double unitCount))
            {
                byteUnit = new ByteUnit(unitCount, byteUnitValue.Key);
                return true;
            }
        }

        return false;
    }

    public static ByteUnit FromBytes(double bytes)
    {
        return new ByteUnit(bytes);
    }

    public static ByteUnit FromKiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.KiB);
    }

    public static ByteUnit FromMiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.MiB);
    }

    public static ByteUnit FromGiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.GiB);
    }

    public static ByteUnit FromTiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.TiB);
    }

    public static ByteUnit FromPiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.PiB);
    }

    public static ByteUnit FromEiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.EiB);
    }

    public static ByteUnit FromZiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.ZiB);
    }

    public static ByteUnit FromYiB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.YiB);
    }

    public static ByteUnit FromKB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.kB);
    }

    public static ByteUnit FromMB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.MB);
    }

    public static ByteUnit FromGB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.GB);
    }

    public static ByteUnit FromTB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.TB);
    }

    public static ByteUnit FromPB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.PB);
    }

    public static ByteUnit FromEB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.EB);
    }

    public static ByteUnit FromZB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.ZB);
    }

    public static ByteUnit FromYB(double count)
    {
        return new ByteUnit(count, ByteUnitSymbol.YB);
    }

    public static ByteUnit FromBytes(double bytes, ByteBase byteBase)
    {
        return new ByteUnit(bytes, byteBase);
    }

    public static ByteUnitSymbol[] GetByteUnitSymbols(ByteBase byteBase)
    {
        return [.. ByteUnitSymbolTable.Where(b => b.Value.Base.HasFlag(byteBase)).Select(b => b.Key)];
    }

    public static ByteUnit GetByteUnit(ByteUnitSymbol symbol)
    {
        return ByteUnitSymbolTable[symbol];
    }

    public double GetBytes(double count)
    {
        return Bytes * count;
    }

    public string ToFriendlyString()
    {
        ByteUnit previousByteUnit = this;

        foreach (KeyValuePair<ByteUnitSymbol, ByteUnit> byteUnit in ByteUnitSymbolTable.Where(b => Base.HasFlag(b.Value.Base)))
        {
            if (Bytes < byteUnit.Value.Bytes)
            {
                break;
            }

            previousByteUnit = byteUnit.Value;
        }

        string? unit = Enum.GetName(typeof(ByteUnitSymbol), previousByteUnit.Symbol);
        double bytes = (previousByteUnit.Bytes == 0) ? 0 : Bytes / previousByteUnit.Bytes;
        return $"{bytes:0.##} {unit}";
    }

    public override string ToString()
    {
        string? unit = Enum.GetName(typeof(ByteUnitSymbol), Symbol);
        return $"{Count:0.##} {unit}";
    }

    private static double GetMultiplier(ByteBase byteBase, ByteUnitPrefix byteUnitPrefix)
    {
        return byteUnitPrefix == ByteUnitPrefix.Byte
            ? 1
            : Math.Pow((double)byteBase, (double)byteUnitPrefix - (double)byteBase);
    }

    private static double GetMultiplier(ByteUnitPrefix byteUnitPrefix)
    {
        ByteUnit byteUnit = ByteUnitPrefixTable[byteUnitPrefix];
        return GetMultiplier(byteUnit.Base, byteUnitPrefix);
    }

    private static bool TryParseSize(string size, ByteUnitSymbol symbol, out double unitCount)
    {
        Regex regex = ByteUnitSymbolRegices[symbol];
        Match match = regex.Match(size);
        if (match.Success)
        {
            string sizeMatchValue = match.Groups[1].Value;
            string parsedSize = ParseNumber(sizeMatchValue);

            if (double.TryParse(parsedSize, NumberStyles.Any, CultureInfo.GetCultureInfo("en-US"), out double parsedUnitCount))
            {
                unitCount = parsedUnitCount;
                return true;
            }

            throw new FormatException($"Parsed value {parsedSize} could not be parsed as a number");
        }

        unitCount = 0;
        return false;
    }

    private static string ParseNumber(string number)
    {
        int firstCommaIndex = number.LastIndexOf(',');
        int firstDotIndex = number.IndexOf('.', StringComparison.Ordinal);
        if (firstCommaIndex >= 0 && firstDotIndex >= 0)
        {
            char fractionSeperator = (firstCommaIndex > firstDotIndex) ? ',' : '.';
            char groupSeperator = fractionSeperator == ',' ? '.' : ',';

            // 123.568.567,99 -or- 123,568,567.99
            // 123.45,1234512 -or- 123,45.1234512
            // 2.214512,12532523 -or- 2,214512.12532523
            // etc.
            string[] values = number.Replace(groupSeperator.ToString(), string.Empty, StringComparison.Ordinal).Split([fractionSeperator]);
            string decimals = string.IsNullOrEmpty(values[0]) ? "0" : values[0];
            string fraction = string.IsNullOrEmpty(values[1]) ? "0" : string.Join(string.Empty, values.Skip(1));
            return string.IsNullOrEmpty(fraction) ? $"{decimals}" : $"{decimals}.{fraction}";
        }

        if (firstCommaIndex >= 0 || firstDotIndex >= 0)
        {
            char seperator = firstCommaIndex >= 0 ? ',' : '.';
            string regexSeperator = seperator == ',' ? "," : "\\.";

            Regex regex = new("\\s*\\d*\\s*" + regexSeperator + "{0,2}", RegexOptions.Singleline);
            if (regex.IsMatch(number))
            {
                // 123,45 -or- 123.45
                // 1234,5 -or- 1234.5
                // ,5 -or .5
                // 5, -or- 5.
                string[] values = number.Split([seperator]);
                string decimals = string.IsNullOrEmpty(values[0]) ? "0" : values[0];
                string fraction = string.IsNullOrEmpty(values[1]) ? "0" : values[1];
                return $"{decimals}.{fraction}";
            }

            // 121,152 -or- 121.152
            return number.Replace(regexSeperator.ToString(), string.Empty, StringComparison.Ordinal);
        }

        // 123456
        // 1234
        // 1
        return number;
    }

    private static ByteUnit CreateByteUnit(double count, ByteBase byteBase, ByteUnitPrefix byteUnitPrefix, ByteUnitSymbol byteUnitSymbol)
    {
        ByteUnit byteUnit = new(count, byteBase, byteUnitPrefix, byteUnitSymbol);

        Tuple<ByteUnitPrefix, ByteUnitSymbol> value = new(byteUnitPrefix, byteUnitSymbol);
        ByteUnits.Add(byteUnit, value);
        return byteUnit;
    }
}
