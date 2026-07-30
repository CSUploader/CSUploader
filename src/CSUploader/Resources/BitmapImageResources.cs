// <copyright file="BitmapImageResources.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CSUploader.Resources;

/// <summary>
/// Code-built twin of the WPF ImageResources.xaml bitmap entries (Avalonia has no XAML
/// element for a keyed bitmap resource). Keys are load-bearing: HosterIconConverter
/// computes "FileHoster&lt;Name&gt;Image" at runtime and the status/action converters look
/// these up by name — copy keys 1:1, including the dotted ones (FileHosterStorage.toImage,
/// FileHosterTransfer.itImage, FileHosterFilehoster.ioImage).
/// </summary>
internal static class BitmapImageResources
{
    /// <summary>(key, path under Assets/Images/) — one line per ImageResources.xaml:8-88 entry.</summary>
    internal static readonly (string Key, string Path)[] Entries =
    [
        // Action Images
        ("ActionCancelImage", "action_cancel.png"),
        ("ActionRetryImage", "action_retry.png"),
        ("ActionSkipImage", "action_skip.png"),
        ("ActionResetImage", "action_reset.png"),
        ("ActionFolderImage", "action_folder.png"),
        ("ActionSpeedImage", "action_speed.png"),

        // Button Images
        ("ButtonPauseImage", "button_pause.png"),
        ("ButtonStartImage", "button_start.png"),
        ("ButtonStopImage", "button_stop.png"),

        // Misc Images
        ("GoDownImage", "go_down.png"),

        // Package Images
        ("PackageClosedImage", "package_closed.png"),
        ("PackageOpenImage", "package_open.png"),

        // Status Images (+ Account)
        ("StatusCancelledImage", "status_cancelled.png"),
        ("StatusFailedImage", "status_failed.png"),
        ("StatusHashingImage", "status_hashing.png"),
        ("StatusOkImage", "status_ok.png"),
        ("StatusQueuedImage", "status_queued.png"),
        ("StatusRunningImage", "status_running.png"),
        ("StatusSuccessImage", "status_success.png"),
        ("StatusUploadingImage", "status_uploading.png"),
        ("StatusWarningImage", "status_warning.png"),
        ("AccountImage", "account.png"),

        // File Hoster Images
        // "1Fichier" keeps its leading digit and lowercases the rest, exactly as
        // HosterIconConverter builds the key — hence "1fichier", not "1Fichier".
        ("FileHoster1fichierImage", "FileHosters/filehoster_1fichier.png"),
        ("FileHosterAlfafileImage", "FileHosters/filehoster_alfafile.png"),
        ("FileHosterBruploadImage", "FileHosters/filehoster_brupload.png"),
        ("FileHosterBuzzheavierImage", "FileHosters/filehoster_buzzheavier.png"),
        ("FileHosterCatboxImage", "FileHosters/filehoster_catbox.png"),
        ("FileHosterDropgalaxyImage", "FileHosters/filehoster_dropgalaxy.png"),
        // Keys mirror HosterIconConverter's normalisation (spaces/hyphens dropped, dots KEPT),
        // so "Send.now" resolves to FileHosterSend.nowImage — same shape as Storage.to/Transfer.it.
        ("FileHosterSend.nowImage", "FileHosters/filehoster_send.now.png"),
        ("FileHosterUploadyImage", "FileHosters/filehoster_uploady.png"),
        ("FileHosterExloadImage", "FileHosters/filehoster_exload.png"),
        ("FileHosterExtmatrixImage", "FileHosters/filehoster_extmatrix.png"),
        ("FileHosterFileboomImage", "FileHosters/filehoster_fileboom.png"),
        ("FileHosterFlashbitImage", "FileHosters/filehoster_flashbit.png"),
        ("FileHosterGigapetaImage", "FileHosters/filehoster_gigapeta.png"),
        ("FileHosterFilegardenImage", "FileHosters/filehoster_filegarden.png"),
        ("FileHosterGofileImage", "FileHosters/filehoster_gofile.png"),
        ("FileHosterHexloadImage", "FileHosters/filehoster_hexload.png"),
        ("FileHosterHitfileImage", "FileHosters/filehoster_hitfile.png"),
        ("FileHosterHotlinkImage", "FileHosters/filehoster_hotlink.png"),
        ("FileHosterHxfileImage", "FileHosters/filehoster_hxfile.png"),
        ("FileHosterIcerboxImage", "FileHosters/filehoster_icerbox.png"),
        ("FileHosterIsracloudImage", "FileHosters/filehoster_isracloud.png"),
        ("FileHosterKatfileImage", "FileHosters/filehoster_katfile.png"),
        ("FileHosterKeep2shareImage", "FileHosters/filehoster_keep2share.png"),
        ("FileHosterMegaImage", "FileHosters/filehoster_mega.png"),
        ("FileHosterMediafireImage", "FileHosters/filehoster_mediafire.png"),
        ("FileHosterNitroflareImage", "FileHosters/filehoster_nitroflare.png"),
        ("FileHosterPixeldrainImage", "FileHosters/filehoster_pixeldrain.png"),
        ("FileHosterFilehoster.ioImage", "FileHosters/filehoster_filehoster.io.png"),
        ("FileHosterRapidgatorImage", "FileHosters/filehoster_rapidgator.png"),
        ("FileHosterStorage.toImage", "FileHosters/filehoster_storage.to.png"),
        ("FileHosterTakefileImage", "FileHosters/filehoster_takefile.png"),
        ("FileHosterTezfilesImage", "FileHosters/filehoster_tezfiles.png"),
        ("FileHosterTransfer.itImage", "FileHosters/filehoster_transfer.it.png"),
        ("FileHosterUploadgigImage", "FileHosters/filehoster_uploadgig.png"),
        ("FileHosterUfileImage", "FileHosters/filehoster_ufile.png"),
        ("FileHosterUpstoreImage", "FileHosters/filehoster_upstore.png"),
        ("FileHosterVikingfileImage", "FileHosters/filehoster_vikingfile.png"),
        ("FileHosterWormholeImage", "FileHosters/filehoster_wormhole.png"),

        // Logo Images
        ("LogoCsLogo128Image", "Logo/cs_logo_128_128.png"),
        ("LogoCsLogo256Image", "Logo/cs_logo_256_256.png"),
        ("LogoCsLogo54Image", "Logo/cs_logo_54_54.png"),
        ("LogoCsLogo54TransImage", "Logo/cs_logo_54_54_trans.png"),
        ("LogoCsLogo64Image", "Logo/cs_logo_64_64.png"),
        ("LogoIcon", "Logo/icon.ico"),
        ("Logo128Image", "Logo/logo-128x128.png"),
        ("Logo48Image", "Logo/logo-48x48.png"),
        ("Logo14Image", "Logo/logo_14_14.png"),
        ("Logo15Image", "Logo/logo_15_15.png"),
        ("Logo16Image", "Logo/logo_16_16.png"),
        ("Logo17Image", "Logo/logo_17_17.png"),
        ("Logo18Image", "Logo/logo_18_18.png"),
        ("Logo19Image", "Logo/logo_19_19.png"),
        ("Logo20Image", "Logo/logo_20_20.png"),
    ];

    internal static void MergeInto(IResourceDictionary resources)
    {
        string assembly = typeof(BitmapImageResources).Assembly.GetName().Name!;
        foreach ((string key, string path) in Entries)
        {
            Uri uri = new($"avares://{assembly}/Assets/Images/{path}");
            resources.Add(key, new Bitmap(AssetLoader.Open(uri)));
        }
    }
}
