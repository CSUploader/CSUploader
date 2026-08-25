// <copyright file="AppVersion.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;

namespace CSUploader.Lib;

/// <summary>
/// The running application's version, for anything that shows it or compares against it.
/// </summary>
/// <remarks>
/// <b>One resolver, because there is one right answer and an easy wrong one.</b> The obvious source,
/// <c>Assembly.GetName().Version</c>, is wrong for this app: the head's csproj pins
/// <c>AssemblyVersion</c> to a literal, so release.yml's <c>-p:Version=</c> never reaches it and a
/// shipped 1.6.0 still reports whatever was last checked in. Two callers derived a version
/// independently and disagreed about it, which is how the About box came to advertise a version the
/// updater knew was stale.
/// </remarks>
public static class AppVersion
{
    /// <summary>
    /// The answer when the running assembly carries no version at all. Callers that COMPARE versions
    /// must treat this as "unknown" rather than as a real version — every published release sorts
    /// above it, so believing it would mean finding an update every time, for ever.
    /// </summary>
    public const string Unknown = "0.0.0";

    /// <summary>
    /// The running app's version, e.g. <c>1.5.0</c>. Resolved once.
    /// </summary>
    public static string Current { get; } = Resolve();

    /// <summary>
    /// Reads <c>AssemblyInformationalVersion</c>, which unlike <c>AssemblyVersion</c> IS derived from
    /// the <c>Version</c> property and therefore follows the release tag.
    /// </summary>
    /// <remarks>
    /// The <c>+&lt;sha&gt;</c> the SDK appends is build metadata, not part of the semantic version,
    /// and is trimmed. Everything before it — prerelease tags included — is kept, because those DO
    /// order. The entry assembly is the app; the executing one is only a fallback for hosts that
    /// have no entry assembly at all.
    /// </remarks>
    private static string Resolve()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? Unknown;
    }
}
