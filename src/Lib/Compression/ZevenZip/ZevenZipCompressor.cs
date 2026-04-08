// <copyright file="ZevenZipCompressor.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using SevenZip;

namespace CSUploader.Lib.Compression.ZevenZip;

public class ZevenZipCompressor : Compressor
{
    private readonly IAppLogger _logger;

    public ZevenZipCompressor(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the compression options.
    /// </summary>
    public ZevenZip.CompressionOptions Options { get; set; } = new ZevenZip.CompressionOptions();

    public string? OutputDirectoryPath { get; private set; }

    public DateTime StartDateTime { get; private set; }

    /// <summary>
    /// Compresses the given input directory path to the output directory path.
    /// </summary>
    /// <param name="inputDirectoryPath">The input directory path.</param>
    /// <param name="outputDirectoryPath">The output directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="pauseToken">The pause token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public override async Task CompressAsync(string inputDirectoryPath, string outputDirectoryPath, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
    {
        // Zip
        SevenZipCompressor.LzmaDictionarySize = Options.DictionarySize;
        SevenZipCompressor compressor = new()
        {
            ArchiveFormat = OutArchiveFormat.SevenZip,
            CompressionMethod = Options.CompressionMethod,
            CompressionLevel = Options.CompressionLevel,
            EncryptHeaders = true,
            VolumeSize = Options.SplitVolumeBytes
        };

        compressor.FilesFound += Compressor_FilesFound;
        compressor.FileCompressionStarted += Compressor_FileCompressionStarted;
        compressor.Compressing += Compressor_Compressing;
        compressor.CompressionFinished += Compressor_CompressionFinished;

        StartDateTime = DateTime.Now;
        Size = CalculateSize(inputDirectoryPath);

        string fileName = Path.GetFileNameWithoutExtension(inputDirectoryPath);
        OutputDirectoryPath = Path.Combine(outputDirectoryPath, fileName);
        if (!Directory.Exists(OutputDirectoryPath))
        {
            Directory.CreateDirectory(OutputDirectoryPath);
        }

        string outputFilePath = Path.Combine(OutputDirectoryPath, fileName + ".7z");
        if (File.Exists(outputFilePath))
        {
            int counter = 0;
            do
            {
                outputFilePath = Path.Combine(OutputDirectoryPath, $"{fileName}{counter}.7z");
                counter++;
            } while (File.Exists(outputFilePath));
        }

        try
        {
            ChangeStatus(JobStatus.Running);

            await compressor.CompressDirectoryAsync(inputDirectoryPath, outputFilePath, Options.Password);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to compress directory: {ex}");
        }
    }

    private static long CalculateSize(string directory)
    {
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    private void Compressor_FilesFound(object? sender, IntEventArgs e)
    {
    }

    private void Compressor_FileCompressionStarted(object? sender, FileNameEventArgs e)
    {
    }

    private void Compressor_Compressing(object? sender, ProgressEventArgs e)
    {
        long size = Size ?? 0;
        long bytesCompressed = (long)(size / 100.0 * e.PercentDone);
        OperationProgressEventArgs args = new(size, bytesCompressed, StartDateTime);

        Speed = args.Speed;
        Progress = args.Progress;
        BytesCompressed = args.BytesProcessed;
        BytesRemaining = args.BytesRemaining;
        TimeElapsed = args.TimeElapsed;
        TimeRemaining = args.TimeRemaining;
    }

    private void Compressor_CompressionFinished(object? sender, EventArgs e)
    {
        BytesRemaining = null;
        Speed = null;
        Progress = 100.0;
        TimeRemaining = null;

        ChangeStatus(JobStatus.Success);
    }
}
