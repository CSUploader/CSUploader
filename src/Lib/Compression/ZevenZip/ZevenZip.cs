// <copyright file="ZevenZip.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using SevenZip;

namespace CSUploader.Lib.Compression.ZevenZip;

public static class ZevenZip
{
    public static readonly Dictionary<OutArchiveFormat, string> ArchiveFormatsOutput = new()
    {
        { OutArchiveFormat.SevenZip, "7z" },
        { OutArchiveFormat.Zip, "Zip" },
        { OutArchiveFormat.GZip, "GZip" },
        { OutArchiveFormat.BZip2, "BZip2" },
        { OutArchiveFormat.Tar, "Tar" },
        { OutArchiveFormat.XZ, "XZ" }
    };

    public static readonly Dictionary<CompressionLevel, string> CompressionLevels = new()
    {
        { CompressionLevel.None, "None" },
        { CompressionLevel.Fast, "Fast" },
        { CompressionLevel.Low, "Low" },
        { CompressionLevel.Normal, "Normal" },
        { CompressionLevel.High, "High" },
        { CompressionLevel.Ultra, "Ultra" }
    };

    public static readonly Dictionary<CompressionMethod, string> CompressionMethods = new()
    {
        { CompressionMethod.Copy, "Copy" },
        { CompressionMethod.Deflate, "Deflate" },
        { CompressionMethod.Deflate64, "Deflate64" },
        { CompressionMethod.BZip2, "BZip2" },
        { CompressionMethod.Lzma, "LZMA" },
        { CompressionMethod.Lzma2, "LZMA2" },
        { CompressionMethod.Ppmd, "PPMd" },
        { CompressionMethod.Default, "Default" }
    };

    public static readonly Dictionary<int, string> DictionarySizes = new()
    {
        { 64 * KiloBytes, "64 KB" },
        { 1 * MegaBytes, "1 MB" },
        { 2 * MegaBytes, "2 MB" },
        { 3 * MegaBytes, "3 MB" },
        { 4 * MegaBytes, "4 MB" },
        { 6 * MegaBytes, "6 MB" },
        { 8 * MegaBytes, "8 MB" },
        { 12 * MegaBytes, "12 MB" },
        { 16 * MegaBytes, "16 MB" },
        { 24 * MegaBytes, "24 MB" },
        { 32 * MegaBytes, "32 MB" },
        { 48 * MegaBytes, "48 MB" },
        { 64 * MegaBytes, "64 MB" },
        { 96 * MegaBytes, "96 MB" },
        { 128 * MegaBytes, "128 MB" },
        { 192 * MegaBytes, "192 MB" },
        { 256 * MegaBytes, "256 MB" },
        { 384 * MegaBytes, "384 MB" },
        { 512 * MegaBytes, "512 MB" },
        { 768 * MegaBytes, "768 MB" },
        { 1024 * MegaBytes, "1024 MB" },
        { 1536 * MegaBytes, "1536 MB" }
    };

    public static readonly Dictionary<int, string> WordSizes = new()
    {
        { 8, "8" },
        { 12, "12" },
        { 16, "16" },
        { 24, "24" },
        { 32, "32" },
        { 48, "48" },
        { 64, "64" },
        { 96, "96" },
        { 128, "128" },
        { 192, "192" },
        { 256, "256" },
        { 273, "273" }
    };

    public static readonly Dictionary<long, string> SolidBlockSizes = new()
    {
        { 0L, "Non-solid" },
        { 1L * MegaBytes, "1 MB" },
        { 2L * MegaBytes, "2 MB" },
        { 4L * MegaBytes, "4 MB" },
        { 8L * MegaBytes, "8 MB" },
        { 16L * MegaBytes, "16 MB" },
        { 32L * MegaBytes, "32 MB" },
        { 64L * MegaBytes, "64 MB" },
        { 128L * MegaBytes, "128 MB" },
        { 256L * MegaBytes, "256 MB" },
        { 512L * MegaBytes, "512 MB" },
        { 1L * GigaBytes, "1 GB" },
        { 2L * GigaBytes, "2 GB" },
        { 4L * GigaBytes, "4 GB" },
        { 8L * GigaBytes, "8 GB" },
        { 16L * GigaBytes, "16 GB" },
        { 32L * GigaBytes, "32 GB" },
        { 64L * GigaBytes, "64 GB" },
        { 1L * -1, "Solid" }
    };

    public static readonly Dictionary<long, string> SplitVolumeBytes = new()
    {
        { 10 * MegaBytes, "10M" },
        { 100 * MegaBytes, "100M" },
        { 1000 * MegaBytes, "1000M" },
        { 650 * MegaBytes, "650M - CD" },
        { 700 * MegaBytes, "700M - CD" },
        { 4092L * MegaBytes, "4092M - FAT" },
        { 4480L * MegaBytes, "4480M - DVD" },
        { 8128L * MegaBytes, "8128M - DVD DL" },
        { 23040L * MegaBytes, "23040M - BD" }
    };

    private const int KiloBytes = 1024;

    private const int MegaBytes = 1024 * KiloBytes;

    private const int GigaBytes = 1024 * MegaBytes;

    public class CompressionOptions
    {
        public CompressionLevel CompressionLevel { get; set; }

        public CompressionMethod CompressionMethod { get; set; }

        public int DictionarySize { get; set; }

        public int WordSize { get; set; }

        public long SolidBlockSize { get; set; }

        public int NumberCPUThreads { get; set; }

        public int SplitVolumeBytes { get; set; }

        public string? Password { get; set; }
    };
}
