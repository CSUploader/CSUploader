// <copyright file="FileTypeIconConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows.Data;

namespace CSUploader.Converters;

/// <summary>
/// Maps a file name (or extension) to the matching vscode-icons SVG. The SVGs live under
/// <c>external/vscode-icons/icons/</c> (git submodule) and are embedded at build time
/// under the <c>FileTypes/</c> resource path — see <c>src/CSUploader.csproj</c>.
/// </summary>
public class FileTypeIconConverter : IValueConverter
{
    private const string ResourceBase = "pack://application:,,,/FileTypes/";

    /// <summary>
    /// Maps lower-case file extensions (without the dot) to vscode-icons names. Extensions
    /// not in this table fall back first to a category and then to <c>default_file</c>.
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

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s) || !s.Contains('.', StringComparison.Ordinal))
        {
            // Package rows (e.g. "ReScene Files") and empty values get the generic file icon.
            return DefaultIconUri();
        }

        string ext = Path.GetExtension(s).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            return DefaultIconUri();
        }

        // Only return a URI for extensions we have an explicit mapping for. Unknown
        // extensions fall back to the default icon, since constructing speculative URIs
        // like file_type_xyz.svg would throw at render-time when no such resource exists.
        if (ExtensionMap.TryGetValue(ext, out string? iconName))
        {
            return new Uri(ResourceBase + "file_type_" + iconName + ".svg", UriKind.Absolute);
        }

        return DefaultIconUri();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Uri DefaultIconUri() => new(ResourceBase + "default_file.svg", UriKind.Absolute);
}
