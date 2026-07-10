// <copyright file="ByteUnit.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CSUploader.Lib;

/// <summary>
/// Immutable value-type representation of a byte quantity tagged with its preferred
/// base (binary IEC 1024-based or decimal SI 1000-based). Used by the UI converters
/// and JSON serialization to render sizes in the most appropriate unit.
/// </summary>
[JsonConverter(typeof(ByteUnitJsonConverter))]
public readonly record struct ByteUnit(double Bytes, ByteBase Base)
{
    /// <summary>Defaults to decimal base when only a raw byte count is supplied.</summary>
    public ByteUnit(double bytes)
        : this(bytes, ByteBase.Decimal)
    {
    }

    /// <summary>Constructs from a count of <paramref name="symbol"/> units (e.g. <c>new(3, ByteUnitSymbol.MiB)</c> ≡ 3 MiB).</summary>
    public ByteUnit(double count, ByteUnitSymbol symbol)
        : this(count * Tables.BySymbol[symbol].Bytes, Tables.BySymbol[symbol].Base)
    {
    }

    /// <summary>Constructs from a count of <paramref name="prefix"/> units.</summary>
    public ByteUnit(double count, ByteUnitPrefix prefix)
        : this(count * Tables.ByPrefix[prefix].Bytes, Tables.ByPrefix[prefix].Base)
    {
    }

    // --- Static unit singletons. Kept as API ergonomics so callers can write
    //     `ByteUnit.MiB` or `ByteUnit.kB` instead of constructing a transient. ---

    /// <summary>1 byte — valid in both binary and decimal bases.</summary>
    public static ByteUnit B => Tables.BySymbol[ByteUnitSymbol.B];

    public static ByteUnit KiB => Tables.BySymbol[ByteUnitSymbol.KiB];

    public static ByteUnit MiB => Tables.BySymbol[ByteUnitSymbol.MiB];

    public static ByteUnit GiB => Tables.BySymbol[ByteUnitSymbol.GiB];

    public static ByteUnit TiB => Tables.BySymbol[ByteUnitSymbol.TiB];

    public static ByteUnit PiB => Tables.BySymbol[ByteUnitSymbol.PiB];

    public static ByteUnit EiB => Tables.BySymbol[ByteUnitSymbol.EiB];

    public static ByteUnit ZiB => Tables.BySymbol[ByteUnitSymbol.ZiB];

    public static ByteUnit YiB => Tables.BySymbol[ByteUnitSymbol.YiB];

#pragma warning disable SA1300 // Element should begin with upper-case letter — SI uses lowercase k by convention.
    public static ByteUnit kB => Tables.BySymbol[ByteUnitSymbol.kB];
#pragma warning restore SA1300

    public static ByteUnit MB => Tables.BySymbol[ByteUnitSymbol.MB];

    public static ByteUnit GB => Tables.BySymbol[ByteUnitSymbol.GB];

    public static ByteUnit TB => Tables.BySymbol[ByteUnitSymbol.TB];

    public static ByteUnit PB => Tables.BySymbol[ByteUnitSymbol.PB];

    public static ByteUnit EB => Tables.BySymbol[ByteUnitSymbol.EB];

    public static ByteUnit ZB => Tables.BySymbol[ByteUnitSymbol.ZB];

    public static ByteUnit YB => Tables.BySymbol[ByteUnitSymbol.YB];

    /// <summary>The largest applicable symbol at this byte count + base.</summary>
    public ByteUnitSymbol Symbol => Tables.LargestApplicable(Bytes, Base).Symbol;

    /// <summary>The largest applicable prefix at this byte count + base.</summary>
    public ByteUnitPrefix Prefix => Tables.LargestApplicable(Bytes, Base).Prefix;

    public static ByteUnit FromBytes(double bytes) => new(bytes, ByteBase.Decimal);

    public static ByteUnit FromBytes(double bytes, ByteBase byteBase) => new(bytes, byteBase);

    /// <summary>
    /// Attempts to parse text shapes like <c>"1.5 KiB"</c>, <c>"20MB"</c>, or a plain
    /// integer (treated as bytes). Returns <c>true</c> and a populated
    /// <paramref name="byteUnit"/> on success; <c>false</c> otherwise.
    /// </summary>
    public static bool TryParseSize(string size, [NotNullWhen(true)] out ByteUnit? byteUnit)
    {
        foreach ((ByteUnitSymbol symbol, _) in Tables.BySymbol)
        {
            if (TryParseSize(size, symbol, out double unitCount))
            {
                byteUnit = new ByteUnit(unitCount, symbol);
                return true;
            }
        }

        byteUnit = null;
        return false;
    }

    /// <summary>
    /// Formats as the largest applicable unit at the instance's base, e.g.
    /// <c>"1.5 MiB"</c> for 1,572,864 bytes in binary base. Always renders with
    /// at most two fractional digits.
    /// </summary>
    public string ToFriendlyString()
    {
        (ByteUnit picked, ByteUnitSymbol symbol, _) = Tables.LargestApplicableUnit(Bytes, Base);
        string? unit = Enum.GetName(symbol);
        double count = picked.Bytes == 0 ? 0 : Bytes / picked.Bytes;
        return $"{count:0.##} {unit}";
    }

    public override string ToString() => ToFriendlyString();

    private static bool TryParseSize(string size, ByteUnitSymbol symbol, out double unitCount)
    {
        Match match = Tables.Regexes[symbol].Match(size);
        if (!match.Success)
        {
            unitCount = 0;
            return false;
        }

        string parsedSize = NormalizeNumber(match.Groups[1].Value);
        if (double.TryParse(parsedSize, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
        {
            unitCount = parsed;
            return true;
        }

        throw new FormatException($"Parsed value {parsedSize} could not be parsed as a number");
    }

    /// <summary>
    /// Folds locale-mixed numeric forms — "1.234,56" (eu) vs "1,234.56" (us) vs
    /// "123.45" / "123,45" — into a canonical en-US "decimal-dot" string the
    /// <see cref="double.TryParse(string, NumberStyles, IFormatProvider, out double)"/>
    /// call can handle invariantly.
    /// </summary>
    private static string NormalizeNumber(string number)
    {
        int lastComma = number.LastIndexOf(',');
        int firstDot = number.IndexOf('.', StringComparison.Ordinal);

        // Both separators present → the rightmost one is the fraction separator,
        // the other is a grouping separator we strip.
        if (lastComma >= 0 && firstDot >= 0)
        {
            char fractionSep = lastComma > firstDot ? ',' : '.';
            char groupSep = fractionSep == ',' ? '.' : ',';

            string[] parts = number.Replace(groupSep.ToString(), string.Empty, StringComparison.Ordinal).Split(fractionSep);
            string whole = string.IsNullOrEmpty(parts[0]) ? "0" : parts[0];
            string fraction = string.IsNullOrEmpty(parts[1]) ? "0" : string.Join(string.Empty, parts.Skip(1));
            return string.IsNullOrEmpty(fraction) ? whole : $"{whole}.{fraction}";
        }

        // Only one separator present.
        if (lastComma >= 0 || firstDot >= 0)
        {
            char sep = lastComma >= 0 ? ',' : '.';
            string escapedSep = sep == ',' ? "," : "\\.";

            Regex regex = new("\\s*\\d*\\s*" + escapedSep + "{0,2}", RegexOptions.Singleline);
            if (regex.IsMatch(number))
            {
                // Decimal form: "123,45", "123.45", ",5", ".5", "5,", "5.".
                string[] parts = number.Split(sep);
                string whole = string.IsNullOrEmpty(parts[0]) ? "0" : parts[0];
                string fraction = string.IsNullOrEmpty(parts[1]) ? "0" : parts[1];
                return $"{whole}.{fraction}";
            }

            // Grouping form: "121,152" / "121.152" — strip the separator.
            return number.Replace(escapedSep, string.Empty, StringComparison.Ordinal);
        }

        // Plain integer.
        return number;
    }

    /// <summary>Holds a unit and its associated metadata in the precomputed lookup arrays.</summary>
    private readonly record struct UnitMeta(ByteUnit Unit, ByteUnitPrefix Prefix, ByteUnitSymbol Symbol);

    /// <summary>
    /// Lookup tables built once at type-init time and frozen for cheap reads. Keeps
    /// the hot path of <see cref="Symbol"/> / <see cref="Prefix"/> / <see cref="ToFriendlyString"/>
    /// out of LINQ.
    /// </summary>
    private static class Tables
    {
        public static readonly FrozenDictionary<ByteUnitSymbol, ByteUnit> BySymbol;
        public static readonly FrozenDictionary<ByteUnitPrefix, ByteUnit> ByPrefix;
        public static readonly FrozenDictionary<ByteUnitSymbol, Regex> Regexes;

        // Per-instance-base, ascending-Bytes ordered list. Hot path for ToFriendlyString
        // / Symbol / Prefix — replaces the original "re-run a Where on every call".
        private static readonly FrozenDictionary<ByteBase, UnitMeta[]> ByBase;

        static Tables()
        {
            (ByteBase Base, ByteUnitPrefix Prefix, ByteUnitSymbol Symbol)[] defs =
            [
                (ByteBase.Binary | ByteBase.Decimal, ByteUnitPrefix.Byte, ByteUnitSymbol.B),
                (ByteBase.Binary, ByteUnitPrefix.Kibi, ByteUnitSymbol.KiB),
                (ByteBase.Binary, ByteUnitPrefix.Mebi, ByteUnitSymbol.MiB),
                (ByteBase.Binary, ByteUnitPrefix.Gibi, ByteUnitSymbol.GiB),
                (ByteBase.Binary, ByteUnitPrefix.Tebi, ByteUnitSymbol.TiB),
                (ByteBase.Binary, ByteUnitPrefix.Pebi, ByteUnitSymbol.PiB),
                (ByteBase.Binary, ByteUnitPrefix.Exbi, ByteUnitSymbol.EiB),
                (ByteBase.Binary, ByteUnitPrefix.Zebi, ByteUnitSymbol.ZiB),
                (ByteBase.Binary, ByteUnitPrefix.Yobi, ByteUnitSymbol.YiB),
                (ByteBase.Decimal, ByteUnitPrefix.Kilo, ByteUnitSymbol.kB),
                (ByteBase.Decimal, ByteUnitPrefix.Mega, ByteUnitSymbol.MB),
                (ByteBase.Decimal, ByteUnitPrefix.Giga, ByteUnitSymbol.GB),
                (ByteBase.Decimal, ByteUnitPrefix.Tera, ByteUnitSymbol.TB),
                (ByteBase.Decimal, ByteUnitPrefix.Pera, ByteUnitSymbol.PB),
                (ByteBase.Decimal, ByteUnitPrefix.Exa, ByteUnitSymbol.EB),
                (ByteBase.Decimal, ByteUnitPrefix.Zetta, ByteUnitSymbol.ZB),
                (ByteBase.Decimal, ByteUnitPrefix.Yotta, ByteUnitSymbol.YB),
            ];

            Dictionary<ByteUnitSymbol, ByteUnit> bySymbol = [with(defs.Length)];
            Dictionary<ByteUnitPrefix, ByteUnit> byPrefix = [with(defs.Length)];
            List<UnitMeta> metas = [with(defs.Length)];
            foreach ((ByteBase b, ByteUnitPrefix p, ByteUnitSymbol s) in defs)
            {
                ByteUnit u = new(MultiplierFor(b, p), b);
                bySymbol[s] = u;
                byPrefix[p] = u;
                metas.Add(new UnitMeta(u, p, s));
            }

            BySymbol = bySymbol.ToFrozenDictionary();
            ByPrefix = byPrefix.ToFrozenDictionary();

            // Index by every ByteBase value that can appear as instance.Base:
            // pure Binary, pure Decimal, and the Binary|Decimal combo carried by B.
            ByteBase[] queryBases = [ByteBase.Binary, ByteBase.Decimal, ByteBase.Binary | ByteBase.Decimal];
            ByBase = queryBases
                .Select(qb => KeyValuePair.Create(
                    qb,
                    metas.Where(m => m.Unit.Base.HasFlag(qb)).OrderBy(m => m.Unit.Bytes).ToArray()))
                .ToFrozenDictionary();

            // Regex per symbol — compiled once. "B" / "Byte" symbols don't get the
            // "?" trailer (the symbol must be present); every other symbol is
            // optional so "1.5 KiB" and "1.5KiB" both parse.
            Dictionary<ByteUnitSymbol, Regex> regexes = [with(defs.Length)];
            foreach ((_, _, ByteUnitSymbol s) in defs)
            {
                string symbolName = Enum.GetName(s) ?? string.Empty;
                string optional = s is ByteUnitSymbol.B or ByteUnitSymbol.Byte ? string.Empty : "?";
                regexes[s] = new Regex(
                    $@"([\d\.,]+)\s*{symbolName}{optional}",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
            }

            Regexes = regexes.ToFrozenDictionary();
        }

        public static (ByteUnit Unit, ByteUnitSymbol Symbol, ByteUnitPrefix Prefix) LargestApplicableUnit(double bytes, ByteBase b)
        {
            // Fall back to the Decimal table if we ever see an out-of-band ByteBase
            // (e.g. default(ByteUnit) where Base is 0) — better to render something
            // sensible than throw on a binding-time read.
            if (!ByBase.TryGetValue(b, out UnitMeta[]? arr))
            {
                arr = ByBase[ByteBase.Decimal];
            }

            UnitMeta pick = arr[0];
            foreach (UnitMeta m in arr)
            {
                if (bytes < m.Unit.Bytes)
                {
                    break;
                }

                pick = m;
            }

            return (pick.Unit, pick.Symbol, pick.Prefix);
        }

        public static (ByteUnitPrefix Prefix, ByteUnitSymbol Symbol) LargestApplicable(double bytes, ByteBase b)
        {
            (_, ByteUnitSymbol s, ByteUnitPrefix p) = LargestApplicableUnit(bytes, b);
            return (p, s);
        }

        private static double MultiplierFor(ByteBase byteBase, ByteUnitPrefix prefix) =>
            prefix == ByteUnitPrefix.Byte
                ? 1
                : Math.Pow((double)byteBase, (double)prefix - (double)byteBase);
    }
}
