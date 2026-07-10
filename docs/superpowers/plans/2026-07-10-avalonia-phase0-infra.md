# Avalonia Migration Phase 0: Infrastructure — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the migration tooling: MCP wiring, a direct-TCP bridge driver for the current session, the i18n regen gate, and the submodule build guard.

**Architecture:** No app code changes. Everything here is dev-loop plumbing defined in the design doc (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, §MCP dev loop, §Phases/Phase 0).

**Tech Stack:** Claude Code `.mcp.json`, .NET 10 file-based C# app, Python 3, MSBuild, xunit.

## Global Constraints

- Work happens in the worktree `E:\Projects\CSUploader\CSUploader-avalonia` on branch `avalonia-migration` (already created; submodule already initialized).
- Tooling repo: `E:\Projects\avalonia-agent-mcp` (read-only for us — never modify it).
- All commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Never hand-edit `src/Resources/Strings*.resx` — they are generated from `docs/i18n-inventory*.md`.

---

### Task 1: `.mcp.json` pointing at the pre-built AvaDevMcp exe

**Files:**
- Create: `.mcp.json` (repo root)
- Modify: `README.md` (dev-tooling note at the end)

**Interfaces:**
- Produces: MCP server `ava-desktop` available to any future Claude Code session opened in this repo.

- [ ] **Step 1: Verify the Release build of AvaDevMcp exists (build once if missing)**

Run: `ls "E:/Projects/avalonia-agent-mcp/AvaDevMcp/bin/Release/net10.0/AvaDevMcp.exe" || dotnet build "E:/Projects/avalonia-agent-mcp/AvaDevMcp/AvaDevMcp.csproj" -c Release`
Expected: file exists (it was built earlier this session).

- [ ] **Step 2: Create `.mcp.json`**

```json
{
  "mcpServers": {
    "ava-desktop": {
      "command": "E:/Projects/avalonia-agent-mcp/AvaDevMcp/bin/Release/net10.0/AvaDevMcp.exe"
    }
  }
}
```

