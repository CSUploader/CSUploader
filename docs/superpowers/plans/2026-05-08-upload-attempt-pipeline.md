# Upload Attempt Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-`PackageFile` mutable `FileHosterClient` (with its nullable `HttpHandler` field, four-event surface, and reads of `ProxyManager.Current` / `AppSettings.Current` / `Logger.Current` statics) with an explicit per-attempt pipeline. Each upload attempt becomes an immutable `AttemptContext` flowing through an `AttemptRunner`, which emits a single `IAsyncEnumerable<UploadEvent>` consumed by `PackageFile` and `ProxyManager`. Per-hoster code shrinks to an `IFileHosterPipeline` whose only required entrypoint is `RunAsync(AttemptContext)` — auth shape (token / cookie / OAuth / API key) is opaque to the runner and owned by the pipeline impl, so adding hosters with different auth flows is mechanical.

**Architecture:**
- Outer pipeline (shared): `ProxyChoice` selection → `HttpHandler` build → invoke per-hoster `IFileHosterPipeline.RunAsync(ctx)` → emit terminal `AttemptCompleted` event.
- Inner pipeline (per-hoster): owns auth state, login/folder/transfer flow, decides retries-on-401. Receives a non-null `HttpHandler` via `AttemptContext` — CS8602 vanishes by type.
- `ProxyManager` subscribes to `AttemptRunner.AttemptCompleted` instead of being called from `PackageManager.OnFileStateChanged`. The `ActiveProxyId` side-channel on `FileHosterClient` is deleted.
- Hashing stays where it is for Phases 0–3; in Phase 4 it's extracted to `IHashingService` and `PackageFile` consumes hashing events from there.

**Tech Stack:** .NET 10, WPF, EF Core (SQLite), xUnit + Moq, System.Text.Json. CommunityToolkit.Mvvm source generators. In-memory SQLite for repository tests via `TestDbContextFactory`.

---

## File Structure

### New files

| Path | Responsibility |
|---|---|
| `src/Upload/Pipeline/AttemptContext.cs` | Immutable record threaded through every stage. All properties non-nullable. |
| `src/Upload/Pipeline/UploadEvent.cs` | Sealed record hierarchy: `ProxyPicked`, `HandlerBuilt`, `AuthStarted/Succeeded/Failed`, `TransferStarted/Progress/Completed`, `AttemptFailed`, `AttemptCancelled`, `AttemptCompleted`. |
| `src/Upload/Pipeline/IFileHosterPipeline.cs` | Per-hoster contract: `Name`, `RequiresHashingBeforeUpload`, `IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext, CancellationToken)`. |
| `src/Upload/Pipeline/IFileHosterRegistry.cs` | Looks up pipelines by hoster name. |
| `src/Upload/Pipeline/DefaultFileHosterRegistry.cs` | Concrete registry built from `IEnumerable<IFileHosterPipeline>` injected by DI. |
| `src/Upload/Pipeline/AttemptRunner.cs` | Orchestrator: picks proxy, builds handler, invokes pipeline, emits unified event stream, raises `AttemptCompleted` for `ProxyManager` to subscribe. |
| `src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs` | `IFileHosterPipeline` impl: token auth, folder create, multipart upload. Per-credentials auth cache. |
| `src/Upload/Pipeline/Hosters/RapidgatorAuthState.cs` | Internal record holding token + UserInfo for one set of credentials. |
| `src/Lib/Net/IProxySource.cs` | DI seam over `ProxyManager`: `ProxyChoice Next()`. |
| `src/Lib/Net/ProxyChoice.cs` | Non-nullable record with static `Direct` instance. |
| `src/Lib/Net/Http/IHttpHandlerFactory.cs` | DI seam: `HttpHandler Create(ProxyChoice, IAppLogger)`. |
| `src/Lib/Net/Http/DefaultHttpHandlerFactory.cs` | Builds `HttpHandler` with proxy + mock-server snapshot. |
| `src/Lib/Net/Http/MockServerConfig.cs` | Immutable snapshot record (replaces `AppSettings.Current` read in `HttpHandler`). |
| `tests/Upload/Pipeline/AttemptRunnerTests.cs` | Runner tests with fakes. |
| `tests/Upload/Pipeline/Hosters/RapidgatorPipelineTests.cs` | Per-pipeline tests with fake `HttpHandler`. |
| `tests/Upload/Pipeline/Hosters/FakeCookieHosterPipelineTests.cs` | Demonstrates auth-shape flexibility (cookie-based, no token). |
| `tests/Lib/Net/Http/DefaultHttpHandlerFactoryTests.cs` | Factory tests. |

### Modified files

| Path | Change |
|---|---|
| `src/Lib/Net/ProxyManager.cs` | Implement `IProxySource`. Subscribe to `AttemptRunner.AttemptCompleted`. Delete static `Current` setter (Phase 4). |
| `src/Lib/Net/Http/HttpHandler.cs` | Constructor takes `MockServerConfig`; drop `AppSettings.Current` read in `MaybeRewriteToMockServer`. |
| `src/Upload/FileHosterClient.cs` | Phase 4: shrink to `Name` / `Host` / `Protocol` metadata + factory map; events deleted. |
| `src/Upload/RapidgatorClient.cs` | Phase 4: deleted. |
| `src/Upload/PackageFile.cs` | Subscribes to `AttemptRunner` event stream; old four-event subscription deleted. |
| `src/Upload/UploadScheduler.cs` | `LaunchUpload(file)` calls `_attemptRunner.RunAsync(file, ct)` and forwards events. |
| `src/Upload/PackageManager.cs` | Drop `ProxyManager.Current?.ReportResult` block in `OnFileStateChanged` — runner does it now. |
| `src/App.xaml.cs` | Register `IProxySource`, `IHttpHandlerFactory`, `IFileHosterRegistry`, `IFileHosterPipeline` (Rapidgator), `AttemptRunner`. Remove `ProxyManager.Current = ...` line. |
| `src/Upload/Package.cs` | `AddPackageFiles` no longer mutates `FileHoster.SharedSessionCache` — auth state lives in the pipeline. |

---

## Conventions Used Throughout

- **MIT header** on every new `.cs` file (matches existing `RapidgatorClient.cs`).
- **File-scoped namespaces**, nullable enabled, `StringComparison.Ordinal` for any string comparison.
- **Tests** use xUnit + Moq, mirror source paths under `tests/`, name methods `MethodName_StateUnderTest_ExpectedBehavior`.
- **Commits** are scoped to a single task. Format: `pipeline(<phase>): <change>` (e.g. `pipeline(0): add ProxyChoice record`).
- **Run all tests** (`dotnet test --nologo --no-build` after `dotnet build`) at the end of each task before committing. The failing test should be the only failure between Steps 2 and 4.
- Each task that adds production code adds a test. No production change without a test that justifies it.

---

## Phase 0: Foundation Types (no behaviour change)

Phase 0 introduces the new types in isolation. Nothing wires up to the scheduler yet. Each task is independently committable.

### Task 0.1: Add `ProxyChoice`

**Files:**
- Create: `src/Lib/Net/ProxyChoice.cs`
- Test: `tests/Lib/Net/ProxyChoiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Lib/Net/ProxyChoiceTests.cs
// <copyright file="ProxyChoiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Tests.Lib.Net;

public class ProxyChoiceTests
{
    [Fact]
    public void Direct_HasZeroId_AndNullWebProxy()
    {
        ProxyChoice direct = ProxyChoice.Direct;

        Assert.Equal(0, direct.Id);
        Assert.Null(direct.WebProxy);
        Assert.Equal("(direct)", direct.Description);
    }

    [Fact]
    public void Via_PreservesIdAndDescription()
    {
        ProxyChoice via = new(42, null, "http://example:8080");

        Assert.Equal(42, via.Id);
        Assert.Equal("http://example:8080", via.Description);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ProxyChoiceTests"`
Expected: build error — `ProxyChoice` does not exist.

- [ ] **Step 3: Implement the type**

```csharp
// src/Lib/Net/ProxyChoice.cs
// <copyright file="ProxyChoice.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;

namespace CSUploader.Lib.Net;

/// <summary>
/// Immutable, non-null description of which proxy a given upload attempt is routed through.
/// Use <see cref="Direct"/> instead of null when no proxy is in play — that way every consumer
/// sees a value-typed answer and the type system enforces "every attempt has a proxy decision".
/// </summary>
/// <param name="Id">Database id of the proxy row, or 0 for direct connection.</param>
/// <param name="WebProxy">Resolved <see cref="IWebProxy"/> for the HttpClient; null for direct.</param>
/// <param name="Description">Human-readable form, surfaced to the Logs tab.</param>
public sealed record ProxyChoice(int Id, IWebProxy? WebProxy, string Description)
{
    public static ProxyChoice Direct { get; } = new(0, null, "(direct)");
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~ProxyChoiceTests"`
Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Lib/Net/ProxyChoice.cs tests/Lib/Net/ProxyChoiceTests.cs
git commit -m "pipeline(0): add ProxyChoice record"
```

---

### Task 0.2: Add `UploadEvent` hierarchy

**Files:**
- Create: `src/Upload/Pipeline/UploadEvent.cs`
- Test: `tests/Upload/Pipeline/UploadEventTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/UploadEventTests.cs
// <copyright file="UploadEventTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline;

public class UploadEventTests
{
    [Fact]
    public void AttemptCompleted_CarriesProxyIdAndOutcome()
    {
        AttemptCompleted ev = new(Success: true, ProxyId: 7, FileUrl: "https://x/y");

        Assert.True(ev.Success);
        Assert.Equal(7, ev.ProxyId);
        Assert.Equal("https://x/y", ev.FileUrl);
    }

    [Fact]
    public void TransferProgress_ComputesPercentage()
    {
        TransferProgress ev = new(BytesUploaded: 25, TotalBytes: 100, SpeedBytesPerSec: 1024);

        Assert.Equal(25.0, ev.PercentComplete);
    }

    [Fact]
    public void TransferProgress_HandlesZeroTotal()
    {
        TransferProgress ev = new(BytesUploaded: 0, TotalBytes: 0, SpeedBytesPerSec: 0);

        Assert.Equal(0.0, ev.PercentComplete);
    }

