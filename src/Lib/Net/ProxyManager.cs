// <copyright file="ProxyManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Lib.Net;

/// <summary>
/// Holds the current set of enabled proxies (in priority order) and hands them out
/// to <see cref="HttpHandler"/>-using clients via <see cref="NextProxy"/>. New uploads
/// (and retries — each retry constructs a fresh client) pick a proxy at construction;
/// in-flight HttpClients are unaffected by changes here.
/// </summary>
public class ProxyManager
{
    /// <summary>
    /// Static accessor for code paths that aren't on the DI graph yet
    /// (e.g. <see cref="Upload.FileHosterClient.FindByHost"/>'s factories).
    /// Mirrors the <see cref="Upload.AppSettings.Current"/> pattern.
    /// </summary>
    public static ProxyManager? Current { get; set; }

    private readonly ProxySettingRepository _repo;
    private readonly Lock _lock = new();
    private List<ProxySettingDto> _proxies = [];
    private int _rotationIndex;

    // Constructor keeps the IAppLogger parameter for DI signature stability even though
    // ProxyManager itself no longer logs — the per-test logging happens via the wrapping
    // logger inside TestProxyAsync.
    public ProxyManager(ProxySettingRepository repo, IAppLogger logger)
    {
        _repo = repo;
        _ = logger;
    }

