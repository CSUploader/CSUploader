// <copyright file="DialogServiceBase.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Services;

/// <summary>
/// Shared base for head <see cref="IDialogService"/> implementations. Owns the opt-out
/// confirmation flow — the suppression-gate check, the "don't ask again" set mutation, and its
/// fire-and-forget CSV write-back — so every head implements only the raw dialog via
/// <see cref="ShowOptOutConfirmationCoreAsync"/>. All other <see cref="IDialogService"/> members
/// are head-specific and stay on the derived class (which declares the interface; this base's
/// public <see cref="ShowOptOutConfirmationAsync"/> satisfies that one member through inheritance).
/// </summary>
public abstract class DialogServiceBase(AppSettings settings, SettingRepository settingRepository)
{
    /// <summary>The shared application settings, exposed so derived heads read them through the
    /// base rather than capturing their own copy of the constructor parameter (which would
    /// double-capture it alongside this base — a CS9107 warning).</summary>
    protected AppSettings Settings => settings;

    public async Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null)
    {
        if (settings.SuppressedConfirmations.Contains(confirmationKey))
        {
            return true;
        }

        (bool confirmed, bool dontAskAgain) = await ShowOptOutConfirmationCoreAsync(message, title ?? Localizer.Instance["Common_Confirm"]);
        if (!confirmed)
        {
            return false;
        }

        if (dontAskAgain)
        {
            settings.SuppressedConfirmations.Add(confirmationKey);

            // Fire-and-forget the DB write; if it fails the user will just be asked again
            // on next action, which is an acceptable fallback.
            _ = PersistSuppressedAsync();
        }

        return true;
    }

    /// <summary>
    /// Shows the raw opt-out confirmation dialog (a message plus a "Don't ask again" checkbox)
    /// and reports whether the user confirmed and whether they ticked the checkbox. The base
    /// handles suppression lookup and persistence around this call; <paramref name="title"/> is
    /// already resolved (the caller's value or the localized default).
    /// </summary>
    protected abstract Task<(bool Confirmed, bool DontAskAgain)> ShowOptOutConfirmationCoreAsync(string message, string title);

    private async Task PersistSuppressedAsync()
    {
        try
        {
            string value = string.Join(",", settings.SuppressedConfirmations);
            SettingDto? existing = await settingRepository.FindByKeyAsync(SettingKey.SuppressedConfirmations);
            if (existing is not null)
            {
                existing.Value = value;
                await settingRepository.UpdateAsync(existing);
            }
            else
            {
                await settingRepository.InsertAsync(new SettingDto { Key = SettingKey.SuppressedConfirmations, Value = value });
            }
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
