// <copyright file="ProxyManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Dal;

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
    /// (e.g. <see cref="CSUploader.Upload.FileHosterClient.FindByHost"/>'s factories).
    /// Mirrors the <see cref="CSUploader.Upload.AppSettings.Current"/> pattern.
    /// </summary>
    public static ProxyManager? Current { get; set; }

    private readonly ProxySettingRepository _repo;
    private readonly IAppLogger _logger;
    private readonly Lock _lock = new();
    private List<ProxySettingDto> _proxies = [];
    private int _rotationIndex;

    public ProxyManager(ProxySettingRepository repo, IAppLogger logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Reloads the proxy list from the database. Called at startup and after the
    /// Connection Manager UI saves changes.
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
    /// Returns the next proxy in the rotation, or <c>null</c> if no proxies are enabled
    /// or the next entry is a sentinel "No Proxy" / direct connection.
    /// </summary>
    public ProxySettingDto? NextProxy()
    {
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
    /// Records a failure against a specific proxy (fire-and-forget DB increment).
    /// Surfaced in the Connection Manager grid via <see cref="ProxySettingDto.ProblemsCount"/>.
    /// </summary>
    public void RecordFailure(int proxyId)
    {
        if (proxyId <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _repo.IncrementProblemsAsync(proxyId);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to increment proxy problem count: {ex.Message}");
            }
        });
    }
}