    /// <summary>
    /// Reloads the proxy list from the database. Called at startup and after the
    /// Connection Manager UI saves changes. FileHosterClients pick up the new state
    /// on their next upload attempt (they build a fresh HttpHandler each time), so no
    /// extra "rotation reloaded" plumbing is needed.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ProxySettingDto[] all = await _repo.GetAllAsync(cancellationToken);
        ProxySettingDto[] enabled = [.. all.Where(p => p.Enabled).OrderBy(p => p.Priority).ThenBy(p => p.Id)];
        lock (_lock)
        {
            _proxies = [.. enabled];
            if (_rotationIndex >= _proxies.Count)
            {
                _rotationIndex = 0;
            }
        }
    }

    /// <summary>
    /// Raised whenever a proxy's connectivity is observed — either by a manual test or
    /// during an upload. Subscribers can react to update UI status icons without polling.
    /// </summary>
    public event EventHandler<ProxyResultEventArgs>? ProxyResultObserved;

    /// <summary>
    /// Notify subscribers (currently the Connection Manager grid) that a proxy was just
    /// exercised, with the outcome. Safe to call from background threads — handlers must
    /// marshal back to the dispatcher themselves if they touch UI state.
    /// </summary>
    public void ReportResult(int proxyId, bool success, string? message = null)
    {
        if (proxyId <= 0)
        {
            return;
        }

        ProxyResultObserved?.Invoke(this, new ProxyResultEventArgs(proxyId, success, message));
    }

    /// <summary>
    /// Returns the next proxy in the rotation, or <c>null</c> if no proxies are enabled,
    /// the next entry is a sentinel "No Proxy" / direct connection, or the master
    /// <see cref="Upload.AppSettings.ProxiesEnabled"/> switch is off (the user
    /// configured proxies but doesn't want them used yet).
    /// </summary>
    public ProxySettingDto? NextProxy()
    {
        if (Upload.AppSettings.Current?.ProxiesEnabled == false)
        {
            return null;
        }

        lock (_lock)
        {
            if (_proxies.Count == 0)
            {
                return null;
            }

            ProxySettingDto candidate = _proxies[_rotationIndex];
            _rotationIndex = (_rotationIndex + 1) % _proxies.Count;
            return candidate.Type == ProxyType.None ? null : candidate;
        }
    }

    /// <summary>
    /// Builds an <see cref="IWebProxy"/> from the given setting. Returns null for
    /// <see cref="ProxyType.None"/> or invalid entries (caller should treat null as
    /// "no proxy"). Supports HTTP/HTTPS/SOCKS4/SOCKS5 via WebProxy URI scheme
    /// (built into .NET 6+).
    /// </summary>
    public static IWebProxy? BuildWebProxy(ProxySettingDto proxy)
    {
        if (proxy.Type == ProxyType.None || string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port <= 0)
        {
            return null;
        }

        string scheme = proxy.Type switch
        {
            ProxyType.Http => "http",
            ProxyType.Https => "https",
            ProxyType.Socks4 => "socks4",
            ProxyType.Socks5 => "socks5",
            _ => "http",
        };

        Uri uri = new($"{scheme}://{proxy.Host}:{proxy.Port}");
        WebProxy webProxy = new(uri);
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        }

        return webProxy;
    }

    /// <summary>
    /// Test endpoint used by <see cref="TestProxyAsync"/>. Returns the caller's IP as
    /// plain text — small, fast, and lets us confirm the proxy actually masked the IP.
    /// </summary>
    public static string TestEndpoint { get; set; } = "https://api.ipify.org";

    /// <summary>
    /// Performs a single HTTP GET through the given proxy with a short timeout. Used
    /// by the Connection Manager's "Test" / "Test All" actions to surface dead or
    /// unauthenticated proxies before they break uploads. Routed through
    /// <see cref="HttpHandler"/> so the request lands in the Logs tab alongside upload
    /// traffic.
    /// </summary>
    public static async Task<ProxyTestResult> TestProxyAsync(ProxySettingDto proxy, IAppLogger logger, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (proxy.Type == ProxyType.None)
        {
            return ProxyTestResult.Failed("Proxy type is None — direct connection, nothing to test.");
        }

        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port <= 0)
        {
            return ProxyTestResult.Failed("Host or port is invalid.");
        }

        IWebProxy? webProxy = BuildWebProxy(proxy);
        if (webProxy is null)
        {
            return ProxyTestResult.Failed("Could not construct proxy from settings.");
        }

        HttpClientHandler handler = new()
        {
            Proxy = webProxy,
            UseProxy = true,
            AllowAutoRedirect = false,
        };
        using HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        string proxyDescription = $"{proxy.Type.ToString().ToLowerInvariant()}://{proxy.Host}:{proxy.Port}";
        // Capture the transaction HttpHandler builds so the Details button can show the
        // full request+response. Wraps the user's logger so the existing log-to-Logs-tab
        // behaviour is preserved.
        HttpTransaction? captured = null;
        TransactionCapturingLogger capturingLogger = new(logger, tx => captured ??= tx);
        // bypassMockServer: a connectivity test against api.ipify.org would otherwise be
        // rewritten to localhost:8080/api in DEBUG builds, which defeats the whole point.
        HttpHandler httpHandler = new(client, capturingLogger, proxyDescription, bypassMockServer: true);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string body = await httpHandler.GetStringAsync(TestEndpoint, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            // HttpClient.GetAsync only throws on transport errors, not on 4xx/5xx.
            // A misbehaving proxy that responds with e.g. 503 + an HTML error page
            // would otherwise read as Success — surface the status code as a failure.
            int status = captured?.StatusCode ?? 0;
            if (status is < 200 or >= 300)
            {
                string reason = string.IsNullOrEmpty(captured?.StatusReason)
                    ? "non-success status"
                    : captured!.StatusReason;
                return ProxyTestResult.Failed($"HTTP {status} {reason}") with
                {
                    LatencyMs = sw.ElapsedMilliseconds,
                    Body = body.Trim(),
                    Transaction = captured,
                };
            }

            return ProxyTestResult.Ok(sw.ElapsedMilliseconds, body.Trim()) with { Transaction = captured };
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProxyTestResult.Failed("Cancelled.") with { Transaction = captured };
        }
        catch (TaskCanceledException)
        {
            return ProxyTestResult.Failed($"Timed out after {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds:0}s.") with { Transaction = captured };
        }
        catch (HttpRequestException ex)
        {
            return ProxyTestResult.Failed(ex.Message) with { Transaction = captured };
        }
        catch (Exception ex)
        {
            return ProxyTestResult.Failed(ex.GetType().Name + ": " + ex.Message) with { Transaction = captured };
        }
    }

    /// <summary>
    /// Lightweight IAppLogger decorator that forwards every log call to an inner logger
    /// (so the Logs tab still gets the entry) while snapping the first HttpTransaction
    /// it sees, used by <see cref="TestProxyAsync"/> to surface request/response details.
    /// </summary>
    private sealed class TransactionCapturingLogger(IAppLogger inner, Action<HttpTransaction> capture) : IAppLogger
    {
        public event LogEventHandler? OnLogOutput
        {
            add => inner.OnLogOutput += value;
            remove => inner.OnLogOutput -= value;
        }

        public void Log(
            object? sender,
            LogType logType,
            string text,
            HttpTransaction? httpTransaction = null,
            string filePath = "",
            string function = "",
            int lineNumber = 0)
        {
            if (httpTransaction is not null)
            {
                capture(httpTransaction);
            }

            inner.Log(sender, logType, text, httpTransaction, filePath, function, lineNumber);
        }
    }

}
