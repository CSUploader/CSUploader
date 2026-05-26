// <copyright file="ExLoadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Ex-Load. Pure config — the upload protocol lives in
/// <see cref="XFileSharingApiPipeline"/>. To add another XFileSharing-API hoster (e.g.
/// FilesMonster, RareFile, KatFile if their APIs match this convention), subclass the
/// base with a similar two-line shim and register in DI.
/// </summary>
public sealed class ExLoadPipeline : XFileSharingApiPipeline
{
    public ExLoadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so existing test fixtures
    /// can continue to drive ExLoadPipeline through canned responses.</summary>
    internal ExLoadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "ExLoad";

    protected override string Host => "https://ex-load.com";
}
