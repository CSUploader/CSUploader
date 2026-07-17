// <copyright file="FakeInteractiveLogin.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Fake <c>interactiveLogin</c> callback for the <c>EditAccountWindow</c> sign-in tests (Phase 5 Task 9): it
/// stands in for the real WebView verifier so a headless test can drive Sign-in through its three outcomes
/// without a browser. <see cref="Success"/> returns a caller-supplied <see cref="AccountCheckResult"/> (built
/// with real named args so a test controls exactly what comes back — API key, derived username, storage);
/// <see cref="Failure"/> returns an invalid result carrying a message + optional detail; <see cref="Throws"/>
/// makes the callback throw (the catch-branch path). <see cref="Callback"/> is the delegate to hand the
/// window ctor / service member; it records <see cref="CallCount"/> and <see cref="LastHoster"/> so a test
/// can assert the sign-in actually ran for the expected hoster.
/// </summary>
internal sealed class FakeInteractiveLogin
{
    private FakeInteractiveLogin(Func<string, Task<AccountCheckResult>> inner) => Callback = inner;

    public int CallCount { get; private set; }

    public string? LastHoster { get; private set; }

    /// <summary>The delegate to pass as the window's <c>interactiveLogin</c>. Records the call before
    /// delegating so a <see cref="Throws"/> callback still bumps <see cref="CallCount"/>.</summary>
    public Func<string, Task<AccountCheckResult>> Callback => hoster =>
    {
        CallCount++;
        LastHoster = hoster;
        return field(hoster);
    };

    public static FakeInteractiveLogin Success(AccountCheckResult result) => new(_ => Task.FromResult(result));

    public static FakeInteractiveLogin Failure(string message, string? detail = null) =>
        new(_ => Task.FromResult(new AccountCheckResult(false, AccountType.Free, Message: message, Detail: detail)));

    public static FakeInteractiveLogin Throws(Exception exception) => new(_ => throw exception);
}
