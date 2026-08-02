// <copyright file="MultipartPartHeaderOrderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Pins the byte order of a file part's headers. Browsers and curl always emit
/// <c>Content-Disposition</c> first, and at least one live server parses positionally rather than by
/// name: 1fichier.com's <c>upload.cgi</c> takes the entire body and then answers HTTP 200 with
/// "Pas de fichier trouvé dans l'envoi" ("no file found in the upload") when <c>Content-Type</c>
/// comes first. Isolated live 2026-07-29 — identical bytes, identical field name, only those two
/// headers swapped: Content-Type first → 200 + that message; Content-Disposition first → 302 + the
/// file stored. The cost of getting it wrong is silent and total, since the whole upload is spent
/// before the server says no.
/// </summary>
public class MultipartPartHeaderOrderTests
{
    [Fact]
    public async Task AddFilePart_WritesContentDispositionBeforeContentType()
    {
        string path = Path.Combine(Path.GetTempPath(), "csuploader-part-order.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            using MultipartFormDataContent multipart = new("----CSUploaderBoundaryTest");
            AddPart(multipart, path, "file[]");

            string wire = await multipart.ReadAsStringAsync();
            int disposition = wire.IndexOf("Content-Disposition:", StringComparison.Ordinal);
            int contentType = wire.IndexOf("Content-Type:", StringComparison.Ordinal);

            Assert.True(disposition >= 0, "the part carries no Content-Disposition at all");
            Assert.True(contentType >= 0, "the part carries no Content-Type at all");
            Assert.True(
                disposition < contentType,
                $"Content-Disposition must precede Content-Type in a file part. Wire order was:\n{wire[..Math.Min(300, wire.Length)]}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddFilePart_KeepsTheFieldNameQuotedVerbatim_IncludingBrackets()
    {
        // 1fichier's field is literally "file[]" — brackets are not RFC token characters, so an
        // unquoted or escaped rendering would not survive the server's parser.
        string path = Path.Combine(Path.GetTempPath(), "csuploader-part-name.bin");
        await File.WriteAllBytesAsync(path, [1]);
        try
        {
            using MultipartFormDataContent multipart = new("----CSUploaderBoundaryTest");
            AddPart(multipart, path, "file[]");

            string wire = await multipart.ReadAsStringAsync();
            Assert.Contains(""""name="file[]"; filename="csuploader-part-name.bin"""", wire, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AddPart(MultipartFormDataContent multipart, string path, string fieldName)
    {
        // A plain ByteArrayContent stands in for the streaming upload content — AddFilePart only
        // touches the part's headers, which is what this pins.
        ByteArrayContent content = new(File.ReadAllBytes(path));
        HttpHandler.AddFilePart(multipart, content, fieldName, path);
    }
}
