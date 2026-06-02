// <copyright file="XFileSharingChunkedHelpersTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Pure-function tests for the static helpers that drive the modern XFileSharing
/// chunked upload protocol. The full chunked flow is exercised via the mock webserver
/// in integration tests; these focus on URL derivation, finalize-XML parsing, and the
/// per-chunk acknowledgement check — the parts most likely to break on a hoster shape
/// we haven't seen.
/// </summary>
public class XFileSharingChunkedHelpersTests
{
    // ---- TryDeriveChunkedEndpoints ----

    [Fact]
    public void TryDeriveChunkedEndpoints_StandardUrl_DerivesUpAndApiUrls()
    {
        // The hxfile.co browser captured an upload URL ending in "/cgi-bin/upload.cgi";
        // the JS strips "upload.cgi" and concatenates "up.cgi"/"api.cgi".
        bool ok = XFileSharingApiPipeline.TryDeriveChunkedEndpoints(
            "https://01un-cdn-de-rx.ctmp.world/cgi-bin/upload.cgi",
            out string up,
            out string api);

        Assert.True(ok);
        Assert.Equal("https://01un-cdn-de-rx.ctmp.world/cgi-bin/up.cgi", up);
        Assert.Equal("https://01un-cdn-de-rx.ctmp.world/cgi-bin/api.cgi", api);
    }

    [Fact]
    public void TryDeriveChunkedEndpoints_UrlWithQueryString_PreservesQueryOnBothEndpoints()
    {
        // Ex-Load's API hands back upload.cgi URLs with `?upload_type=file&utype=reg`
        // appended. Strip the path segment, keep the query, append on both derived URLs.
        bool ok = XFileSharingApiPipeline.TryDeriveChunkedEndpoints(
            "http://s5.ex-load.com/cgi-bin/upload.cgi?upload_type=file&utype=reg",
            out string up,
            out string api);

        Assert.True(ok);
        Assert.Equal("http://s5.ex-load.com/cgi-bin/up.cgi?upload_type=file&utype=reg", up);
        Assert.Equal("http://s5.ex-load.com/cgi-bin/api.cgi?upload_type=file&utype=reg", api);
    }

    [Fact]
    public void TryDeriveChunkedEndpoints_UrlWithoutUploadCgiSuffix_ReturnsFalse()
    {
        // If the URL doesn't end with upload.cgi, we can't derive chunked endpoints.
        // Caller treats false as "fall back to classic single-multipart".
        bool ok = XFileSharingApiPipeline.TryDeriveChunkedEndpoints(
            "https://hoster.example/some/other/path.cgi",
            out _,
            out _);

        Assert.False(ok);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("/relative/upload.cgi")]   // relative URL — Uri.TryCreate rejects with UriKind.Absolute
    public void TryDeriveChunkedEndpoints_NonAbsoluteOrUnparseable_ReturnsFalse(string input)
    {
        bool ok = XFileSharingApiPipeline.TryDeriveChunkedEndpoints(input, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryDeriveChunkedEndpoints_NonHttpAbsoluteUrl_StillDerives()
    {
        // Deliberately permissive on the scheme — we don't filter ftp:// etc. because
        // the actual HttpClient POST would fail unambiguously if a hoster ever returns
        // a non-http scheme. Silent fallback to classic in that case is fine; this just
        // documents that we don't pre-filter at the URL-derivation layer.
        bool ok = XFileSharingApiPipeline.TryDeriveChunkedEndpoints(
            "ftp://example/cgi-bin/upload.cgi", out string up, out string api);
        Assert.True(ok);
        Assert.EndsWith("/cgi-bin/up.cgi", up);
        Assert.EndsWith("/cgi-bin/api.cgi", api);
    }

    // ---- ChunkResponseIsOk ----

    [Theory]
    [InlineData("<OK>")]
    [InlineData("<OK>\n")]
    [InlineData("  <OK>  ")]
    [InlineData("<OK>extra noise")]   // accept the prefix — server sometimes appends a newline-separated detail
    public void ChunkResponseIsOk_AcceptsExpectedShapes(string body)
    {
        Assert.True(XFileSharingApiPipeline.ChunkResponseIsOk(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FAIL")]
    [InlineData("<Error>...")]
    [InlineData("OK")]                // no angle brackets — not the protocol shape
    public void ChunkResponseIsOk_RejectsOtherBodies(string body)
    {
        Assert.False(XFileSharingApiPipeline.ChunkResponseIsOk(body));
    }

    // ---- ParseFinalizeFileCode ----

    [Fact]
    public void ParseFinalizeFileCode_HxFileLinksShape_ReturnsCode()
    {
        // Verbatim from the hxfile.co api.cgi response we captured (after gzip
        // decompression). The Code element sits inside a <Links> wrapper.
        const string xml =
            "<Links><Code>6p3abaxbpxbg</Code>" +
            "<Link>https://hxfile.co/6p3abaxbpxbg</Link>" +
            "<DelLink>https://hxfile.co/6p3abaxbpxbg?killcode=is2cjtv2s7</DelLink></Links>";

        Assert.Equal("6p3abaxbpxbg", XFileSharingApiPipeline.ParseFinalizeFileCode(xml));
    }

    [Fact]
    public void ParseFinalizeFileCode_ToleratesWhitespaceInsideCodeElement()
    {
        Assert.Equal("abc123", XFileSharingApiPipeline.ParseFinalizeFileCode("<Code>  abc123  </Code>"));
    }

    [Fact]
    public void ParseFinalizeFileCode_NoCodeElement_ReturnsNull()
    {
        Assert.Null(XFileSharingApiPipeline.ParseFinalizeFileCode("<Error>nope</Error>"));
        Assert.Null(XFileSharingApiPipeline.ParseFinalizeFileCode(""));
        Assert.Null(XFileSharingApiPipeline.ParseFinalizeFileCode("not xml at all"));
    }
}
