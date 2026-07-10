using CSUploader.Lib.Net;

namespace CSUploader.Services;

/// <summary>
/// Placeholder <see cref="IInteractiveAuthService"/> for Phase 2. Interactive (captcha-gated,
/// WebView2-hosted) sign-in arrives with the Phase 8 login port; until then any upload that reaches
/// a captcha-gated hoster's sign-in fails fast rather than silently no-opping.
/// </summary>
public sealed class StubInteractiveAuthService : IInteractiveAuthService
{
    public Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
        InteractiveAuthSpec spec,
        string username,
        ProxyChoice? proxy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Interactive sign-in arrives with the Phase 8 WebView2 port.");
}
