// <copyright file="AlfafilePipelineUploadTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class AlfafilePipelineUploadTests
{
    [Fact]
    public async Task RunAsync_HappyPath_EndsInTransferCompletedWithUrl()
    {
        Queue<string> responses = new(new[]
        {
            // login — Alfafile's login response doesn't include user.folder_id
            """{"response":{"token":"TOK","user":{"email":"u@example.com"}},"status":200,"details":null}""",
            // folder/create — real Alfafile returns slug-style string IDs like "GCtX"
            """{"response":{"folder":{"folder_id":"GCtX","mode":1,"mode_label":"Public","parent_folder_id":"0","name":"package1","url":"https://alfafile.net/folder/GCtX","nb_folders":0,"nb_files":0,"created":1778221286}},"status":200,"details":null}""",
            // file/upload — returns upload_url + upload_id
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.alfafile/post?uuid=U1","file":null,"state":0,"state_label":"Uploading"}},"status":200,"details":null}""",
            // file/upload_info — state=2 ("Done"), public file url
            """{"response":{"upload":{"upload_id":"U1","url":null,"file":{"url":"https://alfafile.net/abc/x.zip"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/abc/x.zip", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_WhenServerHasFileAlready_SkipsBytesAndUsesDedupUrl()
    {
        // file/upload returns state=2 + populated file.url (hash dedup).
        bool uploadInvoked = false;
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCtX"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":null,"file":{"url":"https://alfafile.net/dedup/x.zip"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) =>
            {
                uploadInvoked = true;
                return Task.CompletedTask;
            });

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.False(uploadInvoked, "Bytes upload must be skipped on dedup hit");
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/dedup/x.zip", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_WhenUploadInfoStillProcessing_PollsUntilDone()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCtX"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.alfafile/post"}},"status":200,"details":null}""",
            // poll 1: still processing
            """{"response":{"upload":{"file":null,"state":1}},"status":200,"details":null}""",
            // poll 2: done — surface the public URL
            """{"response":{"upload":{"file":{"url":"https://alfafile.net/poll-ok/x.zip"},"state":2}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/poll-ok/x.zip", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_WhenUploadInfoStateIsFail_StopsPollingAndReportsAttemptFailed()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCtX"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.alfafile/post"}},"status":200,"details":null}""",
            """{"response":{"upload":{"file":null,"state":3}},"status":200,"details":"upload corrupt"}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failure = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("state 3", failure.Reason, StringComparison.Ordinal);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_FileAtDriveRoot_SendsSanitizedFolderName()
    {
        // Regression: uploading a file at D:\file.iso used to produce a folder name of
        // "D:\" (Path.GetDirectoryName + DirectoryInfo.Name on a drive root). The hoster
        // accepted it but the resulting folder was bizarre. Now drive-root paths fall
        // back to "uploads".
        string? folderCreateUrl = null;
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCtX"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":null,"file":{"url":"https://alfafile.net/dedup/x.iso"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url =>
            {
                if (url.Contains("/folder/create", StringComparison.Ordinal))
                {
                    folderCreateUrl = url;
                }
                return responses.Dequeue();
            },
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        AttemptContext ctx = MakeContext() with { FilePath = @"D:\x.iso" };
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx, CancellationToken.None))
        {
        }

        Assert.NotNull(folderCreateUrl);
        Assert.Contains("name=uploads", folderCreateUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("D%3A", folderCreateUrl, StringComparison.Ordinal); // no URL-encoded colon
    }

    [Fact]
    public async Task RunAsync_SecondFileSameFolderName_ReusesCachedFolderId()
    {
        // Regression: Alfafile returns HTTP 409 "Folder with the same name already exists"
        // on duplicate folder/create calls. Without caching, every file in a package after
        // the first would fail because they all want the same folder name. The pipeline
        // caches the folder_id per (credentialsId, parent, name) so subsequent files reuse
        // it without re-calling /folder/create.
        int folderCreateCalls = 0;
        Queue<string> responses = new(new[]
        {
            // === File 1 ===
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCtX"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":null,"file":{"url":"https://alfafile.net/f1/x.iso"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
            // === File 2 — note: no folder/create response queued. If the pipeline calls
            //     it again the test will throw Queue empty. ===
            """{"response":{"upload":{"upload_id":"U2","url":null,"file":{"url":"https://alfafile.net/f2/y.iso"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url =>
            {
                if (url.Contains("/folder/create", StringComparison.Ordinal))
                {
                    folderCreateCalls++;
                }
                return responses.Dequeue();
            },
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        FileHosterLoginDto creds = new() { Id = 17, FileHosterName = "Alfafile", Username = "u", Password = "p" };
        AttemptContext ctx1 = MakeContext() with { Credentials = creds, FileName = "x.iso", FilePath = @"C:\pkg\x.iso" };
        AttemptContext ctx2 = MakeContext() with { Credentials = creds, FileName = "y.iso", FilePath = @"C:\pkg\y.iso" };

        // File 1
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx1, CancellationToken.None)) { }
        // File 2 — must hit the folder cache, skip the folder/create round-trip
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx2, CancellationToken.None)) { }

        Assert.Equal(1, folderCreateCalls);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_FolderCreate409_LooksUpExistingFolderViaInfoEndpoint()
    {
        // Cross-session case: a previous run already created the "package1" folder, so
        // re-creating it returns HTTP 409. The pipeline must call /folder/info on the
        // parent, scan the `folders` array for a matching name, and reuse that id.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            // folder/create — 409 conflict
            """{"response":null,"status":409,"details":"Conflict. Folder with the same name already exists"}""",
            // folder/info on the parent — returns folders[] with the existing one
            """{"response":{"folder":{"folder_id":"0","folders":[{"folder_id":"OldX","name":"other"},{"folder_id":"GCtX","name":"package1"}]}},"status":200,"details":null}""",
            // file/upload — dedup hit so the test stays compact
            """{"response":{"upload":{"upload_id":"U1","url":null,"file":{"url":"https://alfafile.net/dedup/x.iso"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        string? fileUploadUrl = null;
        AlfafilePipeline pipeline = new(
            getOverride: url =>
            {
                if (url.Contains("/file/upload?", StringComparison.Ordinal))
                {
                    fileUploadUrl = url;
                }
                return responses.Dequeue();
            },
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        AttemptContext ctx = MakeContext() with { FilePath = @"C:\package1\x.iso", FileName = "x.iso" };
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/dedup/x.iso", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
        // file/upload should have been called with folder_id=GCtX (the existing one),
        // not "0" or anything else.
        Assert.NotNull(fileUploadUrl);
        Assert.Contains("folder_id=GCtX", fileUploadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FolderCreate409_NoMatchingSubfolder_ReportsFailure()
    {
        // Sanity: if the lookup doesn't find the named folder (mismatched API response
        // shape, stale cache server-side, etc.), the failure is surfaced clearly rather
        // than silently swallowed.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":null,"status":409,"details":"Conflict. Folder with the same name already exists"}""",
            // No matching name in folders[]
            """{"response":{"folder":{"folder_id":"0","folders":[{"folder_id":"OldX","name":"other"}]}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        AttemptFailed failure = Assert.Single(events.OfType<AttemptFailed>());
        Assert.Contains("409", failure.Reason, StringComparison.Ordinal);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_FileUploadResponseHasEmptyArrayFile_ProceedsToBytesUpload()
    {
        // Real Alfafile returns `"file": []` (an empty PHP-serialized map) in the
        // file/upload response while the upload is still pending — without a converter,
        // System.Text.Json refuses to deserialize an array into a UploadUrlFile object
        // and the whole envelope fails to parse, so the pipeline used to report the raw
        // JSON body as the error.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCte"}},"status":200,"details":null}""",
            // Verbatim from the user's report: file is the empty array []
            """{"response":{"upload":{"upload_id":"8qqHd","url":"https://s8.alfafile.net/multipart-upload/92e58316","file":[],"state":0,"state_label":"Uploading"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"8qqHd","url":null,"file":{"url":"https://alfafile.net/ok/win10.iso"},"state":2,"state_label":"Done"}},"status":200,"details":null}""",
        });
        bool bytesUploaded = false;
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) =>
            {
                bytesUploaded = true;
                return Task.CompletedTask;
            });

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.True(bytesUploaded, "Bytes upload must run when file/upload returned `file: []` (no dedup hit)");
        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/ok/win10.iso", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_UploadInfoResponseHasEmptyArrayFile_PollsUntilObject()
    {
        // upload_info during state=Processing also returns `"file": []` — the poll loop
        // must keep going until the file object materializes with a url.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK"},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"GCte"}},"status":200,"details":null}""",
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.alfafile/post"}},"status":200,"details":null}""",
            // Empty-array file, still processing
            """{"response":{"upload":{"file":[],"state":1}},"status":200,"details":null}""",
            // Done, real file object
            """{"response":{"upload":{"file":{"url":"https://alfafile.net/poll/x.iso"},"state":2}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://alfafile.net/poll/x.iso", tc.FileUrl);
        Assert.Empty(events.OfType<AttemptFailed>());
        Assert.Empty(responses);
    }

    [Fact]
    public async Task RunAsync_LoginFailsWithStatus401_YieldsAuthFailed()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":null,"status":401,"details":"Unauthorized. Wrong login or password."}""",
        });
        AlfafilePipeline pipeline = new(url => responses.Dequeue());

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(MakeContext(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e is AuthFailed);
        Assert.Contains(events, e => e is AttemptFailed);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Alfafile",
        Credentials = new FileHosterLoginDto { Id = 17, FileHosterName = "Alfafile", Username = "u@example.com", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
