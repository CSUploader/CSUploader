// <copyright file="TempFolders.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Tests.TestSupport;

/// <summary>
/// Folders under the system temp folder for a test's own files, all deleted on <see cref="Dispose"/>. A test
/// class holds one and disposes it from its own Dispose; xUnit builds a class instance per test, so each test
/// cleans up after itself. Helpers that need real files (a <c>PackageFile</c> reads its file's length when it
/// is built) take their folder from here: making their own and never deleting it left a folder in %TEMP% for
/// every package they built, run after run.
/// </summary>
internal sealed class TempFolders : IDisposable
{
    private readonly List<string> _paths = [];

    /// <summary>Creates an empty folder named <paramref name="prefix"/> plus a unique suffix; returns its path.</summary>
    public string Create(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Deletes a folder, retrying for up to half a second while a file in it is still open. Something in
    /// the background can hold one for a moment after the test — a scheduler's hash of the file, say,
    /// which disposing the scheduler does not wait for. Still held after that, the folder stays behind
    /// rather than failing the test.
    /// </summary>
    private static void Delete(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20)
                {
                    return;
                }

                Thread.Sleep(25);
            }
        }
    }
}
