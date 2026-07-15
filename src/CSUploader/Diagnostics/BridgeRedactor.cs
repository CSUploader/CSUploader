#if AVA_BRIDGE
using AvaDevBridge.Contracts;

namespace CSUploader.Diagnostics;

/// <summary>
/// Belt-and-braces agent-safety redactor for the AvaDevBridge dev loop. The bridge's built-in
/// secret regex already hides password|token|secret|apikey|authorization|bearer|pin|otp-shaped
/// property names on its own, so this only ADDS the CSUploader-specific credential shapes that
/// regex misses — session cookies and per-account user-hash tokens — and masks any value the
/// bridge decides to hide. Wrapped in AVA_BRIDGE (references a bridge contract type); compiled
/// only in Debug builds that opt into the bridge via Directory.Build.local.props.
/// </summary>
internal sealed class BridgeRedactor : ISensitiveDataRedactor
{
    public bool ShouldHide(string ownerType, string name) =>
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("userhash", StringComparison.OrdinalIgnoreCase);

    public string Mask(string value) => "«redacted»";
}
#endif
