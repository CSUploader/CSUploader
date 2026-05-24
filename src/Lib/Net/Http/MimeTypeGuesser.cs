// <copyright file="MimeTypeGuesser.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Maps a filename extension to a best-guess MIME type for multipart uploads.
/// Used so the file part in a multipart POST advertises the type a browser would
/// have sent (e.g. <c>video/mp4</c> for <c>.mp4</c>) rather than the generic
/// <c>application/octet-stream</c> — some XFileSharing-family backends (BRupload's
/// fs.cgi in particular) behave differently based on the part's reported type.
/// </summary>
/// <remarks>
/// .NET doesn't ship an extension→MIME mapper in BCL (the one in
/// <c>Microsoft.AspNetCore.StaticFiles</c> would drag in ASP.NET). The list below
/// covers the formats people actually upload to file hosters; anything outside it
/// falls back to <c>application/octet-stream</c>, which matches the previous
/// behaviour so we never regress to a worse guess than what we had.
/// </remarks>
internal static class MimeTypeGuesser
{
    private const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".ts"] = "video/mp2t",

        // Audio
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/opus",

        // Archives
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".bz2"] = "application/x-bzip2",
        [".xz"] = "application/x-xz",

        // Disk images / installers
        [".iso"] = "application/x-iso9660-image",
        [".img"] = "application/octet-stream",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".msi"] = "application/x-msi",
        [".dmg"] = "application/x-apple-diskimage",

        // Documents
        [".pdf"] = "application/pdf",
        [".epub"] = "application/epub+zip",
        [".mobi"] = "application/x-mobipocket-ebook",
        [".txt"] = "text/plain",
        [".srt"] = "application/x-subrip",

        // Images
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    public static string Guess(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return Fallback;
        }

        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            return Fallback;
        }

        return ExtensionToMime.TryGetValue(ext, out string? mime) ? mime : Fallback;
    }
}
