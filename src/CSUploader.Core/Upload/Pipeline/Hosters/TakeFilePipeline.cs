// <copyright file="TakeFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// TakeFile. Standard XFileSharingPro API; protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// </summary>
/// <remarks>
/// DISABLED 2026-06-28 — the class is retained (registry entry, DI registration, and the
/// EditAccount ApiKeyHosters entry are commented out; the smoke test asserts it is absent from the
/// registry) so a re-enable is low-churn.
/// <para>
/// Why: takefile.link's whole domain is behind a Cloudflare <b>managed</b> challenge
/// (<c>cType:'managed'</c>). The C# my_account scrape — and every other request to the domain —
/// gets served the "Just a moment…" interstitial instead of the page. The 2026-05-26 probe sweep
/// "worked" only because the challenge wasn't being enforced that day; it is now.
/// </para>
/// <para>
/// What we tried (and why it didn't work): the opt-in cf_clearance path on the base
/// (<see cref="RequiresCloudflareClearance"/> / <see cref="SignInUserAgentOverride"/> /
/// <see cref="SignInSessionLifetime"/>, all still wired below). The WebView solves the challenge
/// and holds a <c>cf_clearance</c> cookie; we pin the WebView UA to the handler's, capture the
/// clearance, and forward it (with xfss) on every request. Verified against a real browser capture.
/// But a <i>managed</i> challenge also validates the browser's TLS/JA-fingerprint, which a .NET
/// <c>HttpClient</c> can't reproduce, so Cloudflare rejects the request even with a valid
/// clearance + matching UA + IP. (The cf_clearance machinery is kept on the base because it WOULD
/// defeat a lighter, non-managed Cloudflare challenge — it just can't beat a managed one.)
/// </para>
/// <para>
/// Re-enable checklist (only after confirming takefile.link no longer serves a managed challenge to
/// non-browser clients): (1) un-comment the FileHosterClient registry entry; (2) un-comment the DI
/// registration in App.xaml.cs; (3) re-add "TakeFile" to EditAccountWindow.ApiKeyHosters; (4) flip
/// the smoke test back to asserting the registry contains it.
/// </para>
/// </remarks>
public sealed class TakeFilePipeline : XFileSharingApiPipeline
{
    public TakeFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal TakeFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "TakeFile";

    protected override string Host => "https://takefile.link";

    /// <summary>TakeFile's sign-in page is <c>/user_login</c>, not the XFS-default
    /// <c>/login.html</c> — so the WebView sign-in lands on the right page.</summary>
    protected override string LoginPagePath => "/user_login";

    // ---- Cloudflare managed-challenge clearance (see XFileSharingApiPipeline) ----
    // takefile.link's whole domain is behind a Cloudflare managed challenge: the C# my_account
    // scrape gets the "Just a moment…" interstitial. We capture cf_clearance during the WebView
    // sign-in and forward it on every request, with the WebView pinned to the handler's UA so the
    // clearance is reusable, and a short session window so we re-sign-in before clearance expires.

    protected override bool RequiresCloudflareClearance => true;

    /// <summary>Sign the WebView in with the exact UA the C# handler sends, so the captured
    /// cf_clearance is valid when the handler reuses it.</summary>
    protected override string? SignInUserAgentOverride => DefaultHttpHandlerFactory.DefaultUserAgent;

    /// <summary>Cloudflare managed-challenge clearance lasts ≈30 min; re-sign-in a bit sooner so the
    /// forwarded cf_clearance is always fresh.</summary>
    protected override TimeSpan SignInSessionLifetime => TimeSpan.FromMinutes(20);
}
