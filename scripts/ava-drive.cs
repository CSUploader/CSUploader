#!/usr/bin/env dotnet
// Direct-TCP driver for AvaDevBridge — used until the MCP server is loaded (session restart).
// Usage: dotnet run scripts/ava-drive.cs -- <tool> [json-args] [--out <file>]
//   e.g. dotnet run scripts/ava-drive.cs -- ava_windows
//        dotnet run scripts/ava-drive.cs -- ava_screenshot '{"maxWidth":2500}' --out shot.png
//   Auto-discovers the newest live handshake; a screenshot's base64 payload is saved to --out
//   (default shot.png) and the printed envelope carries the file path instead.
#:project ../../../avalonia-agent-mcp/AvaDevProtocol/AvaDevProtocol.csproj
// .NET 10 file-based apps run with reflection-based System.Text.Json disabled; AvaDevProtocol's
// HandshakeFile.Discover deserializes a POCO reflectively, so re-enable the runtime switch here.
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Net.Sockets;
using System.Text.Json;
using AvaDevProtocol;

// Strip "--out <value>" first, THEN derive tool/json-args from the remainder — otherwise
// the value after --out is mistaken for the json-args (or even the tool name).
string outPath = "shot.png";
List<string> rest = [];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outPath = args[++i];
        continue;
    }

    rest.Add(args[i]);
}

string? tool = rest.FirstOrDefault(a => !a.StartsWith('-'));
if (tool is null)
{
    Console.Error.WriteLine("usage: ava-drive <tool> [json-args] [--out file]");
    return 2;
}

string? argsJson = rest.Skip(rest.IndexOf(tool) + 1).FirstOrDefault(a => !a.StartsWith('-'));

HandshakeInfo? hs = HandshakeFile.Discover().OrderByDescending(h => h.StartedUtc).FirstOrDefault();
if (hs is null)
{
    Console.Error.WriteLine("no live bridge app found (is the Avalonia app running in Debug?)");
    return 3;
}

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
    // Default to an empty object, not null: bridge tools call args.TryGetProperty(...), which
    // throws on a JSON null (ava_tree/ava_screenshot). This matches how the MCP client always
    // sends an arguments object for no-arg tools.
    ["args"] = JsonSerializer.Deserialize<JsonElement>(argsJson ?? "{}"),
};
await FrameProtocol.WriteAsync(
    stream,
    JsonSerializer.SerializeToUtf8Bytes(request),
    CancellationToken.None);
byte[] payload = await FrameProtocol.ReadAsync(stream, FrameProtocol.MaxResponse, CancellationToken.None);

JsonElement env = JsonSerializer.Deserialize<JsonElement>(payload);
if (env.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
    && result.TryGetProperty("base64", out JsonElement b64))
{
    File.WriteAllBytes(outPath, Convert.FromBase64String(b64.GetString()!));
    Console.WriteLine($"{{\"ok\":true,\"savedTo\":\"{outPath.Replace("\\", "/", StringComparison.Ordinal)}\",\"mime\":{result.GetProperty("mime").GetRawText()}}}");
    return 0;
}

Console.WriteLine(env.GetRawText());
return env.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean() ? 0 : 1;
