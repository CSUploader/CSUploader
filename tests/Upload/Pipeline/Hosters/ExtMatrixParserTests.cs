// <copyright file="ExtMatrixParserTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pure-function tests for the plain-text and HTML parsers behind <see cref="ExtMatrixPipeline"/>.
/// ExtMatrix's API doesn't quote exact wire shapes in its docs, so these tests are the
/// place we encode "what shapes we've decided to accept" — when a real upload surfaces a
/// previously-unseen body we'll add it here and tune the regex.
/// </summary>
public class ExtMatrixParserTests
{
    // ---- ParseUploadResponse — success path ----

    [Fact]
    public void ParseUploadResponse_SuccessWithUrl_ReturnsThatUrl()
    {
        // Docs phrasing: "upload_success followed by download URL and deletion URL".
        // We don't know the exact delimiter so the parser just grabs the first http(s)
        // URL after the success marker.
        HttpResponseSnapshot snap = new(200, "upload_success\nhttps://www.extmatrix.com/abc123\nhttps://www.extmatrix.com/abc123?killcode=xyz", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Equal("https://www.extmatrix.com/abc123", url);
        Assert.Null(error);
        Assert.False(invalid);
    }

    [Theory]
    [InlineData("upload_success https://www.extmatrix.com/file/abc https://www.extmatrix.com/file/abc?killcode=xyz")]
    [InlineData("upload_success|https://www.extmatrix.com/file/abc|https://www.extmatrix.com/file/abc?killcode=xyz")]
    [InlineData("upload_success\r\nhttps://www.extmatrix.com/file/abc\r\nhttps://www.extmatrix.com/file/abc?killcode=xyz")]
    public void ParseUploadResponse_TolerantOfDelimitersAroundUrl(string body)
    {
        // Different XFS-derived deployments use space, pipe, or CRLF as delimiters between
        // the marker and the URL. Cover all three — and if a real upload shows a fourth
        // shape, we'll add it.
        HttpResponseSnapshot snap = new(200, body, []);

        (string? url, _, _) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Equal("https://www.extmatrix.com/file/abc", url);
    }

    // ---- ParseUploadResponse — failure paths ----

    [Fact]
    public void ParseUploadResponse_InvalidApi_FlagsAuthExpiredAndClearsErrorMessage()
    {
        // `invalid_api` is special — caller treats it as "the user's API key was rejected"
        // and forces a re-bootstrap. We return (null, null, true) rather than an error
        // message so the caller's switch on the third tuple field is unambiguous.
        HttpResponseSnapshot snap = new(200, "invalid_api", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Null(url);
        Assert.Null(error);
        Assert.True(invalid);
    }

    [Fact]
    public void ParseUploadResponse_UploadFailed_ReturnsErrorWithSnippet()
    {
        HttpResponseSnapshot snap = new(200, "upload_failed: file too large", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Null(url);
        Assert.Contains("upload_failed", error, StringComparison.Ordinal);
        Assert.False(invalid);
    }

    [Fact]
    public void ParseUploadResponse_UnknownBody_ReturnsUnrecognisedError()
    {
        // Defensive: if ExtMatrix ever changes their wire shape we surface the raw body
        // (snippet-clamped) so the user can report it instead of getting a silent failure.
        HttpResponseSnapshot snap = new(200, "<html><body>something else entirely</body></html>", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Null(url);
        Assert.Contains("unrecognised", error, StringComparison.Ordinal);
        Assert.False(invalid);
    }

    [Fact]
    public void ParseUploadResponse_SuccessMarkerWithoutUrl_ReturnsClearError()
    {
        HttpResponseSnapshot snap = new(200, "upload_success", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Null(url);
        Assert.Contains("no public URL", error, StringComparison.Ordinal);
        Assert.False(invalid);
    }

    [Fact]
    public void ParseUploadResponse_Non2xx_ReturnsTransportError()
    {
        // A 502 from the upstream / proxy shouldn't be confused with a hoster-level error.
        HttpResponseSnapshot snap = new(502, "Bad Gateway", []);

        (string? url, string? error, bool invalid) = ExtMatrixPipeline.ParseUploadResponse(snap);

        Assert.Null(url);
        Assert.Contains("HTTP 502", error, StringComparison.Ordinal);
        Assert.False(invalid);
    }

    // ---- ExtractApiKey ----

    [Fact]
    public void ExtractApiKey_InputNameThenValue_ReturnsKey()
    {
        const string html = """<input type="text" name="api_key" value="abc123DEFxyz" readonly>""";

        Assert.Equal("abc123DEFxyz", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_InputValueThenName_ReturnsKey()
    {
        // Some PHP templates write value= before name=; we accept either order.
        const string html = """<input value="ZyXw98_76" name="api_key" type="text" />""";

        Assert.Equal("ZyXw98_76", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_DashedAttributeNameVariant_StillMatches()
    {
        // Some XFS forks render `name="api-key"` (hyphen) instead of `api_key` (underscore).
        const string html = """<input name="api-key" value="HYPHEN_VARIANT" />""";

        Assert.Equal("HYPHEN_VARIANT", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_QueryParameterFallback_ExtractsFromUrl()
    {
        // Account pages sometimes render an example URL with the user's key already
        // substituted in — e.g. inside a "your API URL is …" hint block.
        const string html = """<p>Your API URL: <code>https://www.extmatrix.com/api/upload.php?api_key=URL_EMBEDDED</code></p>""";

        Assert.Equal("URL_EMBEDDED", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_VerbatimExtMatrixRendering_ReturnsKey()
    {
        // Captured verbatim from the live /members/account.php response on 2026-06-06
        // (after gunzipping the Fiddler trace at D:\temp2\extmatrix). The <input> has
        // disabled="disabled" and NO name= attribute — only the preceding "API Key:"
        // label cell identifies what the input contains. This is the load-bearing case
        // ExtMatrixPipeline.ExtractApiKey was rewritten for; if anyone tightens the
        // regex back to a name-anchored form, this test fails first.
        const string html =
            """<td style="text-align:right;">API Key:</td>""" +
            """<td style="text-align:left;"><input type="text" style="width:300px;" disabled="disabled" value="0sBtMhDiNkaY5v3wtErzNZ7hXD1l" /> <a href="./account.php?task=get_api_key">Get API Key</a></td>""";

        Assert.Equal("0sBtMhDiNkaY5v3wtErzNZ7hXD1l", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_LabelAnchoredWithLineBreaks_StillMatches()
    {
        // Same shape as the live rendering but with the label and input across multiple
        // lines (newlines + indentation between the <td>s). The Singleline regex option
        // makes `.` match newlines so the label-anchored branch still fires.
        const string html =
            "<td>API Key:</td>\n" +
            "  <td>\n" +
            "    <input type=\"text\" disabled=\"disabled\" value=\"KeyAcrossLines123\" />\n" +
            "    <a href=\"./account.php?task=get_api_key\">Get API Key</a>\n" +
            "  </td>";

        Assert.Equal("KeyAcrossLines123", ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_NoMatch_ReturnsNull()
    {
        // No <input> in sight — only the "[Get API Key]" affordance. This shape appears
        // when the user has not yet generated their key; the scraper falls through to
        // hitting GenerateApiKeyUrl and re-fetching.
        const string html = """<p>Click here: <a href="?task=get_api_key">[Get API Key]</a></p>""";

        Assert.Null(ExtMatrixPipeline.ExtractApiKey(html));
    }

    [Fact]
    public void ExtractApiKey_EmptyHtml_ReturnsNull()
    {
        Assert.Null(ExtMatrixPipeline.ExtractApiKey(string.Empty));
    }
}
