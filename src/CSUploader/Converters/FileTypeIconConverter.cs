// <copyright file="FileTypeIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Svg.Skia;

namespace CSUploader.Converters;

/// <summary>
/// Maps a file name (or extension) to the matching vscode-icons SVG, as a cached
/// <see cref="SvgImage"/> for <c>Image.Source</c>. Avalonia twin of the WPF converter
/// (<c>src/Converters/FileTypeIconConverter.cs</c>) — same extension table, same fallbacks;
/// differs only in the payload type (the WPF converter returned a pack URI for SharpVectors).
/// The SVGs live under <c>external/vscode-icons/icons/</c> (git submodule) and are embedded at
/// build time under the <c>FileTypes/</c> avares path — see <c>CSUploader.csproj</c>.
/// </summary>
public class FileTypeIconConverter : IValueConverter
{
    // Keyed by icon name (e.g. "file_type_video", "default_file"), not by extension, so the many
    // extensions that share one icon share one parsed SvgImage — grids don't re-parse per row.
    private static readonly ConcurrentDictionary<string, SvgImage> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps lower-case file extensions (without the dot) to vscode-icons names. Extensions
    /// not in this table fall back to <c>default_file</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.Ordinal)
    {
        // Video
        ["mkv"] = "video",
        ["mp4"] = "video",
        ["avi"] = "video",
        ["mov"] = "video",
        ["wmv"] = "video",
        ["flv"] = "video",
        ["vob"] = "video",
        ["m4v"] = "video",
        ["webm"] = "video",
        ["mpg"] = "video",
        ["mpeg"] = "video",
        ["ts"] = "video",

        // Audio
        ["mp3"] = "audio",
        ["wav"] = "audio",
        ["flac"] = "audio",
        ["aac"] = "audio",
        ["ogg"] = "audio",
        ["m4a"] = "audio",
        ["opus"] = "audio",
        ["wma"] = "audio",

        // Archives (vscode-icons has a "zip" icon shared across archive types)
        ["zip"] = "zip",
        ["rar"] = "zip",
        ["7z"] = "zip",
        ["tar"] = "zip",
        ["gz"] = "zip",
        ["bz2"] = "zip",
        ["xz"] = "zip",
        ["iso"] = "zip",

        // Images
        ["jpg"] = "image",
        ["jpeg"] = "image",
        ["png"] = "image",
        ["gif"] = "image",
        ["bmp"] = "image",
        ["tiff"] = "image",
        ["tif"] = "image",
        ["webp"] = "image",
        ["heic"] = "image",
        ["raw"] = "image",

        // Documents
        ["pdf"] = "pdf",
        ["doc"] = "word",
        ["docx"] = "word",
        ["xls"] = "excel",
        ["xlsx"] = "excel",
        ["csv"] = "excel",
        ["ppt"] = "powerpoint",
        ["pptx"] = "powerpoint",

        // Text / scene info files (.nfo / .srr / .srs are scene release metadata — text-like)
        ["txt"] = "text",
        ["log"] = "text",
        ["md"] = "text",
        ["nfo"] = "text",
        ["srr"] = "text",
        ["srs"] = "text",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s) || !s.Contains('.', StringComparison.Ordinal))
        {
            // Package rows (e.g. "ReScene Files") and empty values get the generic file icon.
            return Load("default_file");
        }

        string ext = Path.GetExtension(s).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
        {
            return Load("default_file");
        }

        // Only return a mapped icon for extensions we know; unknown extensions fall back to the
        // default icon rather than a speculative file_type_xyz.svg that no resource backs.
        return ExtensionMap.TryGetValue(ext, out string? iconName)
            ? Load("file_type_" + iconName)
            : Load("default_file");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SvgImage Load(string name) => Cache.GetOrAdd(name, static n =>
    {
        // Code-side avares uses the assembly name (rename-proof at the Phase 9 cutover). SvgSource.Load
        // eager-parses via Svg.Skia, so a missing FileTypes/<n>.svg throws here rather than blank-rendering.
        string assembly = typeof(FileTypeIconConverter).Assembly.GetName().Name!;
        return new SvgImage { Source = SvgSource.Load($"avares://{assembly}/FileTypes/{n}.svg", null) };
    });
}
