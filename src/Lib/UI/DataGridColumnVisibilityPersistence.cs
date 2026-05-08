// <copyright file="DataGridColumnVisibilityPersistence.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CSUploader.Dal;

namespace CSUploader.Lib.UI;

/// <summary>
/// Persists per-DataGrid column state — visibility and order — into a single Setting row
/// keyed by name. The string column Header is the identity; keep headers stable in XAML
/// or the persisted entry stops matching and the column reverts to defaults on next
/// launch.
/// Persisted format per entry: <c>Header=visible|displayIndex</c>, joined with commas.
/// <c>visible</c> is <c>1</c> (Visible) or <c>0</c> (Collapsed); <c>displayIndex</c> is
/// the column's position when the user last saw the grid. Both halves are mandatory.
/// </summary>
public static class DataGridColumnVisibilityPersistence
{
    private const char EntrySeparator = ',';
    private const char KeyValueSeparator = '=';
    private const char FieldSeparator = '|';
    private const string VisibleMarker = "1";
    private const string CollapsedMarker = "0";

    /// <summary>
    /// Persisted state for a single column. <see cref="DisplayIndex"/> mirrors WPF's
    /// <see cref="DataGridColumn.DisplayIndex"/> — the column's position after any
    /// header drag-reorder.
    /// </summary>
    public sealed record ColumnState(bool Visible, int DisplayIndex);