Note: Claude Code shape is `mcpServers` (NOT the VS Code `servers` shape shown in the tooling repo's README). Pre-built exe avoids build latency, MSBuild stdout polluting stdio JSON-RPC, and the tooling repo's TreatWarningsAsErrors drift.

- [ ] **Step 3: Append a dev-tooling note to README.md**

```markdown
## UI agent tooling (dev only)

`.mcp.json` wires the [avalonia-agent-mcp](E:/Projects/avalonia-agent-mcp) DevTools bridge
(`ava_attach`, `ava_screenshot`, `ava_tree`, ...) into Claude Code sessions. One-time setup:
`dotnet build -c Release E:/Projects/avalonia-agent-mcp/AvaDevMcp/AvaDevMcp.csproj`, then restart
the session. Rebuild after pulling tooling-repo changes (a stale exe fails loudly at `ava_attach`
with a protocol-version error). The Avalonia head only starts the in-app bridge when built on a
machine that has the tooling repo (see `Directory.Build.local.props`, Phase 2).
```

- [ ] **Step 4: Commit**

```bash
git add .mcp.json README.md
git commit -m "chore: wire avalonia-agent-mcp MCP server for agent-driven UI development"
```

---

### Task 2: `scripts/ava-drive.cs` — direct-TCP bridge driver (session fallback)

**Files:**
- Create: `scripts/ava-drive.cs`

**Interfaces:**
- Consumes: `AvaDevProtocol` (project reference): `HandshakeFile.Discover()`/`PruneDead()`, `FrameProtocol` (4-byte big-endian length prefix + UTF-8 JSON, 32 MB cap), `ToolResponse(bool Ok, JsonElement? Result, ToolErrorInfo? Error)`. Wire request: `{"token":"...","tool":"ava_tree","args":{...}}` camelCase. `ava_screenshot` result: `{"mime":"image/png","base64":"..."}`.
- Produces: CLI for the CURRENT session (MCP tools need a session restart): `dotnet run scripts/ava-drive.cs -- <tool> [json-args] [--out file.png]`. Examples: `dotnet run scripts/ava-drive.cs -- ava_windows`, `dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out shot.png`.

- [ ] **Step 1: Write the driver**

```csharp
#!/usr/bin/env dotnet
// Direct-TCP driver for AvaDevBridge — used until the MCP server is loaded (session restart).
// Usage: dotnet run scripts/ava-drive.cs -- <tool> [json-args] [--out <file>]
//   Auto-discovers the newest live handshake; screenshot base64 is saved to --out (default shot.png)
//   and replaced with the file path in the printed envelope.
#:project ../../../avalonia-agent-mcp/AvaDevProtocol/AvaDevProtocol.csproj

using System.Net.Sockets;
using System.Text.Json;
using AvaDevProtocol;

string? tool = args.FirstOrDefault(a => !a.StartsWith('-'));
if (tool is null) { Console.Error.WriteLine("usage: ava-drive <tool> [json-args] [--out file]"); return 2; }
string? argsJson = args.Skip(Array.IndexOf(args, tool) + 1).FirstOrDefault(a => !a.StartsWith('-'));
string outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? "shot.png";

HandshakeInfo? hs = HandshakeFile.Discover().OrderByDescending(h => h.StartedUtc).FirstOrDefault();
if (hs is null) { Console.Error.WriteLine("no live bridge app found (is the Avalonia app running in Debug?)"); return 3; }

using TcpClient client = new();
try
{
    await client.ConnectAsync("127.0.0.1", hs.Port);
}
catch (SocketException)
{
    Console.Error.WriteLine($"no live bridge app found (stale handshake for pid {hs.Pid})");
    return 3;
}
NetworkStream stream = client.GetStream();

Dictionary<string, object?> request = new()
{
    ["token"] = hs.Token,
    ["tool"] = tool,
    ["args"] = argsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(argsJson),
};
await FrameProtocol.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), CancellationToken.None);
byte[] payload = await FrameProtocol.ReadAsync(stream, FrameProtocol.MaxResponse, CancellationToken.None);

JsonElement env = JsonSerializer.Deserialize<JsonElement>(payload);
if (env.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
    && result.TryGetProperty("base64", out JsonElement b64))
{
    File.WriteAllBytes(outPath, Convert.FromBase64String(b64.GetString()!));
    Console.WriteLine($"{{\"ok\":true,\"savedTo\":\"{outPath.Replace("\\", "/")}\",\"mime\":{result.GetProperty("mime").GetRawText()}}}");
    return 0;
}

Console.WriteLine(env.GetRawText());
return env.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean() ? 0 : 1;
```

NOTE: signatures reviewer-verified against the tooling source: `HandshakeFile.Discover()` → `IReadOnlyList<HandshakeInfo>` (`.Pid/.Port/.Token/.StartedUtc`); `FrameProtocol.WriteAsync(Stream, ReadOnlyMemory<byte>, CancellationToken)`; `FrameProtocol.ReadAsync(Stream, int maxBytes, CancellationToken)` with `FrameProtocol.MaxResponse` as the cap (mirrors the MCP client's own call). `#:project` resolves relative to the .cs FILE's directory (empirically confirmed), so from `scripts/` the tooling repo is `../../../avalonia-agent-mcp/...` — correct from both worktrees (same depth).

- [ ] **Step 2: Smoke-test the no-app path**

Run: `dotnet run scripts/ava-drive.cs -- ava_windows`
Expected: exit 3 with `no live bridge app found ...` (no bridge app exists yet — full validation happens in Phase 2 against the real shell).

- [ ] **Step 3: Commit**

```bash
git add scripts/ava-drive.cs
git commit -m "chore: add direct-TCP AvaDevBridge driver for in-session UI verification"
```

---

### Task 3: `md-to-resx.py --check` mode + xunit gate

**Files:**
- Modify: `scripts/md-to-resx.py` (main() currently at :121-134 — positional `input`/`output` args)
- Test: `tests/Localization/I18nRegenGateTests.cs` (new)

**Interfaces:**
- Produces: `python scripts/md-to-resx.py --check <input.md> <output.resx>` → exit 0 when the committed resx byte-matches the regen, exit 1 with a diff summary otherwise. The xunit gate runs it for all 6 languages on every `dotnet test` (so release.yml enforces it too), skipping when python is unavailable.

- [ ] **Step 1: Add `--check` to md-to-resx.py**

In `main()`, add `parser.add_argument("--check", action="store_true", help="verify output matches regen instead of writing")` and replace the unconditional `write_resx` with:

```python
    entries = parse_md(args.input)
    if args.check:
        import io
        buf = io.StringIO()
        buf.write(RESX_HEADER)
        for key, value in entries.items():
            escaped = xml_escape(value)
            buf.write(f'  <data name="{key}" xml:space="preserve">\n')
            buf.write(f"    <value>{escaped}</value>\n")
            buf.write("  </data>\n")
        buf.write(RESX_FOOTER)
        expected = buf.getvalue()
        # Plain read_text: universal-newline mode normalizes the committed CRLF resx to LF,
        # matching the LF render. Do NOT pass newline="" — that would defeat it.
        actual = args.output.read_text(encoding="utf-8") if args.output.is_file() else ""
        if actual == expected:
            print(f"OK: {args.output} matches regen of {args.input}")
            return 0
        print(f"DRIFT: {args.output} does not match regen of {args.input} — "
              f"edit the .md and regenerate; never hand-edit resx", file=sys.stderr)
        return 1
    write_resx(entries, args.output)
```

Refactor note: extract the buffer-building into a `render_resx(entries) -> str` helper used by both `write_resx` and the check to avoid the duplication above — keep behavior byte-identical (`write_resx` opens with `newline="\n"`; `read_text` must therefore compare against LF content — verify on a real file).

- [ ] **Step 2: Verify --check passes on the current tree and fails on a tamper**

Run: `python scripts/md-to-resx.py --check docs/i18n-inventory.md src/Resources/Strings.resx` → exit 0.
Then append a space to a `<value>` in a COPY of Strings.ja.resx in %TEMP% and `--check` against it → exit 1. (Never tamper with the committed file.)

- [ ] **Step 3: Write the xunit gate**

```csharp
// tests/Localization/I18nRegenGateTests.cs
using System.Diagnostics;

namespace CSUploader.Tests.Localization;

public class I18nRegenGateTests
{
    public static TheoryData<string, string> Languages => new()
    {
        { "docs/i18n-inventory.md", "src/Resources/Strings.resx" },
        { "docs/i18n-inventory.zh-Hans.md", "src/Resources/Strings.zh-Hans.resx" },
        { "docs/i18n-inventory.ko.md", "src/Resources/Strings.ko.resx" },
        { "docs/i18n-inventory.ja.md", "src/Resources/Strings.ja.resx" },
        { "docs/i18n-inventory.vi.md", "src/Resources/Strings.vi.resx" },
        { "docs/i18n-inventory.fil.md", "src/Resources/Strings.fil.resx" },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Resx_MatchesRegenerationFromInventoryMd(string md, string resx)
    {
        string root = FindRepoRoot();
        ProcessStartInfo psi = new("python",
            $"\"{Path.Combine(root, "scripts/md-to-resx.py")}\" --check \"{Path.Combine(root, md)}\" \"{Path.Combine(root, resx)}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true };

        using Process? proc = TryStart(psi);
        if (proc is null)
        {
            return; // python not on PATH — the gate still runs on any machine that has it (incl. CI)
        }

        proc.WaitForExit(30_000);
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.ExitCode == 0, $"i18n drift for {resx}: {stderr}");
    }

    private static Process? TryStart(ProcessStartInfo psi)
    {
        try { return Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // CallerFilePath, NOT AppContext.BaseDirectory: the repo builds to a temp OutDir
    // (D:\temp2\...) to dodge bin locks, so the binary's directory is outside the repo.
    // Same pattern + rationale as tests/Upload/FileHosterIconTests.cs:45.
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = Directory.GetParent(thisFilePath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSUploader.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + thisFilePath);
    }
}
```

Note: the early-return-on-no-python keeps this on xunit 2.9.3 without adding a skippable-fact package. The resx paths in `Languages` move to `src/CSUploader.Core/Resources/` during Phase 1 Task 2 — that plan updates this test.

- [ ] **Step 4: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~I18nRegenGateTests" -p:OutDir=D:\temp2\cbuild-mig` (temp OutDir per the repo's build-lock convention)
Expected: 6 passing (or all skipped if python missing — verify python IS found locally).

- [ ] **Step 5: Commit**

```bash
git add scripts/md-to-resx.py tests/Localization/I18nRegenGateTests.cs
git commit -m "test: machine-enforce resx == regen(i18n-inventory md) via md-to-resx --check"
```

---

### Task 4: MSBuild guard against an empty vscode-icons submodule

**Files:**
- Modify: `src/CSUploader.csproj` (after the existing `<Resource Include="..\external\vscode-icons\icons\*.svg">` ItemGroup at :57-61)

**Interfaces:**
- Produces: build error `vscode-icons submodule not initialized...` when the glob matches nothing; the same Target is copied into the Avalonia head in Phase 2.

- [ ] **Step 1: Add the guard target**

```xml
  <!-- A fresh worktree/clone without `git submodule update --init` leaves the icons glob
       empty, which silently ships an app with no file-type icons (a glob matching zero
       files is not an MSBuild error). Fail the build instead. -->
  <Target Name="EnsureVsCodeIconsSubmodule" BeforeTargets="BeforeBuild">
    <Error Condition="!Exists('..\external\vscode-icons\icons\file_type_json.svg')"
           Text="vscode-icons submodule not initialized — run: git submodule update --init --recursive" />
  </Target>
```

- [ ] **Step 2: Verify both directions**

Run: `dotnet build src/CSUploader.csproj -p:OutDir=D:\temp2\cbuild-mig\wpf` in the worktree → succeeds (submodule is initialized).
Then rename `external/vscode-icons/icons` to `icons_` temporarily, build → expect the error text; rename back, build → succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/CSUploader.csproj
git commit -m "build: fail fast when the vscode-icons submodule is uninitialized"
```
