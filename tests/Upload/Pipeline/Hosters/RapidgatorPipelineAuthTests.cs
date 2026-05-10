// <copyright file="RapidgatorPipelineAuthTests.cs" company="CSUploader">
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

public class RapidgatorPipelineAuthTests
{
    [Fact]
    public async Task RunAsync_FirstCall_LogsInAndYieldsAuthSucceeded()
    {
        Queue<string> responses = new(new[]
        {
            // /api/v2/user/login → token + primary folder id
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
            if (ev is AuthSucceeded) break; // stop at auth stage; folder+transfer tested separately
        }

        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);
    }

    [Fact]
    public async Task RunAsync_SecondCallSameCredentials_ReusesAuthAndSkipsLogin()
    {
        Queue<string> responses = new(new[]
        {
            // First call: login
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            // First call: folder create
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"nope","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
            // Second call: no login needed (cached) — only folder create
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"nope","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        // First call: consume through TransferStarted so folder response is used
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            if (ev is TransferStarted) break;
        }

        List<UploadEvent> second = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            second.Add(ev);
            if (ev is TransferStarted) break; // cached auth — no login call
        }

        Assert.DoesNotContain(second, e => e is AuthStarted);
    }

    [Fact]
    public async Task RunAsync_ManyParallelCallsSameCredentials_OnlyOneLogin()
    {
        // Repro for "Frequent logins. Please wait 20 seconds…": before the per-credentials
        // semaphore gate, kicking off N parallel pipelines for the same account triggered N
        // concurrent login round-trips. The fix: only the first caller logs in; the rest
        // wait on the gate and reuse the cached token.
        const int parallel = 50;
        const string loginResponse = """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""";
        const string folderResponse = """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"nope","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""";

        int loginCalls = 0;
        async Task<string> Respond(string url)
        {
            if (url.Contains("/api/v2/user/login", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref loginCalls);
                // Hold the request long enough that the other 49 callers pile up at the gate
                // (and hit the cache when they wake up) instead of racing through too fast.
                await Task.Delay(50);
                return loginResponse;
            }
            return folderResponse;
        }

        RapidgatorPipeline pipeline = new(Respond);

        Task[] tasks = new Task[parallel];
        for (int i = 0; i < parallel; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                AttemptContext ctx = MakeContext();
                await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
                {
                    if (ev is TransferStarted) break;
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, loginCalls);
    }

    [Fact]
    public async Task RunAsync_LoginFailsWithStatus401_YieldsAuthFailed()
    {
        Queue<string> responses = new(new[] { """{"response":null,"status":401,"details":"bad credentials"}""" });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains(events, e => e is AuthFailed);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    private static async Task<List<UploadEvent>> CollectAsync(IAsyncEnumerable<UploadEvent> stream)
    {
        List<UploadEvent> result = [];
        await foreach (UploadEvent ev in stream)
        {
            result.Add(ev);
        }

        return result;
    }
}