    /// <summary>
    /// Reads the persisted per-column overrides (visibility + display index). Columns
    /// absent from the map use their XAML-default state. DB-facing half — separated from
    /// <see cref="ApplyAsync"/> so unit tests don't need a WPF DataGrid (STA-affine).
    /// </summary>
    public static async Task<Dictionary<string, ColumnState>> LoadOverridesAsync(SettingRepository repo, string settingKey, CancellationToken cancellationToken = default)
    {
        SettingDto? row = await repo.FindByKeyAsync(settingKey, cancellationToken);
        Dictionary<string, ColumnState> map = new(StringComparer.Ordinal);
        if (row?.Value is not { Length: > 0 })
        {
            return map;
        }

        foreach (string raw in row.Value.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.LastIndexOf(KeyValueSeparator);
            if (eq <= 0)
            {
                continue;
            }

            string header = raw[..eq].Trim();
            string payload = raw[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(payload))
            {
                continue;
            }

            // Split visible|displayIndex; tolerate older rows that only stored the
            // visibility marker by defaulting DisplayIndex to -1 (= "leave at XAML
            // default").
            int bar = payload.IndexOf(FieldSeparator);
            string visibilityToken = bar < 0 ? payload : payload[..bar];
            string indexToken = bar < 0 ? string.Empty : payload[(bar + 1)..];

            bool visible = visibilityToken == VisibleMarker;
            int displayIndex = -1;
            if (indexToken.Length > 0
                && int.TryParse(indexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                && parsed >= 0)
            {
                displayIndex = parsed;
            }

            map[header] = new ColumnState(visible, displayIndex);
        }

        return map;
    }

    /// <summary>
    /// Upserts the per-column override map into the setting row.
    /// </summary>
    public static async Task SaveOverridesAsync(SettingRepository repo, string settingKey, IDictionary<string, ColumnState> overrides, CancellationToken cancellationToken = default)
    {
        string value = string.Join(
            EntrySeparator,
            overrides
                .Where(kv => !string.IsNullOrEmpty(kv.Key))
                .Select(kv => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{kv.Key}{KeyValueSeparator}{(kv.Value.Visible ? VisibleMarker : CollapsedMarker)}{FieldSeparator}{kv.Value.DisplayIndex}")));

        SettingDto? existing = await repo.FindByKeyAsync(settingKey, cancellationToken);
        if (existing is null)
        {
            await repo.InsertAsync(new SettingDto { Key = settingKey, Value = value }, cancellationToken);
        }
        else
        {
            existing.Value = value;
            await repo.UpdateAsync(existing, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the persisted overrides and applies them to the matching columns. Columns
    /// without an override are left at their XAML-default state, so a freshly-added
    /// column ships with whatever the developer chose without surprising existing users.
    /// Order is restored by setting <see cref="DataGridColumn.DisplayIndex"/> on each
    /// column in ascending order of the persisted index.
    /// </summary>
    public static async Task ApplyAsync(DataGrid grid, SettingRepository repo, string settingKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ColumnState> overrides = await LoadOverridesAsync(repo, settingKey, cancellationToken);
        if (overrides.Count == 0)
        {
            return;
        }

        // Visibility first — it's order-independent.
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? string.Empty;
            if (overrides.TryGetValue(header, out ColumnState? state))
            {
                column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Display order. Pair each column with its target index, sort ascending so
        // assignments don't shuffle past columns we haven't placed yet, then assign.
        // Columns without a persisted index keep their XAML-default DisplayIndex.
        List<(DataGridColumn Column, int TargetIndex)> withIndex = [];
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? string.Empty;
            if (overrides.TryGetValue(header, out ColumnState? state) && state.DisplayIndex >= 0)
            {
                withIndex.Add((column, state.DisplayIndex));
            }
        }

        int max = grid.Columns.Count - 1;
        foreach ((DataGridColumn column, int index) in withIndex.OrderBy(p => p.TargetIndex))
        {
            int clamped = Math.Clamp(index, 0, max);
            if (column.DisplayIndex != clamped)
            {
                column.DisplayIndex = clamped;
            }
        }
    }

    /// <summary>
    /// Snapshots the current column state into a defaults map for later restoration.
    /// Call this before <see cref="ApplyAsync"/> so the captured values reflect the
    /// XAML defaults rather than any persisted overrides. Uses the column's index in
    /// <see cref="DataGrid.Columns"/> rather than <c>DisplayIndex</c> — at Loaded time
    /// WPF may not have auto-assigned DisplayIndex yet (still <c>-1</c>), so the
    /// collection index is the reliable source of "the order the developer wrote in XAML".
    /// </summary>
    public static Dictionary<string, ColumnState> CaptureCurrentState(DataGrid grid)
    {
        Dictionary<string, ColumnState> map = new(StringComparer.Ordinal);
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            DataGridColumn column = grid.Columns[i];
            string header = column.Header?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            map[header] = new ColumnState(
                column.Visibility == Visibility.Visible,
                i);
        }

        return map;
    }

    /// <summary>
    /// Resets the grid to the supplied state and removes the persisted overrides so a
    /// later <see cref="ApplyAsync"/> is a no-op. Called by the "Reset columns" menu
    /// entry after capturing the XAML defaults at first Loaded.
    /// </summary>
    public static async Task ResetAsync(DataGrid grid, IDictionary<string, ColumnState> defaults, SettingRepository repo, string settingKey, CancellationToken cancellationToken = default)
    {
        // Visibility first (order-independent).
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? string.Empty;
            if (defaults.TryGetValue(header, out ColumnState? state))
            {
                column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Display order. Same ascending-assignment trick as ApplyAsync so each column
        // ends up where it started.
        List<(DataGridColumn Column, int TargetIndex)> withIndex = [];
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? string.Empty;
            if (defaults.TryGetValue(header, out ColumnState? state) && state.DisplayIndex >= 0)
            {
                withIndex.Add((column, state.DisplayIndex));
            }
        }

        int max = grid.Columns.Count - 1;
        foreach ((DataGridColumn column, int index) in withIndex.OrderBy(p => p.TargetIndex))
        {
            int clamped = Math.Clamp(index, 0, max);
            if (column.DisplayIndex != clamped)
            {
                column.DisplayIndex = clamped;
            }
        }

        // Drop the persisted overrides so the next launch starts clean.
        await SaveOverridesAsync(repo, settingKey, new Dictionary<string, ColumnState>(), cancellationToken);
    }

    /// <summary>
    /// Snapshots every column's current visibility and display index into the override
    /// map and upserts the row. Called from the column-toggle menu's Click handler and
    /// from <see cref="DataGrid.ColumnDisplayIndexChanged"/> so each interaction
    /// persists without needing a "Save" button.
    /// </summary>
    public static Task PersistAsync(DataGrid grid, SettingRepository repo, string settingKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, ColumnState> overrides = new(StringComparer.Ordinal);
        foreach (DataGridColumn column in grid.Columns)
        {
            string header = column.Header?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            overrides[header] = new ColumnState(
                column.Visibility == Visibility.Visible,
                column.DisplayIndex);
        }

        return SaveOverridesAsync(repo, settingKey, overrides, cancellationToken);
    }
}