    [Fact]
    public void ProxyPicked_RecordsTheChoice()
    {
        ProxyPicked ev = new(ProxyChoice.Direct);

        Assert.Same(ProxyChoice.Direct, ev.Proxy);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~UploadEventTests"`
Expected: build errors — types do not exist.

- [ ] **Step 3: Implement the hierarchy**

```csharp
// src/Upload/Pipeline/UploadEvent.cs
// <copyright file="UploadEvent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Base type for every event emitted by an upload attempt. Subscribers (PackageFile,
/// ProxyManager) pattern-match the concrete type to update state.
/// </summary>
public abstract record UploadEvent;

public sealed record ProxyPicked(ProxyChoice Proxy) : UploadEvent;

public sealed record HandlerBuilt(HttpHandler Handler) : UploadEvent;

public sealed record AuthStarted : UploadEvent;

public sealed record AuthSucceeded : UploadEvent;

public sealed record AuthFailed(string Reason) : UploadEvent;

public sealed record TransferStarted(long TotalBytes) : UploadEvent;

public sealed record TransferProgress(long BytesUploaded, long TotalBytes, double SpeedBytesPerSec) : UploadEvent
{
    public double PercentComplete => TotalBytes > 0 ? (double)BytesUploaded / TotalBytes * 100.0 : 0.0;
}

public sealed record TransferCompleted(string FileUrl) : UploadEvent;

public sealed record AttemptCancelled : UploadEvent;

public sealed record AttemptFailed(string Reason, Exception? Exception) : UploadEvent;

/// <summary>
/// Final terminal event for every attempt — emitted exactly once. ProxyManager listens
/// for this to update its connectivity icons; PackageFile uses it to flip terminal state.
/// </summary>
public sealed record AttemptCompleted(bool Success, int ProxyId, string? FileUrl) : UploadEvent;
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~UploadEventTests"`
Expected: 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/UploadEvent.cs tests/Upload/Pipeline/UploadEventTests.cs
git commit -m "pipeline(0): add UploadEvent hierarchy"
```

---

### Task 0.3: Add `AttemptContext`

**Files:**
- Create: `src/Upload/Pipeline/AttemptContext.cs`
- Test: `tests/Upload/Pipeline/AttemptContextTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/AttemptContextTests.cs
// <copyright file="AttemptContextTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class AttemptContextTests
{
    [Fact]
    public void With_PreservesUntouchedFields()
    {
        FileHosterLoginDto creds = new() { Id = 5, FileHosterName = "X", Username = "u", Password = "p" };
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>());
        AttemptContext ctx = new()
        {
            AttemptId = Guid.NewGuid(),
            FilePath = "/tmp/x.zip",
            FileName = "x.zip",
            FileSize = 100,
            FileHash = null,
            HosterName = "Rapidgator",
            Credentials = creds,
            Proxy = ProxyChoice.Direct,
            Handler = handler,
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };

        AttemptContext copy = ctx with { FileHash = "abcd" };

        Assert.Equal("abcd", copy.FileHash);
        Assert.Same(creds, copy.Credentials);
        Assert.Same(handler, copy.Handler); // non-nullable by record signature
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~AttemptContextTests"`
Expected: build error — `AttemptContext` undefined.

- [ ] **Step 3: Implement the record**

```csharp
// src/Upload/Pipeline/AttemptContext.cs
// <copyright file="AttemptContext.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Immutable per-attempt context flowing through <see cref="AttemptRunner"/> and into
/// <see cref="IFileHosterPipeline"/>. Every property is non-nullable except where genuinely
/// optional (<see cref="FileHash"/> — only present once the hashing stage completes).
/// </summary>
public sealed record AttemptContext
{
    public required Guid AttemptId { get; init; }

    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }

    /// <summary>Hex-lowercased hash, set after hashing completes. Null on first construction.</summary>
    public string? FileHash { get; init; }

    public required string HosterName { get; init; }

    public required FileHosterLoginDto Credentials { get; init; }

    public required ProxyChoice Proxy { get; init; }

    public required HttpHandler Handler { get; init; }

    public required IAppLogger Logger { get; init; }

    public required Func<long?> SpeedLimitProvider { get; init; }

    public required CancellationToken Cancellation { get; init; }
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~AttemptContextTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/AttemptContext.cs tests/Upload/Pipeline/AttemptContextTests.cs
git commit -m "pipeline(0): add AttemptContext record"
```

---

### Task 0.4: Define `IFileHosterPipeline`

**Files:**
- Create: `src/Upload/Pipeline/IFileHosterPipeline.cs`

No test in this task — interface-only files don't need a dedicated test; they're exercised through their implementations (Phase 2).

- [ ] **Step 1: Write the interface**

```csharp
// src/Upload/Pipeline/IFileHosterPipeline.cs
// <copyright file="IFileHosterPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Per-hoster strategy. Implementations own their auth shape (token, cookie, OAuth, API
/// key, anything) — <see cref="AttemptRunner"/> never inspects credentials beyond passing
/// them in via <see cref="AttemptContext.Credentials"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting concerns the runner has already handled before <see cref="RunAsync"/>:
/// proxy selection, <see cref="Lib.Net.Http.HttpHandler"/> construction, logging hookup,
/// cancellation propagation. Implementations must use <c>ctx.Handler</c> for all HTTP —
/// it is non-null by type and pre-configured with the chosen proxy.
/// </para>
/// <para>
/// Implementations are typically singletons holding per-credentials caches (e.g. a
/// <c>ConcurrentDictionary&lt;int, AuthState&gt;</c> keyed by <c>Credentials.Id</c>) so
/// the same login is reused across files. Cache invalidation on auth failure is the
/// pipeline's responsibility.
/// </para>
/// </remarks>
/// <example>
/// A token-based hoster (Rapidgator-style):
/// <code>
/// var auth = await GetOrLoginAsync(ctx);
/// var folder = await CreateFolderAsync(ctx, auth);
/// await UploadAsync(ctx, auth, folder);
/// yield return new TransferCompleted(url);
/// </code>
/// A cookie-based hoster: stash a <c>CookieContainer</c> in the auth state; reuse on
/// subsequent attempts. The runner doesn't care.
/// </example>
public interface IFileHosterPipeline
{
    /// <summary>Hoster name, must match the key used by <see cref="IFileHosterRegistry"/>.</summary>
    string Name { get; }

    /// <summary>True when the hoster needs the file's content hash before upload (e.g. Rapidgator MD5).</summary>
    bool RequiresHashingBeforeUpload { get; }

    /// <summary>True when the hoster computes a hash post-upload (rare, usually false).</summary>
    bool RequiresHashingAfterUpload { get; }

    /// <summary>
    /// Runs the protocol-specific portion of an upload attempt. Yields events for progress
    /// and outcomes. Must terminate with no more than one of <see cref="TransferCompleted"/>,
    /// <see cref="AttemptFailed"/>, or <see cref="AttemptCancelled"/> — the runner adds the
    /// <see cref="AttemptCompleted"/> envelope itself.
    /// </summary>
    IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Upload/Pipeline/IFileHosterPipeline.cs
git commit -m "pipeline(0): define IFileHosterPipeline contract"
```

---

### Task 0.5: Define `IProxySource`

**Files:**
- Create: `src/Lib/Net/IProxySource.cs`

- [ ] **Step 1: Write the interface**

```csharp
// src/Lib/Net/IProxySource.cs
// <copyright file="IProxySource.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// DI seam over <see cref="ProxyManager"/>. Returns a non-null <see cref="ProxyChoice"/>
/// — "no proxy" is <see cref="ProxyChoice.Direct"/>, never null. Lets <see cref="Upload.Pipeline.AttemptRunner"/>
/// take a constructor dependency without reaching into the global <c>ProxyManager.Current</c>.
/// </summary>
public interface IProxySource
{
    ProxyChoice Next();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Lib/Net/IProxySource.cs
git commit -m "pipeline(0): define IProxySource contract"
```

---

### Task 0.6: Define `IHttpHandlerFactory`

**Files:**
- Create: `src/Lib/Net/Http/IHttpHandlerFactory.cs`

- [ ] **Step 1: Write the interface**

```csharp
// src/Lib/Net/Http/IHttpHandlerFactory.cs
// <copyright file="IHttpHandlerFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Constructs a fresh <see cref="HttpHandler"/> for one upload attempt, baking the chosen
/// proxy and a snapshot of the mock-server config into the resulting client. Returns
/// non-null by contract — direct connections produce a no-proxy <see cref="HttpHandler"/>,
/// not null.
/// </summary>
public interface IHttpHandlerFactory
{
    HttpHandler Create(ProxyChoice proxy, IAppLogger logger);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Lib/Net/Http/IHttpHandlerFactory.cs
git commit -m "pipeline(0): define IHttpHandlerFactory contract"
```

---

### Task 0.7: Add `IFileHosterRegistry` and `DefaultFileHosterRegistry`

**Files:**
- Create: `src/Upload/Pipeline/IFileHosterRegistry.cs`
- Create: `src/Upload/Pipeline/DefaultFileHosterRegistry.cs`
- Test: `tests/Upload/Pipeline/DefaultFileHosterRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/DefaultFileHosterRegistryTests.cs
// <copyright file="DefaultFileHosterRegistryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class DefaultFileHosterRegistryTests
{
    [Fact]
    public void Find_ReturnsRegisteredPipelineByName()
    {
        Mock<IFileHosterPipeline> p = new();
        p.SetupGet(x => x.Name).Returns("Rapidgator");
        DefaultFileHosterRegistry registry = new([p.Object]);

        IFileHosterPipeline? found = registry.Find("Rapidgator");

        Assert.NotNull(found);
        Assert.Same(p.Object, found);
    }

    [Fact]
    public void Find_ReturnsNullWhenUnknown()
    {
        DefaultFileHosterRegistry registry = new([]);

        Assert.Null(registry.Find("DoesNotExist"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        Mock<IFileHosterPipeline> p = new();
        p.SetupGet(x => x.Name).Returns("Rapidgator");
        DefaultFileHosterRegistry registry = new([p.Object]);

        Assert.NotNull(registry.Find("rapidgator"));
        Assert.NotNull(registry.Find("RAPIDGATOR"));
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~DefaultFileHosterRegistryTests"`
Expected: build error.

- [ ] **Step 3: Implement the interface and the registry**

```csharp
// src/Upload/Pipeline/IFileHosterRegistry.cs
// <copyright file="IFileHosterRegistry.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

public interface IFileHosterRegistry
{
    IFileHosterPipeline? Find(string hosterName);
}
```

```csharp
// src/Upload/Pipeline/DefaultFileHosterRegistry.cs
// <copyright file="DefaultFileHosterRegistry.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Default registry constructed from the DI-injected enumerable of pipelines. Each
/// new hoster is registered by adding one DI line; no static factory map needed.
/// </summary>
public sealed class DefaultFileHosterRegistry : IFileHosterRegistry
{
    private readonly Dictionary<string, IFileHosterPipeline> _byName;

    public DefaultFileHosterRegistry(IEnumerable<IFileHosterPipeline> pipelines)
    {
        _byName = pipelines.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IFileHosterPipeline? Find(string hosterName)
        => _byName.TryGetValue(hosterName, out IFileHosterPipeline? p) ? p : null;
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~DefaultFileHosterRegistryTests"`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/IFileHosterRegistry.cs src/Upload/Pipeline/DefaultFileHosterRegistry.cs tests/Upload/Pipeline/DefaultFileHosterRegistryTests.cs
git commit -m "pipeline(0): add IFileHosterRegistry + default impl"
```

---

### Task 0.8: Add `AttemptRunner` skeleton

**Files:**
- Create: `src/Upload/Pipeline/AttemptRunner.cs`
- Test: `tests/Upload/Pipeline/AttemptRunnerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/AttemptRunnerTests.cs
// <copyright file="AttemptRunnerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class AttemptRunnerTests
{
    [Fact]
    public async Task RunAsync_EmitsProxyPicked_HandlerBuilt_PipelineEvents_ThenAttemptCompleted()
    {
        FakeHosterPipeline pipeline = new(success: true, fileUrl: "https://x/y");
        AttemptRunner runner = BuildRunner(pipeline);
        AttemptInputs inputs = MakeInputs();

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.IsType<ProxyPicked>(events[0]);
        Assert.IsType<HandlerBuilt>(events[1]);
        Assert.Contains(events, e => e is TransferStarted);
        Assert.Contains(events, e => e is TransferCompleted);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.True(last.Success);
        Assert.Equal("https://x/y", last.FileUrl);
    }

    [Fact]
    public async Task RunAsync_WhenHosterUnregistered_EmitsAttemptFailedAndAttemptCompletedFalse()
    {
        AttemptRunner runner = BuildRunner(pipelines: []);
        AttemptInputs inputs = MakeInputs() with { HosterName = "UnknownHoster" };

        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in runner.RunAsync(inputs, CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e is AttemptFailed);
        AttemptCompleted last = Assert.IsType<AttemptCompleted>(events[^1]);
        Assert.False(last.Success);
    }

    private static AttemptRunner BuildRunner(params IFileHosterPipeline[] pipelines)
    {
        DefaultFileHosterRegistry registry = new(pipelines);
        Mock<IProxySource> proxySource = new();
        proxySource.Setup(s => s.Next()).Returns(ProxyChoice.Direct);
        Mock<IHttpHandlerFactory> handlerFactory = new();
        handlerFactory.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()));
        return new AttemptRunner(registry, proxySource.Object, handlerFactory.Object);
    }

    private static AttemptInputs MakeInputs() => new()
    {
        FilePath = @"C:\does-not-matter\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "abcd",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 1, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
    };

    private sealed class FakeHosterPipeline(bool success, string fileUrl) : IFileHosterPipeline
    {
        public string Name => "Rapidgator";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            if (success)
            {
                yield return new TransferCompleted(fileUrl);
            }
            else
            {
                yield return new AttemptFailed("synthetic failure", null);
            }
        }
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~AttemptRunnerTests"`
Expected: build error — `AttemptRunner`, `AttemptInputs` don't exist.

- [ ] **Step 3: Implement `AttemptInputs` and `AttemptRunner`**

Add `AttemptInputs` (callers don't have a built `HttpHandler` or chosen `ProxyChoice` yet — those happen inside the runner, so we need a simpler "before context" record):

```csharp
// src/Upload/Pipeline/AttemptInputs.cs
// <copyright file="AttemptInputs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Caller-supplied inputs to <see cref="AttemptRunner.RunAsync"/>. The runner picks the
/// proxy and builds the <see cref="Lib.Net.Http.HttpHandler"/> itself, then promotes
/// these inputs into a full <see cref="AttemptContext"/> for the pipeline.
/// </summary>
public sealed record AttemptInputs
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public string? FileHash { get; init; }
    public required string HosterName { get; init; }
    public required FileHosterLoginDto Credentials { get; init; }
    public required IAppLogger Logger { get; init; }
    public required Func<long?> SpeedLimitProvider { get; init; }
}
```

```csharp
// src/Upload/Pipeline/AttemptRunner.cs
// <copyright file="AttemptRunner.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Outer pipeline orchestrator. One <c>RunAsync</c> call = one upload attempt.
/// Picks proxy → builds handler → invokes hoster pipeline → emits <see cref="AttemptCompleted"/>.
/// </summary>
public sealed class AttemptRunner
{
    private readonly IFileHosterRegistry _registry;
    private readonly IProxySource _proxySource;
    private readonly IHttpHandlerFactory _handlerFactory;

    /// <summary>
    /// Raised after every attempt. <see cref="ProxyManager"/> subscribes here to update
    /// connectivity icons; the old <c>PackageManager.OnFileStateChanged</c> reach-through
    /// to <c>ProxyManager.Current</c> is replaced by this subscription.
    /// </summary>
    public event EventHandler<AttemptCompleted>? AttemptCompleted;

    public AttemptRunner(IFileHosterRegistry registry, IProxySource proxySource, IHttpHandlerFactory handlerFactory)
    {
        _registry = registry;
        _proxySource = proxySource;
        _handlerFactory = handlerFactory;
    }

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptInputs inputs, [EnumeratorCancellation] CancellationToken ct)
    {
        ProxyChoice proxy = _proxySource.Next();
        yield return new ProxyPicked(proxy);

        HttpHandler handler = _handlerFactory.Create(proxy, inputs.Logger);
        yield return new HandlerBuilt(handler);

        IFileHosterPipeline? pipeline = _registry.Find(inputs.HosterName);
        if (pipeline is null)
        {
            string reason = $"No pipeline registered for hoster '{inputs.HosterName}'";
            yield return new AttemptFailed(reason, null);
            AttemptCompleted terminal = new(Success: false, ProxyId: proxy.Id, FileUrl: null);
            yield return terminal;
            this.AttemptCompleted?.Invoke(this, terminal);
            yield break;
        }

        AttemptContext ctx = new()
        {
            AttemptId = Guid.NewGuid(),
            FilePath = inputs.FilePath,
            FileName = inputs.FileName,
            FileSize = inputs.FileSize,
            FileHash = inputs.FileHash,
            HosterName = inputs.HosterName,
            Credentials = inputs.Credentials,
            Proxy = proxy,
            Handler = handler,
            Logger = inputs.Logger,
            SpeedLimitProvider = inputs.SpeedLimitProvider,
            Cancellation = ct,
        };

        bool success = false;
        string? finalUrl = null;
        Exception? failure = null;

        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, ct))
        {
            yield return ev;

            switch (ev)
            {
                case TransferCompleted tc:
                    success = true;
                    finalUrl = tc.FileUrl;
                    break;
                case AttemptFailed af:
                    failure = af.Exception;
                    break;
                case AttemptCancelled:
                    break;
            }
        }

        AttemptCompleted finalEvent = new(Success: success, ProxyId: proxy.Id, FileUrl: finalUrl);
        yield return finalEvent;
        this.AttemptCompleted?.Invoke(this, finalEvent);
        _ = failure; // reserved for richer reporting once all hosters wire AttemptFailed
    }
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~AttemptRunnerTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/AttemptInputs.cs src/Upload/Pipeline/AttemptRunner.cs tests/Upload/Pipeline/AttemptRunnerTests.cs
git commit -m "pipeline(0): add AttemptRunner skeleton with proxy + handler stages"
```

---

## Phase 1: Wire infrastructure (no scheduler change yet)

Phase 1 implements the real `IProxySource`, `IHttpHandlerFactory`, and `MockServerConfig`, then registers them in DI. Existing upload code is unaffected.

### Task 1.1: Extract `MockServerConfig` from `AppSettings`

**Files:**
- Create: `src/Lib/Net/Http/MockServerConfig.cs`
- Test: `tests/Lib/Net/Http/MockServerConfigTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Lib/Net/Http/MockServerConfigTests.cs
// <copyright file="MockServerConfigTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Upload;

namespace CSUploader.Tests.Lib.Net.Http;

public class MockServerConfigTests
{
    [Fact]
    public void FromAppSettings_CapturesEnabledAndBaseUrl()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://localhost:8080" };

        MockServerConfig snap = MockServerConfig.FromAppSettings(settings);

        Assert.True(snap.Enabled);
        Assert.Equal("http://localhost:8080", snap.BaseUrl);
    }

    [Fact]
    public void Disabled_HasEnabledFalseAndEmptyBaseUrl()
    {
        Assert.False(MockServerConfig.Disabled.Enabled);
        Assert.Equal(string.Empty, MockServerConfig.Disabled.BaseUrl);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~MockServerConfigTests"`
Expected: build error.

- [ ] **Step 3: Implement the record**

```csharp
// src/Lib/Net/Http/MockServerConfig.cs
// <copyright file="MockServerConfig.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Snapshot of the mock-server portion of <see cref="AppSettings"/> taken at handler-build
/// time. Removes <see cref="HttpHandler"/>'s read of <c>AppSettings.Current</c> — by the
/// time the handler is constructed, the runtime decision is frozen on the snapshot.
/// </summary>
public sealed record MockServerConfig(bool Enabled, string BaseUrl)
{
    public static MockServerConfig Disabled { get; } = new(false, string.Empty);

    public static MockServerConfig FromAppSettings(AppSettings settings)
        => new(settings.UseMockServer, settings.MockServerBaseUrl ?? string.Empty);
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~MockServerConfigTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Lib/Net/Http/MockServerConfig.cs tests/Lib/Net/Http/MockServerConfigTests.cs
git commit -m "pipeline(1): extract MockServerConfig from AppSettings"
```

---

### Task 1.2: Refactor `HttpHandler` to take `MockServerConfig`

**Files:**
- Modify: `src/Lib/Net/Http/HttpHandler.cs`
- Test: `tests/Lib/Net/Http/HttpHandlerMockRewriteTests.cs` (new)

This change is *additive* — keep the existing constructor signature and add an overload taking `MockServerConfig`. Migrate callers in subsequent tasks. The old constructor delegates to the new one with `MockServerConfig.FromAppSettings(AppSettings.Current)`. After Phase 4 deletes the old callers, the legacy ctor is removed.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Lib/Net/Http/HttpHandlerMockRewriteTests.cs
// <copyright file="HttpHandlerMockRewriteTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

public class HttpHandlerMockRewriteTests
{
    [Fact]
    public void Ctor_WithMockServerConfig_DoesNotReadAppSettingsCurrent()
    {
        // The new ctor takes the snapshot directly; AppSettings.Current is irrelevant here.
        MockServerConfig snap = new(true, "http://localhost:9999");
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), proxyDescription: null, mockServer: snap);

        Assert.Equal(snap, handler.MockServerSnapshot);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~HttpHandlerMockRewriteTests"`
Expected: build error — `MockServerSnapshot` doesn't exist; new ctor doesn't exist.

- [ ] **Step 3: Add the new constructor and snapshot field**

Edit `src/Lib/Net/Http/HttpHandler.cs`:

Replace the primary constructor declaration:

```csharp
public class HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription = null, bool bypassMockServer = false)
{
    private readonly IAppLogger _logger = logger;
    private readonly string _proxyDescription = string.IsNullOrEmpty(proxyDescription) ? "(direct)" : proxyDescription;
    private readonly bool _bypassMockServer = bypassMockServer;
```

with:

```csharp
public class HttpHandler
{
    private readonly IAppLogger _logger;
    private readonly string _proxyDescription;
    private readonly bool _bypassMockServer;
    private readonly MockServerConfig _mockServer;

    /// <summary>
    /// Legacy ctor — reads <see cref="AppSettings.Current"/> for the mock snapshot.
    /// New code should pass an explicit <see cref="MockServerConfig"/> instead. Kept
    /// during the pipeline migration; deleted in Phase 4.
    /// </summary>
    public HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription = null, bool bypassMockServer = false)
        : this(httpclient, logger, proxyDescription, MockServerConfig.FromAppSettings(AppSettings.Current), bypassMockServer)
    {
    }

    public HttpHandler(HttpClient httpclient, IAppLogger logger, string? proxyDescription, MockServerConfig mockServer, bool bypassMockServer = false)
    {
        HttpClient = httpclient;
        _logger = logger;
        _proxyDescription = string.IsNullOrEmpty(proxyDescription) ? "(direct)" : proxyDescription;
        _bypassMockServer = bypassMockServer;
        _mockServer = mockServer;
    }

    /// <summary>Test-observable snapshot of the mock config locked in at construction.</summary>
    internal MockServerConfig MockServerSnapshot => _mockServer;
```

Then change `MaybeRewriteToMockServer` to read the snapshot:

```csharp
    private string MaybeRewriteToMockServer(string url)
    {
        if (_bypassMockServer)
        {
            return url;
        }

        if (!_mockServer.Enabled || string.IsNullOrEmpty(_mockServer.BaseUrl))
        {
            _logger.Log(this, LogType.Status, $"Mock server disabled — sending to live URL: {url}");
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? originalUri))
        {
            return url;
        }

        if (!Uri.TryCreate(_mockServer.BaseUrl, UriKind.Absolute, out Uri? mockUri))
        {
            return url;
        }

        if (string.Equals(originalUri.Host, mockUri.Host, StringComparison.OrdinalIgnoreCase)
            && originalUri.Port == mockUri.Port)
        {
            return url;
        }

        string host = originalUri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        int firstDot = host.IndexOf('.', StringComparison.Ordinal);
        string slug = (firstDot > 0 ? host[..firstDot] : host).ToLowerInvariant();

        string mockBase = _mockServer.BaseUrl.TrimEnd('/');
        string rewritten = $"{mockBase}/{slug}{originalUri.PathAndQuery}";
        _logger.Log(this, LogType.Status, $"Mock rewrite: {url} -> {rewritten}");
        return rewritten;
    }
```

Finally, replace the `protected HttpClient HttpClient { get; set; } = httpclient;` line with `protected HttpClient HttpClient { get; }` (it's now assigned in the ctor body).

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass; the new test included; pre-existing tests unaffected.

- [ ] **Step 5: Commit**

```bash
git add src/Lib/Net/Http/HttpHandler.cs tests/Lib/Net/Http/HttpHandlerMockRewriteTests.cs
git commit -m "pipeline(1): HttpHandler accepts MockServerConfig snapshot"
```

---

### Task 1.3: `ProxyManagerSource` adapter

**Files:**
- Modify: `src/Lib/Net/ProxyManager.cs` — implement `IProxySource`
- Test: `tests/Lib/Net/ProxyManagerSourceTests.cs` (new)

`ProxyManager` already has `NextProxy()` returning `ProxySettingDto?`. Implement `IProxySource` as a thin wrapper that maps null → `ProxyChoice.Direct` and non-null → built `WebProxy`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Lib/Net/ProxyManagerSourceTests.cs
// <copyright file="ProxyManagerSourceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Lib.Net;

public class ProxyManagerSourceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IDbContextFactory<CSUploaderDbContext> _factory;

    public ProxyManagerSourceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>().UseSqlite(_conn).Options;
        _factory = new Factory(options);
        using CSUploaderDbContext db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() { _conn.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void Next_WithNoProxiesEnabled_ReturnsDirect()
    {
        AppSettings.Current = new AppSettings { ProxiesEnabled = true };
        ProxyManager manager = new(new ProxySettingRepository(_factory), Mock.Of<IAppLogger>());

        ProxyChoice choice = ((IProxySource)manager).Next();

        Assert.Same(ProxyChoice.Direct, choice);
    }

    private sealed class Factory(DbContextOptions<CSUploaderDbContext> options) : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ProxyManagerSourceTests"`
Expected: build error — `ProxyManager` doesn't implement `IProxySource`.

- [ ] **Step 3: Add `IProxySource` implementation**

In `src/Lib/Net/ProxyManager.cs`, change the class declaration:

```csharp
public class ProxyManager : IProxySource
```

Add at the bottom of the class (just before the closing `}` of `ProxyManager`):

```csharp
    /// <summary>
    /// <see cref="IProxySource"/> implementation. Adapts the existing nullable rotation
    /// to the non-null <see cref="ProxyChoice"/> world: null becomes <see cref="ProxyChoice.Direct"/>.
    /// </summary>
    ProxyChoice IProxySource.Next()
    {
        ProxySettingDto? next = NextProxy();
        if (next is null)
        {
            return ProxyChoice.Direct;
        }

        IWebProxy? webProxy = BuildWebProxy(next);
        string description = $"{next.Type.ToString().ToLowerInvariant()}://{next.Host}:{next.Port}";
        return new ProxyChoice(next.Id, webProxy, description);
    }
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~ProxyManagerSourceTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Lib/Net/ProxyManager.cs tests/Lib/Net/ProxyManagerSourceTests.cs
git commit -m "pipeline(1): ProxyManager implements IProxySource"
```

---

### Task 1.4: `DefaultHttpHandlerFactory`

**Files:**
- Create: `src/Lib/Net/Http/DefaultHttpHandlerFactory.cs`
- Test: `tests/Lib/Net/Http/DefaultHttpHandlerFactoryTests.cs`

The factory body is the same as today's `RapidgatorClient.BuildHttpHandler` (`src/Upload/RapidgatorClient.cs:90`) but takes `ProxyChoice` (already-resolved) instead of reaching into `ProxyManager.Current` itself.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Lib/Net/Http/DefaultHttpHandlerFactoryTests.cs
// <copyright file="DefaultHttpHandlerFactoryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

public class DefaultHttpHandlerFactoryTests
{
    [Fact]
    public void Create_ReturnsNonNullHandler()
    {
        AppSettings settings = new();
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.NotNull(handler); // by signature; this asserts type only
    }

    [Fact]
    public void Create_BakesMockServerSnapshotFromCurrentSettings()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://mock:9000" };
        DefaultHttpHandlerFactory factory = new(settings);

        HttpHandler handler = factory.Create(ProxyChoice.Direct, Mock.Of<IAppLogger>());

        Assert.True(handler.MockServerSnapshot.Enabled);
        Assert.Equal("http://mock:9000", handler.MockServerSnapshot.BaseUrl);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~DefaultHttpHandlerFactoryTests"`
Expected: build error.

- [ ] **Step 3: Implement the factory**

```csharp
// src/Lib/Net/Http/DefaultHttpHandlerFactory.cs
// <copyright file="DefaultHttpHandlerFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;

namespace CSUploader.Lib.Net.Http;

public sealed class DefaultHttpHandlerFactory : IHttpHandlerFactory
{
    private readonly AppSettings _settings;

    public DefaultHttpHandlerFactory(AppSettings settings)
    {
        _settings = settings;
    }

    public HttpHandler Create(ProxyChoice proxy, IAppLogger logger)
    {
        HttpClientHandler clientHandler = new()
        {
            AllowAutoRedirect = false,
        };

        if (proxy.WebProxy is not null)
        {
            clientHandler.Proxy = proxy.WebProxy;
            clientHandler.UseProxy = true;
        }
        else
        {
            clientHandler.UseProxy = false;
        }

        // Per-attempt timeout: the request itself has its own cancellation; the client-level
        // timeout is generous to allow long uploads while a stuck connection still gets killed.
        HttpClient client = new(clientHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        MockServerConfig snap = MockServerConfig.FromAppSettings(_settings);
        return new HttpHandler(client, logger, proxy.Description, snap);
    }
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~DefaultHttpHandlerFactoryTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Lib/Net/Http/DefaultHttpHandlerFactory.cs tests/Lib/Net/Http/DefaultHttpHandlerFactoryTests.cs
git commit -m "pipeline(1): add DefaultHttpHandlerFactory"
```

---

### Task 1.5: Register new services in DI

**Files:**
- Modify: `src/App.xaml.cs` — add three registrations

Add the new services to the DI graph **without** removing existing registrations. `AttemptRunner` is registered as a singleton because it owns the registry and the `AttemptCompleted` event subscribers.

- [ ] **Step 1: Read the existing `ConfigureServices` method**

Run: `grep -n "ConfigureServices\|AddSingleton<ProxyManager>\|AddSingleton<AppSettings>" src/App.xaml.cs`

Identify where `ProxyManager` is registered.

- [ ] **Step 2: Edit `App.xaml.cs`**

Inside `ConfigureServices`, after the `services.AddSingleton<ProxyManager>(...)` line, add:

```csharp
        // Pipeline infrastructure (Phase 1 wiring; not yet on the upload hot path)
        services.AddSingleton<Lib.Net.IProxySource>(sp => sp.GetRequiredService<Lib.Net.ProxyManager>());
        services.AddSingleton<Lib.Net.Http.IHttpHandlerFactory>(sp => new Lib.Net.Http.DefaultHttpHandlerFactory(sp.GetRequiredService<Upload.AppSettings>()));
        services.AddSingleton<Upload.Pipeline.IFileHosterRegistry>(sp => new Upload.Pipeline.DefaultFileHosterRegistry(sp.GetServices<Upload.Pipeline.IFileHosterPipeline>()));
        services.AddSingleton<Upload.Pipeline.AttemptRunner>();
```

(Adjust namespaces to match the existing import style — verify with the surrounding lines.)

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass. No behavioural change.

- [ ] **Step 4: Smoke-run the app**

Run: `dotnet run --project src/CSUploader.csproj`
Expected: app starts; `ConfigureServices` resolves all dependencies; no DI exceptions on startup.

- [ ] **Step 5: Commit**

```bash
git add src/App.xaml.cs
git commit -m "pipeline(1): register IProxySource / IHttpHandlerFactory / AttemptRunner in DI"
```

---

## Phase 2: `RapidgatorPipeline` (parallel to old client)

Phase 2 implements the new pipeline for Rapidgator. Old `RapidgatorClient` keeps running in production until Phase 3 flips the switch.

### Task 2.1: `RapidgatorAuthState` cache record

**Files:**
- Create: `src/Upload/Pipeline/Hosters/RapidgatorAuthState.cs`
- Test: `tests/Upload/Pipeline/Hosters/RapidgatorAuthStateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/Hosters/RapidgatorAuthStateTests.cs
// <copyright file="RapidgatorAuthStateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorAuthStateTests
{
    [Fact]
    public void Authenticated_HoldsTokenAndUserInfo()
    {
        RapidgatorAuthState state = new(Token: "tok", PrimaryFolderId: 42);

        Assert.Equal("tok", state.Token);
        Assert.Equal(42, state.PrimaryFolderId);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RapidgatorAuthStateTests"`
Expected: build error.

- [ ] **Step 3: Implement the record**

```csharp
// src/Upload/Pipeline/Hosters/RapidgatorAuthState.cs
// <copyright file="RapidgatorAuthState.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Per-credentials authenticated session for Rapidgator. Cached inside <see cref="RapidgatorPipeline"/>
/// keyed by <see cref="Dal.FileHosterLoginDto.Id"/> so files for the same account skip the login round-trip.
/// </summary>
internal sealed record RapidgatorAuthState(string Token, int PrimaryFolderId);
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~RapidgatorAuthStateTests"`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/Hosters/RapidgatorAuthState.cs tests/Upload/Pipeline/Hosters/RapidgatorAuthStateTests.cs
git commit -m "pipeline(2): add RapidgatorAuthState cache record"
```

---

### Task 2.2: `RapidgatorPipeline` skeleton + auth flow

**Files:**
- Create: `src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs`
- Test: `tests/Upload/Pipeline/Hosters/RapidgatorPipelineAuthTests.cs`

This task builds out **only** the auth flow: given an `AttemptContext`, log in (or reuse cached state) and yield `AuthStarted` / `AuthSucceeded` / `AuthFailed`. Folder + upload come in Tasks 2.3 and 2.4.

The pipeline must not call `ctx.Handler.GetStringAsync` directly in tests — wrap it via an injected delegate so we can fake responses.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/Hosters/RapidgatorPipelineAuthTests.cs
// <copyright file="RapidgatorPipelineAuthTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

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
        List<UploadEvent> events = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Contains(events, e => e is AuthStarted);
        Assert.Contains(events, e => e is AuthSucceeded);
    }

    [Fact]
    public async Task RunAsync_SecondCallSameCredentials_ReusesAuthAndSkipsLogin()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));
        List<UploadEvent> second = await CollectAsync(pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.DoesNotContain(second, e => e is AuthStarted);
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
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
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
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RapidgatorPipelineAuthTests"`
Expected: build error — `RapidgatorPipeline` doesn't exist.

- [ ] **Step 3: Implement the auth path**

```csharp
// src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs
// <copyright file="RapidgatorPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CSUploader.Lib.Extensions;

namespace CSUploader.Upload.Pipeline.Hosters;

public sealed class RapidgatorPipeline : IFileHosterPipeline
{
    private readonly ConcurrentDictionary<int, RapidgatorAuthState> _authByCredentialsId = new();
    private readonly Func<string, Task<string>>? _httpOverride;

    /// <summary>Production ctor — uses the <see cref="AttemptContext.Handler"/> for HTTP.</summary>
    public RapidgatorPipeline()
    {
    }

    /// <summary>Test ctor — substitutes a synchronous responder for HTTP. Synchronous body kept in a Task wrapper.</summary>
    internal RapidgatorPipeline(Func<string, string> httpOverride)
    {
        _httpOverride = url => Task.FromResult(httpOverride(url));
    }

    public string Name => "Rapidgator";
    public bool RequiresHashingBeforeUpload => true;
    public bool RequiresHashingAfterUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // === Auth ===
        if (!_authByCredentialsId.TryGetValue(ctx.Credentials.Id, out RapidgatorAuthState? auth))
        {
            yield return new AuthStarted();

            (RapidgatorAuthState? newAuth, string? error) = await LoginAsync(ctx);
            if (newAuth is null)
            {
                yield return new AuthFailed(error ?? "login returned no token");
                yield return new AttemptFailed(error ?? "login failed", null);
                yield break;
            }

            _authByCredentialsId[ctx.Credentials.Id] = newAuth;
            auth = newAuth;
            yield return new AuthSucceeded();
        }

        // Folder + upload come in Tasks 2.3 and 2.4. For now, terminate the attempt cleanly
        // so this task's tests pass without requiring later-task code.
        yield return new TransferCompleted("about:blank");
    }

    private async Task<(RapidgatorAuthState?, string?)> LoginAsync(AttemptContext ctx)
    {
        string url = $"https://www.rapidgator.net/api/v2/user/login"
            + $"?login={Uri.EscapeDataString(ctx.Credentials.Username ?? string.Empty)}"
            + $"&password={Uri.EscapeDataString(ctx.Credentials.Password ?? string.Empty)}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out LoginEnvelope? env) || env?.Status != 200 || env.Response is null)
        {
            return (null, env?.Details ?? "login failed");
        }

        return (new RapidgatorAuthState(env.Response.Token, env.Response.User?.FolderId ?? 0), null);
    }

    private Task<string> GetAsync(AttemptContext ctx, string url)
        => _httpOverride is not null ? _httpOverride(url) : ctx.Handler.GetStringAsync(url, ctx.Cancellation);

    private sealed class LoginEnvelope
    {
        [JsonPropertyName("response")] public LoginResponse? Response { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class LoginResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("user")] public LoginUser? User { get; set; }
    }

    private sealed class LoginUser
    {
        [JsonPropertyName("folder_id")] public int FolderId { get; set; }
    }
}
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~RapidgatorPipelineAuthTests"`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs tests/Upload/Pipeline/Hosters/RapidgatorPipelineAuthTests.cs
git commit -m "pipeline(2): RapidgatorPipeline auth flow with per-credentials cache"
```

---

### Task 2.3: `RapidgatorPipeline` folder creation

**Files:**
- Modify: `src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs`
- Test: `tests/Upload/Pipeline/Hosters/RapidgatorPipelineFolderTests.cs`

After successful auth, create (or reuse) a folder named after the package directory. Today's `RapidgatorClient.HttpCreateFolderAsync` (`src/Upload/RapidgatorClient.cs`, search for `folder/create`) is the reference.

- [ ] **Step 1: Write the failing test**

Add a test that pre-stages a login response then a folder/create response, and asserts the pipeline yields a `TransferStarted` (proving folder succeeded — we'll bridge to upload in 2.4).

```csharp
// tests/Upload/Pipeline/Hosters/RapidgatorPipelineFolderTests.cs
// <copyright file="RapidgatorPipelineFolderTests.cs" company="CSUploader">
// (header omitted for brevity — match the standard MIT header)
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorPipelineFolderTests
{
    [Fact]
    public async Task RunAsync_AfterAuth_CreatesFolderAndProceedsToTransfer()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"package1","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
            if (ev is TransferStarted) break; // stop at the bridge into transfer; full transfer in Task 2.4
        }

        Assert.Contains(events, e => e is TransferStarted);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RapidgatorPipelineFolderTests"`
Expected: failure — currently the pipeline emits `TransferCompleted("about:blank")` immediately, no `TransferStarted`.

- [ ] **Step 3: Add the folder-create call to `RapidgatorPipeline.RunAsync`**

Replace the placeholder `yield return new TransferCompleted("about:blank");` line with:

```csharp
        // === Folder ===
        string folderName = ResolveFolderName(ctx.FilePath);
        (int? folderId, string? folderError) = await CreateFolderAsync(ctx, auth!, folderName);
        if (folderId is null)
        {
            yield return new AttemptFailed(folderError ?? "folder/create failed", null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Transfer comes in Task 2.4. Bridge to a stub success for now so this task's test passes.
        yield return new TransferCompleted("about:blank");
```

Add the helpers below the `LoginAsync` method:

```csharp
    private static string ResolveFolderName(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        return string.IsNullOrEmpty(dir) ? "uploads" : new DirectoryInfo(dir).Name;
    }

    private async Task<(int? FolderId, string? Error)> CreateFolderAsync(AttemptContext ctx, RapidgatorAuthState auth, string folderName)
    {
        string url = $"https://www.rapidgator.net/api/v2/folder/create"
            + $"?name={Uri.EscapeDataString(folderName)}"
            + $"&parent_folder_id={auth.PrimaryFolderId}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out FolderEnvelope? env) || env?.Status != 200 || env.Response?.Folder is null)
        {
            return (null, env?.Details ?? "folder/create failed");
        }

        return (env.Response.Folder.Id, null);
    }

    private sealed class FolderEnvelope
    {
        [JsonPropertyName("response")] public FolderResponseBody? Response { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("details")] public string? Details { get; set; }
    }

    private sealed class FolderResponseBody
    {
        [JsonPropertyName("folder")] public FolderDetail? Folder { get; set; }
    }

    private sealed class FolderDetail
    {
        [JsonPropertyName("folder_id")] public int Id { get; set; }
    }
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~RapidgatorPipelineFolderTests"`
Expected: 1 passed (and the auth tests still pass).

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs tests/Upload/Pipeline/Hosters/RapidgatorPipelineFolderTests.cs
git commit -m "pipeline(2): RapidgatorPipeline folder creation"
```

---

### Task 2.4: `RapidgatorPipeline` upload flow

**Files:**
- Modify: `src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs`
- Test: `tests/Upload/Pipeline/Hosters/RapidgatorPipelineUploadTests.cs`

The upload flow has three steps:
1. POST `/api/v2/file/upload?folder_id&hash&size&token` → returns `upload_url` and `upload_id`.
2. PUT/multipart-POST the file bytes to `upload_url` (this is what `HttpHandler.UploadFileAsync` does).
3. POST `/api/v2/file/upload_info?upload_id&token` to confirm the upload and retrieve the public URL.

For testability, the pipeline takes a delegate for the multipart upload that wraps `HttpHandler.UploadFileAsync` and emits progress.

The full RapidgatorPipeline test in this task uses a small fake that emits a synthetic progress event and a successful upload-finished result.

Because of length, this task's full test+impl is folded into a single "implement the rest of the pipeline" step. See the existing `RapidgatorClient.HttpUploadFileAsync` for the exact API shape.

- [ ] **Step 1: Write the failing test (full happy path)**

```csharp
// tests/Upload/Pipeline/Hosters/RapidgatorPipelineUploadTests.cs
// <copyright file="RapidgatorPipelineUploadTests.cs" company="CSUploader">
// (header omitted)
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorPipelineUploadTests
{
    [Fact]
    public async Task RunAsync_HappyPath_EndsInTransferCompletedWithUrl()
    {
        Queue<string> responses = new(new[]
        {
            // login
            """{"response":{"token":"TOK","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            // folder/create
            """{"response":{"folder":{"folder_id":"8676913","mode":0,"mode_label":"Public","parent_folder_id":"5973665","name":"package1","url":"https://r/folder/8676913","nb_folders":0,"nb_files":0,"size_files":0,"created":1778221286,"folders":[]}},"status":200,"details":null}""",
            // file/upload — returns the upload_url + upload_id
            """{"response":{"upload":{"upload_id":"U1","url":"https://upload.rapidgator/post"}},"status":200,"details":null}""",
            // file/upload_info — confirms upload, returns public file url
            """{"response":{"upload":{"file":{"url":"https://r.net/file/abc123"}}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(
            getOverride: url => responses.Dequeue(),
            uploadOverride: (filePath, link, _) => Task.CompletedTask);

        AttemptContext ctx = MakeContext();
        List<UploadEvent> events = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx, CancellationToken.None))
        {
            events.Add(ev);
        }

        TransferCompleted tc = Assert.Single(events.OfType<TransferCompleted>());
        Assert.Equal("https://r.net/file/abc123", tc.FileUrl);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RapidgatorPipelineUploadTests"`
Expected: build error — `RapidgatorPipeline` second test ctor doesn't exist; the upload step is unimplemented.

- [ ] **Step 3: Add the upload steps and the test ctor with `uploadOverride`**

Add a second internal test ctor:

```csharp
    /// <summary>Test ctor — substitutes both GET and multipart upload behaviour.</summary>
    internal RapidgatorPipeline(Func<string, string> getOverride, Func<string, string, Func<long?>?, Task> uploadOverride)
    {
        _httpOverride = url => Task.FromResult(getOverride(url));
        _uploadOverride = uploadOverride;
    }

    private readonly Func<string, string, Func<long?>?, Task>? _uploadOverride;
```

Then in `RunAsync`, replace the placeholder `yield return new TransferCompleted("about:blank");` with the full upload + confirm flow:

```csharp
        // === File upload request → upload_url + upload_id ===
        (string? uploadUrl, string? uploadId, string? upError) = await GetUploadUrlAsync(ctx, auth!, folderId.Value);
        if (uploadUrl is null || uploadId is null)
        {
            yield return new AttemptFailed(upError ?? "file/upload failed", null);
            yield break;
        }

        // === Multipart upload bytes ===
        try
        {
            await UploadBytesAsync(ctx, uploadUrl);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            yield return new AttemptCancelled();
            yield break;
        }
        catch (Exception ex)
        {
            yield return new AttemptFailed(ex.Message, ex);
            yield break;
        }

        // === Upload info → public URL ===
        (string? fileUrl, string? infoError) = await GetUploadInfoAsync(ctx, auth!, uploadId);
        if (fileUrl is null)
        {
            yield return new AttemptFailed(infoError ?? "file/upload_info failed", null);
            yield break;
        }

        yield return new TransferCompleted(fileUrl);
```

Add the three helpers (with their JSON envelopes following the `LoginEnvelope` pattern):

```csharp
    private async Task<(string?, string?, string?)> GetUploadUrlAsync(AttemptContext ctx, RapidgatorAuthState auth, int folderId)
    {
        string url = $"https://www.rapidgator.net/api/v2/file/upload"
            + $"?folder_id={folderId}"
            + $"&name={Uri.EscapeDataString(ctx.FileName)}"
            + $"&hash={ctx.FileHash}"
            + $"&size={ctx.FileSize}"
            + $"&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out UploadUrlEnvelope? env) || env?.Status != 200 || env.Response?.Upload is null)
        {
            return (null, null, env?.Details ?? "file/upload failed");
        }

        return (env.Response.Upload.Url, env.Response.Upload.UploadId, null);
    }

    private Task UploadBytesAsync(AttemptContext ctx, string uploadUrl)
        => _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, uploadUrl, ctx.SpeedLimitProvider)
            : ctx.Handler.UploadFileAsync(ctx.FilePath, uploadUrl, ctx.SpeedLimitProvider, ctx.Cancellation);

    private async Task<(string?, string?)> GetUploadInfoAsync(AttemptContext ctx, RapidgatorAuthState auth, string uploadId)
    {
        string url = $"https://www.rapidgator.net/api/v2/file/upload_info?upload_id={uploadId}&token={auth.Token}";
        string body = await GetAsync(ctx, url);

        if (!JsonHelpers.TryDeserializeObject(body, out UploadInfoEnvelope? env) || env?.Status != 200 || env.Response?.Upload?.File?.Url is null)
        {
            return (null, env?.Details ?? "file/upload_info failed");
        }

        return (env.Response.Upload.File.Url, null);
    }

    private sealed class UploadUrlEnvelope
    {
        [JsonPropertyName("response")] public UploadUrlResponse? Response { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("details")] public string? Details { get; set; }
    }
    private sealed class UploadUrlResponse
    {
        [JsonPropertyName("upload")] public UploadUrl? Upload { get; set; }
    }
    private sealed class UploadUrl
    {
        [JsonPropertyName("upload_id")] public string UploadId { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    }

    private sealed class UploadInfoEnvelope
    {
        [JsonPropertyName("response")] public UploadInfoResponse? Response { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("details")] public string? Details { get; set; }
    }
    private sealed class UploadInfoResponse
    {
        [JsonPropertyName("upload")] public UploadInfoUpload? Upload { get; set; }
    }
    private sealed class UploadInfoUpload
    {
        [JsonPropertyName("file")] public UploadInfoFile? File { get; set; }
    }
    private sealed class UploadInfoFile
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
```

- [ ] **Step 4: Build and re-run all tests**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass; new upload test included.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs tests/Upload/Pipeline/Hosters/RapidgatorPipelineUploadTests.cs
git commit -m "pipeline(2): RapidgatorPipeline upload + confirm flow"
```

---

### Task 2.5: Auth-failure invalidation

**Files:**
- Modify: `src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs`
- Test: `tests/Upload/Pipeline/Hosters/RapidgatorPipelineRetryTests.cs`

If a folder/upload/upload_info call returns `status: 401` (token expired), invalidate the cached auth state. The next attempt re-logs in. We don't auto-retry within the same attempt — the scheduler's retry handler triggers a fresh attempt that builds a new context.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/Pipeline/Hosters/RapidgatorPipelineRetryTests.cs
// (header omitted)

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorPipelineRetryTests
{
    [Fact]
    public async Task FolderCreate401_InvalidatesCache_NextAttemptLogsInAgain()
    {
        Queue<string> responses = new(new[]
        {
            // attempt 1: login OK
            """{"response":{"token":"TOK1","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
            // attempt 1: folder/create returns 401
            """{"response":null,"status":401,"details":"unauthorized"}""",
            // attempt 2: login again (cache was invalidated)
            """{"response":{"token":"TOK2","user":{"folder_id":"5973665"}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AttemptContext ctx1 = MakeContext();
        await foreach (UploadEvent _ in pipeline.RunAsync(ctx1, CancellationToken.None)) { /* drain */ }

        AttemptContext ctx2 = MakeContext();
        List<UploadEvent> attempt2 = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx2, CancellationToken.None))
        {
            attempt2.Add(ev);
            if (ev is AuthSucceeded) break;
        }

        Assert.Contains(attempt2, e => e is AuthStarted);
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = @"C:\nope\package1\x.zip",
        FileName = "x.zip",
        FileSize = 100,
        FileHash = "deadbeef",
        HosterName = "Rapidgator",
        Credentials = new FileHosterLoginDto { Id = 9, FileHosterName = "Rapidgator", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~RapidgatorPipelineRetryTests"`
Expected: failure — the second attempt currently reuses the cached token and skips `AuthStarted`.

- [ ] **Step 3: Invalidate the cache on 401**

In `CreateFolderAsync`, change the failure branch to detect `status == 401` and invalidate. Easiest: have all helpers return the parsed envelope so the orchestration code can inspect `status`.

A clean approach: introduce a private helper that throws a sentinel `AuthExpired` exception on 401, caught in `RunAsync` and used to invalidate the cache.

Add near the top of `RapidgatorPipeline`:

```csharp
    private sealed class AuthExpiredException : Exception { }
```

Adjust `CreateFolderAsync`, `GetUploadUrlAsync`, `GetUploadInfoAsync` to throw `AuthExpiredException` when `env?.Status == 401`. Then in `RunAsync`, wrap each call in a try/catch:

```csharp
        try
        {
            // existing folder + upload + info flow
        }
        catch (AuthExpiredException)
        {
            _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
            yield return new AuthFailed("token expired");
            yield return new AttemptFailed("token expired — retry will re-authenticate", null);
            yield break;
        }
```

(C# disallows `yield return` inside `catch`, so structure the cleanup as a flag set inside `catch` and the yields after the try block. Sketch:

```csharp
        bool authExpired = false;
        // try { ... folder/upload/info logic ... } catch (AuthExpiredException) { authExpired = true; }
        if (authExpired)
        {
            _authByCredentialsId.TryRemove(ctx.Credentials.Id, out _);
            yield return new AuthFailed("token expired");
            yield return new AttemptFailed("token expired — retry will re-authenticate", null);
            yield break;
        }
```

The full code for this restructuring is omitted here — it's mechanical. Pattern: hoist results into local vars inside try, branch on `authExpired` after.)

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/Pipeline/Hosters/RapidgatorPipeline.cs tests/Upload/Pipeline/Hosters/RapidgatorPipelineRetryTests.cs
git commit -m "pipeline(2): invalidate Rapidgator auth on 401"
```

---

### Task 2.6: Register `RapidgatorPipeline` in DI

**Files:**
- Modify: `src/App.xaml.cs`

- [ ] **Step 1: Add the registration**

In `ConfigureServices`, after the `AddSingleton<Upload.Pipeline.AttemptRunner>()` line, add:

```csharp
        services.AddSingleton<Upload.Pipeline.IFileHosterPipeline, Upload.Pipeline.Hosters.RapidgatorPipeline>();
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass.

- [ ] **Step 3: Smoke-run the app**

Run: `dotnet run --project src/CSUploader.csproj`
Expected: starts cleanly. The pipeline is registered but unused — uploads still go through the old `RapidgatorClient`.

- [ ] **Step 4: Commit**

```bash
git add src/App.xaml.cs
git commit -m "pipeline(2): register RapidgatorPipeline as IFileHosterPipeline"
```

---

## Phase 3: Wire `AttemptRunner` into the scheduler

Phase 3 switches `UploadScheduler.LaunchUpload` from calling `file.FileHoster.UploadAsync` to calling `AttemptRunner.RunAsync`. The old `RapidgatorClient` keeps existing for hashing only (Phase 4 removes it).

### Task 3.1: `PackageFile` consumes a pipeline event stream

**Files:**
- Modify: `src/Upload/PackageFile.cs`

Add a public `ApplyEvent(UploadEvent ev)` method that pattern-matches and updates state. The four old event handlers (`FileHoster_UploadProgress`, etc.) stay for now — they handle hashing during the migration.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Upload/PackageFilePipelineEventsTests.cs
// (header omitted)

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackageFilePipelineEventsTests
{
    [Fact]
    public void ApplyEvent_TransferProgress_UpdatesProgressFields()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 100, SpeedBytesPerSec: 1024));

        Assert.Equal(50, file.BytesLoaded);
        Assert.Equal(50, file.BytesRemaining);
        Assert.Equal(50.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_TransferCompleted_SetsFinishedAndUrl()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferCompleted("https://x/y"));

        Assert.True(file.IsUploadFinished);
        Assert.Equal("https://x/y", file.FileUrl);
        Assert.Equal(100.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_AttemptFailed_SetsError()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new AttemptFailed("network down", null));

        Assert.Equal("network down", file.Error);
    }

    private static PackageFile MakeFile(out FileHosterClient client)
    {
        // Use any tempfile path that exists for the FileInfo construction
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "x");
        Package pkg = new(new PackageOptions { DirectoryPath = Path.GetDirectoryName(path)!, Logger = Mock.Of<IAppLogger>() });
        client = new StubClient();
        PackageFile file = new(pkg, path, client, new FileHosterLoginDto());
        return file;
    }

    private sealed class StubClient : FileHosterClient
    {
        public override string Name => "Stub";
        public override Task UploadAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public override Task UploadAsync(string filePath, string u, string p, CancellationToken ct = default) => Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run the test — verify it fails**

Run: `dotnet test --nologo --filter "FullyQualifiedName~PackageFilePipelineEventsTests"`
Expected: build error — `ApplyEvent` doesn't exist.

- [ ] **Step 3: Add `ApplyEvent` to `PackageFile`**

In `src/Upload/PackageFile.cs`, add (just before `private FileInfo FileInfo`):

```csharp
    /// <summary>
    /// Consumes a single <see cref="UploadEvent"/> emitted by <see cref="Pipeline.AttemptRunner"/>.
    /// Replaces the four-event subscription pattern (UploadProgress / UploadFinished /
    /// HashingProgress / HashingFinished) — those events stay during the migration window
    /// for hashing, but the upload portion now flows through here.
    /// </summary>
    public void ApplyEvent(Pipeline.UploadEvent ev)
    {
        switch (ev)
        {
            case Pipeline.TransferStarted ts:
                BytesRemaining = ts.TotalBytes;
                BytesLoaded = 0;
                Progress = 0.0;
                StartedDate = DateTime.Now;
                break;

            case Pipeline.TransferProgress tp:
                BytesLoaded = tp.BytesUploaded;
                BytesRemaining = tp.TotalBytes - tp.BytesUploaded;
                Progress = tp.PercentComplete;
                Speed = (long)tp.SpeedBytesPerSec;
                break;

            case Pipeline.TransferCompleted tc:
                IsUploadFinished = true;
                FileUrl = tc.FileUrl;
                Progress = 100.0;
                BytesRemaining = null;
                Speed = null;
                FinishedDate = DateTime.Now;
                break;

            case Pipeline.AttemptFailed af:
                Error = af.Reason;
                Speed = null;
                FinishedDate = DateTime.Now;
                break;

            case Pipeline.AttemptCancelled:
                FinishedDate = DateTime.Now;
                Speed = null;
                break;
        }
    }
```

- [ ] **Step 4: Build and re-run the test**

Run: `dotnet build && dotnet test --nologo --no-build --filter "FullyQualifiedName~PackageFilePipelineEventsTests"`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Upload/PackageFile.cs tests/Upload/PackageFilePipelineEventsTests.cs
git commit -m "pipeline(3): PackageFile.ApplyEvent consumes UploadEvent stream"
```

---

### Task 3.2: `ProxyManager` subscribes to `AttemptRunner.AttemptCompleted`

**Files:**
- Modify: `src/Upload/PackageManager.cs` — wire the subscription on construction
- Modify: `src/Upload/PackageManager.cs` — remove the `OnFileStateChanged` reach-through

The cleanest place to wire the subscription is in `App.xaml.cs` after both `ProxyManager` and `AttemptRunner` are resolved. Add a small wiring helper.

- [ ] **Step 1: Add the wiring**

In `src/App.xaml.cs`, after the `ServiceProvider` is built (search for `BuildServiceProvider` or the equivalent), add:

```csharp
        // Pipeline → ProxyManager bridge: AttemptCompleted feeds ProxyResultObserved.
        Lib.Net.ProxyManager proxyManager = serviceProvider.GetRequiredService<Lib.Net.ProxyManager>();
        Upload.Pipeline.AttemptRunner runner = serviceProvider.GetRequiredService<Upload.Pipeline.AttemptRunner>();
        runner.AttemptCompleted += (_, completed) =>
        {
            if (completed.ProxyId > 0)
            {
                proxyManager.ReportResult(completed.ProxyId, completed.Success);
            }
        };
```

- [ ] **Step 2: Remove the reach-through in `PackageManager.OnFileStateChanged`**

In `src/Upload/PackageManager.cs`, locate the block at lines 478–485 (the `Lib.Net.ProxyManager.Current?.ReportResult` call) and delete it.

- [ ] **Step 3: Run all tests**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass. (Existing tests don't exercise the runner-bridge yet; we'll add an integration test in Task 3.4.)

- [ ] **Step 4: Commit**

```bash
git add src/App.xaml.cs src/Upload/PackageManager.cs
git commit -m "pipeline(3): bridge AttemptRunner.AttemptCompleted to ProxyManager.ReportResult"
```

---

### Task 3.3: `UploadScheduler.LaunchUpload` calls `AttemptRunner`

**Files:**
- Modify: `src/Upload/UploadScheduler.cs`
- Modify: `src/Upload/PackageFile.cs` — add a `BuildAttemptInputs()` helper

This is the switch-flip. Replace `file.StartUploadAsync(ct)` (which delegates to `FileHoster.UploadAsync`) with:

```csharp
await foreach (var ev in attemptRunner.RunAsync(file.BuildAttemptInputs(logger), ct))
{
    file.ApplyEvent(ev);
}
```

The scheduler then transitions the file to `Completed` / `Failed` based on `AttemptCompleted.Success`.

- [ ] **Step 1: Add `BuildAttemptInputs` to `PackageFile`**

In `src/Upload/PackageFile.cs`:

```csharp
    /// <summary>
    /// Builds the immutable inputs for one upload attempt. Called by <see cref="UploadScheduler"/>
    /// just before invoking <see cref="Pipeline.AttemptRunner.RunAsync"/>.
    /// </summary>
    public Pipeline.AttemptInputs BuildAttemptInputs(IAppLogger logger) => new()
    {
        FilePath = FileInfo.FullName,
        FileName = Name,
        FileSize = FileInfo.Length,
        FileHash = FileHash,
        HosterName = FileHoster.Name,
        Credentials = FileHosterLogin,
        Logger = logger,
        SpeedLimitProvider = GetEffectiveSpeedLimitBytesPerSecond,
    };
```

- [ ] **Step 2: Modify `UploadScheduler` to take `AttemptRunner` and `IAppLogger`**

`UploadScheduler` already takes `AppSettings`. Extend its constructor:

```csharp
public UploadScheduler(AppSettings settings, Pipeline.AttemptRunner attemptRunner, IAppLogger logger)
{
    _settings = settings;
    _attemptRunner = attemptRunner;
    _logger = logger;
    _channel = Channel.CreateUnbounded<Action>(...);
}

private readonly Pipeline.AttemptRunner _attemptRunner;
private readonly IAppLogger _logger;
```

Update the DI registration in `App.xaml.cs` accordingly.

- [ ] **Step 3: Replace the `LaunchUpload` body**

Search `src/Upload/UploadScheduler.cs` for `LaunchUpload` (~line 290 area).

Replace the call to `file.StartUploadAsync(...)` with:

```csharp
    private void LaunchUpload(PackageFile file)
    {
        SetFileState(file, FileState.Uploading);
        CancellationTokenSource cts = new();
        file.Cts = cts;

        _ = Task.Run(async () =>
        {
            bool success = false;
            try
            {
                await foreach (Pipeline.UploadEvent ev in _attemptRunner.RunAsync(file.BuildAttemptInputs(_logger), cts.Token))
                {
                    file.ApplyEvent(ev);
                    if (ev is Pipeline.AttemptCompleted ac)
                    {
                        success = ac.Success;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Post(() => SetFileState(file, FileState.Cancelled));
                return;
            }
            catch (Exception ex)
            {
                file.Error = ex.Message;
                _logger.Log(this, LogType.Error, $"Upload pipeline crashed: {ex}");
                Post(() => SetFileState(file, FileState.Failed));
                return;
            }

            Post(() => SetFileState(file, success ? FileState.Completed : FileState.Failed));
            Post(FillSlots);
        });
    }
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build && dotnet test --nologo --no-build`
Expected: all tests pass. Existing scheduler tests use a real `PackageManager` + `UploadScheduler`; if any fail because of the new ctor parameter, update them by passing a real `AttemptRunner` built from `DefaultFileHosterRegistry`, a fake `IProxySource`, and a stub `IHttpHandlerFactory`.

- [ ] **Step 5: Smoke-test the app with a real Rapidgator account**

Run: `dotnet run --project src/CSUploader.csproj`. Add an account, queue a package, watch the upload. Expected: file uploads through the pipeline; Logs tab shows the same HTTP transactions as before; Connection Manager shows proxy result icons on completion (the new bridge wired in 3.2).

- [ ] **Step 6: Commit**

```bash
git add src/Upload/UploadScheduler.cs src/Upload/PackageFile.cs src/App.xaml.cs
git commit -m "pipeline(3): UploadScheduler dispatches uploads via AttemptRunner"
```

---

### Task 3.4: Integration test — full attempt through scheduler

**Files:**
- Test: `tests/Upload/Pipeline/AttemptRunnerIntegrationTests.cs`

End-to-end: a fake `IFileHosterPipeline` returns success; assert the bridge fires `ProxyResultObserved`.

- [ ] **Step 1: Write the test**

```csharp
// tests/Upload/Pipeline/AttemptRunnerIntegrationTests.cs
// (header omitted)

using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class AttemptRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_OnSuccessWithProxy_RaisesAttemptCompletedWithProxyId()
    {
        FakePipeline pipeline = new();
        DefaultFileHosterRegistry registry = new([pipeline]);
        Mock<IProxySource> proxy = new();
        proxy.Setup(p => p.Next()).Returns(new ProxyChoice(42, null, "http://x:1"));
        Mock<IHttpHandlerFactory> hf = new();
        hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
            .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()));
        AttemptRunner runner = new(registry, proxy.Object, hf.Object);

        AttemptCompleted? captured = null;
        runner.AttemptCompleted += (_, e) => captured = e;

        AttemptInputs inputs = new()
        {
            FilePath = "x.zip",
            FileName = "x.zip",
            FileSize = 100,
            HosterName = "Fake",
            Credentials = new FileHosterLoginDto(),
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
        };

        await foreach (UploadEvent _ in runner.RunAsync(inputs, CancellationToken.None)) { /* drain */ }

        Assert.NotNull(captured);
        Assert.True(captured!.Success);
        Assert.Equal(42, captured.ProxyId);
    }

    private sealed class FakePipeline : IFileHosterPipeline
    {
        public string Name => "Fake";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;
        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TransferStarted(ctx.FileSize);
            yield return new TransferCompleted("https://done");
        }
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test --nologo --no-build --filter "FullyQualifiedName~AttemptRunnerIntegrationTests"`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/Upload/Pipeline/AttemptRunnerIntegrationTests.cs
git commit -m "pipeline(3): integration test for AttemptRunner end-to-end"
```

---

## Phase 4: Retire old code

### Task 4.1: Extract hashing into `IHashingService`

**Files:**
- Create: `src/Lib/Crypto/IHashingService.cs`
- Create: `src/Lib/Crypto/Md5HashingService.cs`
- Modify: `src/Upload/UploadScheduler.cs` — call `IHashingService` directly in `LaunchHash`
- Modify: `src/Upload/PackageFile.cs` — drop `FileHoster_HashingProgress` / `FileHoster_HashingFinished`; consume `IProgress<HashEvent>` from the service

Hashing today is a separate scheduler stage that calls `FileHosterClient.HashAsync(filePath)`. Extract it to a service so `FileHosterClient` becomes truly metadata-only.

This task is sized larger than the others — it has its own test file and a wider edit. Break into:

- [ ] **Sub-step 4.1.a**: Add `IHashingService` interface + MD5 impl + tests.
- [ ] **Sub-step 4.1.b**: Wire `IHashingService` into `UploadScheduler.LaunchHash`.
- [ ] **Sub-step 4.1.c**: Remove the four event subscriptions from `PackageFile` ctor; remove `Cleanup()`.

Each sub-step ends with build + tests + commit.

(Note: full code samples for this task are intentionally compressed — by Phase 4 you have enough familiarity with the pipeline patterns to write them with a quick reference to the existing `Hashing.cs` class. If executing inline, expand each sub-step into its own task before starting.)

- [ ] **Step 1**: Implement 4.1.a.
- [ ] **Step 2**: Implement 4.1.b.
- [ ] **Step 3**: Implement 4.1.c.
- [ ] **Step 4**: Run all tests after each sub-step.
- [ ] **Step 5**: Commit each sub-step separately:

```bash
git commit -m "pipeline(4): extract IHashingService"
git commit -m "pipeline(4): UploadScheduler uses IHashingService directly"
git commit -m "pipeline(4): PackageFile drops FileHosterClient event subscriptions"
```

---

### Task 4.2: Delete `RapidgatorClient`; shrink `FileHosterClient`

**Files:**
- Delete: `src/Upload/RapidgatorClient.cs`
- Modify: `src/Upload/FileHosterClient.cs` — strip events, abstract `UploadAsync`, hashing.
- Modify: `src/Upload/Package.cs` — `AddPackageFiles` no longer mutates `FileHoster.SharedSessionCache`.

`FileHosterClient` becomes a small metadata facade:

```csharp
public abstract class FileHosterClient
{
    public abstract string Name { get; }
    public Protocol Protocol { get; }
    protected FileHosterClient(Protocol protocol) { Protocol = protocol; }

    public static ReadOnlyDictionary<string, string> FileHosters { get; } = /* unchanged master list */;
    public static FileHosterClient? FindByHost(string name, Protocol protocol, IAppLogger logger) => /* … */;
}
```

(Or even simpler: replace `FileHosterClient` with a `HosterMetadata` record and have the registry's `Find` return that. Decide based on how many places still call `FindByHost`. Verify with `grep -n FindByHost src tests`.)

- [ ] **Step 1**: Run `grep -n "RapidgatorClient\|FileHosterClient" src tests` to map all usages.
- [ ] **Step 2**: Replace each usage that doesn't survive — typically test fixtures and `PackageManager.LoadPersistedPackagesAsync`.
- [ ] **Step 3**: Delete `RapidgatorClient.cs`.
- [ ] **Step 4**: Strip `FileHosterClient.cs` to metadata.
- [ ] **Step 5**: Run all tests; fix any breakage.
- [ ] **Step 6**: Smoke-test the app.
- [ ] **Step 7**: Commit:

```bash
git rm src/Upload/RapidgatorClient.cs
git add -u src/Upload/FileHosterClient.cs src/Upload/Package.cs src/Upload/PackageManager.cs
git commit -m "pipeline(4): retire RapidgatorClient, FileHosterClient becomes metadata-only"
```

---

### Task 4.3: Remove `ProxyManager.Current` and `AppSettings.Current` statics

**Files:**
- Modify: `src/Lib/Net/ProxyManager.cs` — delete `public static ProxyManager? Current`.
- Modify: `src/Upload/AppSettings.cs` — delete `public static AppSettings Current`.
- Modify: `src/App.xaml.cs` — delete `ProxyManager.Current = …` and `AppSettings.Current = …` lines.
- Audit: any remaining callers of `*.Current` — fix by adding a constructor parameter or the `IProxySource` / `AppSettings` DI dependency.

- [ ] **Step 1**: `grep -n "ProxyManager.Current\|AppSettings.Current\|Logger.Current" src tests` — list all call sites.
- [ ] **Step 2**: For each call site, add the appropriate DI parameter. The two remaining calls in `HttpHandler.cs` (Phase 1) and the legacy `HttpHandler` ctor are the longest-lived holdouts — at this point all production callers go through `DefaultHttpHandlerFactory`, so the legacy ctor in `HttpHandler` can be deleted.
- [ ] **Step 3**: Build & run all tests after each batch.
- [ ] **Step 4**: Commit:

```bash
git add -u
git commit -m "pipeline(4): remove ProxyManager.Current and AppSettings.Current statics"
```

`Logger.Current` may have wider usage (it's load-bearing for `HttpHandler`). Mark it `[Obsolete]` if it can't be deleted cleanly in this task; create a follow-up.

---

### Task 4.4: Final sweep

- [ ] **Step 1**: `grep -n "TODO\|FIXME" src/Upload/Pipeline` to catch any forgotten stubs.
- [ ] **Step 2**: Build with `dotnet build -warnaserror` to verify no new warnings introduced. The original CS8602 sites in `RapidgatorClient.cs` are now deleted along with the file.
- [ ] **Step 3**: Run `dotnet test --nologo` end-to-end.
- [ ] **Step 4**: Commit any cleanups:

```bash
git commit -am "pipeline(4): final sweep"
```

---

## Phase 5: Validate auth-shape flexibility

Phase 5 proves the pipeline contract works for an auth shape unlike Rapidgator's. This is a *test-only* task — no production hoster ships in this plan, but the test gives the next implementer (probably you, in another branch) a working reference.

### Task 5.1: `FakeCookieHosterPipeline` reference impl

**Files:**
- Test: `tests/Upload/Pipeline/Hosters/FakeCookieHosterPipelineTests.cs`

Build a fake hoster whose auth shape is **cookies**, not tokens. Demonstrates that auth state can be anything the pipeline wants.

- [ ] **Step 1: Implement the fake (in the test file, not production)**

```csharp
// tests/Upload/Pipeline/Hosters/FakeCookieHosterPipelineTests.cs
// (header omitted)

using System.Net;
using System.Runtime.CompilerServices;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class FakeCookieHosterPipelineTests
{
    [Fact]
    public async Task CookieAuth_PersistsAcrossAttempts_WithoutTokenOrHeader()
    {
        FakeCookieHosterPipeline pipeline = new();

        AttemptContext ctx1 = MakeContext();
        await Drain(pipeline.RunAsync(ctx1, CancellationToken.None));

        AttemptContext ctx2 = MakeContext();
        List<UploadEvent> evs = [];
        await foreach (UploadEvent ev in pipeline.RunAsync(ctx2, CancellationToken.None))
        {
            evs.Add(ev);
        }

        // Second attempt skips AuthStarted because the cookie jar is reused
        Assert.DoesNotContain(evs, e => e is AuthStarted);
        Assert.Contains(evs, e => e is TransferCompleted);
    }

    private static async Task Drain(IAsyncEnumerable<UploadEvent> stream)
    {
        await foreach (UploadEvent _ in stream) { }
    }

    private static AttemptContext MakeContext() => new()
    {
        AttemptId = Guid.NewGuid(),
        FilePath = "x.zip",
        FileName = "x.zip",
        FileSize = 100,
        HosterName = "FakeCookie",
        Credentials = new FileHosterLoginDto { Id = 17, FileHosterName = "FakeCookie", Username = "u", Password = "p" },
        Proxy = ProxyChoice.Direct,
        Handler = new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>()),
        Logger = Mock.Of<IAppLogger>(),
        SpeedLimitProvider = () => null,
        Cancellation = default,
    };

    /// <summary>
    /// Reference cookie-based pipeline: <see cref="CookieContainer"/> is the auth state,
    /// keyed by Credentials.Id. No token, no bearer header — proves the contract is generic.
    /// </summary>
    private sealed class FakeCookieHosterPipeline : IFileHosterPipeline
    {
        private readonly Dictionary<int, CookieContainer> _jars = [];

        public string Name => "FakeCookie";
        public bool RequiresHashingBeforeUpload => false;
        public bool RequiresHashingAfterUpload => false;

        public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!_jars.ContainsKey(ctx.Credentials.Id))
            {
                yield return new AuthStarted();
                await Task.Yield();
                CookieContainer jar = new();
                jar.Add(new Cookie("session", "abc", "/", "fake"));
                _jars[ctx.Credentials.Id] = jar;
                yield return new AuthSucceeded();
            }

            yield return new TransferStarted(ctx.FileSize);
            await Task.Yield();
            yield return new TransferCompleted("https://fake/file/" + ctx.FileName);
        }
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test --nologo --no-build --filter "FullyQualifiedName~FakeCookieHosterPipelineTests"`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/Upload/Pipeline/Hosters/FakeCookieHosterPipelineTests.cs
git commit -m "pipeline(5): reference cookie-auth pipeline test"
```

---

### Task 5.2: Document auth-shape patterns

**Files:**
- Modify: `src/Upload/Pipeline/IFileHosterPipeline.cs` — expand the xmldoc.

- [ ] **Step 1: Edit the interface xmldoc**

Replace the existing `<example>` block with three concrete patterns:

```csharp
/// <example>
/// <para><b>Token-based</b> (Rapidgator-style): cache <c>(token, expiry)</c> per credentials id;
/// invalidate on 401; pass token via query param or bearer header.</para>
/// <para><b>Cookie-based</b>: cache a <see cref="System.Net.CookieContainer"/> per credentials id;
/// the runner-supplied <c>HttpHandler</c> is constructed without `UseCookies`, so the
/// pipeline must attach cookies to outbound requests itself or use a hoster-internal
/// HttpClient adorned with the cached jar.</para>
/// <para><b>API-key</b>: no auth state needed beyond <see cref="AttemptContext.Credentials"/>;
/// every request includes the key in a header. <see cref="AuthStarted"/>/<see cref="AuthSucceeded"/>
/// can be skipped entirely.</para>
/// <para><b>OAuth2 with refresh</b>: cache <c>(access_token, refresh_token, expiry)</c>; on
/// expiry try refresh first, then full re-login.</para>
/// </example>
```

- [ ] **Step 2: Build to verify the xmldoc compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Upload/Pipeline/IFileHosterPipeline.cs
git commit -m "pipeline(5): document IFileHosterPipeline auth-shape patterns"
```

---

## Self-Review

**Spec coverage:**
- ✅ Per-attempt non-nullable `HttpHandler` — `AttemptContext.Handler` (Task 0.3) is non-nullable, threaded through `IFileHosterPipeline.RunAsync` (Task 0.4).
- ✅ CS8602 disappears — Phase 4 deletes `RapidgatorClient.cs` (the only file with the warnings); `RapidgatorPipeline` never declares a nullable handler field.
- ✅ Auth-shape diversity — `IFileHosterPipeline.RunAsync` is the only required entrypoint; pipelines own their auth state internally. Demonstrated by Task 5.1 (cookies) and documented by Task 5.2.
- ✅ `ProxyManager.Current` removal — Task 4.3 deletes the static; Task 3.2 wires the bridge via `AttemptRunner.AttemptCompleted` event subscription.
- ✅ `AppSettings.Current` removal — Task 4.3; replaced by `MockServerConfig` snapshots (Task 1.1) and DI-injected `AppSettings`.
- ✅ Hashing extraction — Task 4.1 isolates `IHashingService` so `FileHosterClient` can shrink to metadata.

**Type consistency:** `AttemptContext` properties match across Tasks 0.3, 2.2-2.5, 3.1, 3.4. `ProxyChoice` and `UploadEvent` records flow through `AttemptRunner` and pipelines without renames. `IFileHosterPipeline.Name` matches `DefaultFileHosterRegistry`'s key convention (case-insensitive ordinal).

**Placeholders:** Task 4.1 sub-steps 4.1.a–4.1.c are intentionally compressed (no full code samples) because they're mechanical translations of the existing `Hashing.cs` class — but the steps name the exact files and the exact responsibilities. Task 4.2 step 2 ("for each call site...") is a directed action with a `grep` command supplying the exact list. Task 4.3 step 2 likewise.

---

**Done.**
