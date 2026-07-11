// <copyright file="DataGridColumnMenuTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CSUploader.Lib.UI;
using CSUploader.Services;
using Moq;
using ColumnState = CSUploader.Lib.UI.DataGridColumnVisibilityPersistence.ColumnState;

namespace CSUploader.Tests.Avalonia.Lib;

/// <summary>
/// The shared column-toggle menu (<see cref="DataGridColumnMenu"/>) — the single Avalonia copy
/// of the WPF head's two near-identical per-view builders. Verifies the item shape, the
/// checkmark tracking (build-time and the Opened refresh), that a toggle persists, and the
/// reset-confirmation plumbing through <see cref="IDialogService"/>.
/// </summary>
public class DataGridColumnMenuTests : SqliteSettingHarness
{
    private const string ResetMessageKey = "Uploaded_ResetColumns_Message";
    private const string ResetTitleKey = "Uploaded_ResetColumns_Title";

    // Faithful replay of DefaultMenuInteractionHandler.Click for a CheckBox item: flip IsChecked
    // first (what the framework does on a real click), THEN raise the Click routed event that the
    // menu's handler listens on. (Confirmed against the installed 11.3.18 by decompilation.)
    private static void SimulateClick(MenuItem item)
    {
        if (item.ToggleType == MenuItemToggleType.CheckBox)
        {
            item.IsChecked = !item.IsChecked;
        }

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    // The toggle/reset Click handlers are async void (matching the WPF originals): the visibility
    // flip is synchronous but the persist runs behind an await. Pump the dispatcher so the EF
    // continuation (if it yielded) drains before we assert the store.
    private static async Task PumpAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }

    // menu.Items is object?-typed; assert the shape and hand back a non-null MenuItem in one step.
    private static MenuItem Item(ContextMenu menu, int index) => Assert.IsType<MenuItem>(menu.Items[index]);

    private ContextMenu BuildMenu(DataGrid grid, IDialogService dialogService)
        => DataGridColumnMenu.Build(
            grid,
            DataGridColumnVisibilityPersistence.CaptureCurrentState(grid),
            Repo,
            "k",
            dialogService,
            ResetMessageKey,
            ResetTitleKey);

    [AvaloniaFact]
    public void Build_YieldsColumnItemsPlusSeparatorAndReset_FirstColumnDisabled()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            ContextMenu menu = BuildMenu(grid, Mock.Of<IDialogService>());

            // 4 column items + separator + reset = 6.
            Assert.Equal(6, menu.Items.Count);
            for (int i = 0; i < 4; i++)
            {
                MenuItem columnItem = Assert.IsType<MenuItem>(menu.Items[i]);
                Assert.Equal(MenuItemToggleType.CheckBox, columnItem.ToggleType); // glyph lever (port rule 31)
            }

            Assert.IsType<Separator>(menu.Items[4]);
            Assert.IsType<MenuItem>(menu.Items[5]);

            // The anchor column stays visible — its toggle is disabled.
            Assert.False(Item(menu, 0).IsEnabled);
            Assert.True(Item(menu, 1).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Build_And_Opened_TrackColumnVisibility()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            grid.Columns[2].IsVisible = false; // C hidden BEFORE build

            ContextMenu menu = BuildMenu(grid, Mock.Of<IDialogService>());

            Assert.True(Item(menu, 0).IsChecked);
            Assert.True(Item(menu, 1).IsChecked);
            Assert.False(Item(menu, 2).IsChecked); // C reflects hidden
            Assert.True(Item(menu, 3).IsChecked);

            // Change visibility externally, then reopen — the Opened refresh re-reads both directions.
            grid.Columns[1].IsVisible = false; // hide B
            grid.Columns[2].IsVisible = true;  // show C
            menu.RaiseEvent(new RoutedEventArgs(MenuBase.OpenedEvent));

            Assert.False(Item(menu, 1).IsChecked);
            Assert.True(Item(menu, 2).IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ToggleColumnItem_HidesColumn_AndPersists()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            ContextMenu menu = BuildMenu(grid, Mock.Of<IDialogService>());

            SimulateClick(Item(menu, 1)); // toggle B off
            Assert.False(grid.Columns[1].IsVisible); // synchronous flip

            await PumpAsync();

            Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
            Assert.False(parsed["B"].Visible);
            Assert.True(parsed["A"].Visible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResetItem_Confirmed_RestoresDefaultsAndClearsRow()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.ShowOptOutConfirmationAsync(ConfirmationKeys.ResetColumns, It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            ContextMenu menu = BuildMenu(grid, dialog.Object);

            // Mutate + persist a hidden column, then reset.
            grid.Columns[1].IsVisible = false;
            await DataGridColumnVisibilityPersistence.PersistAsync(grid, Repo, "k");

            SimulateClick(Item(menu, 5)); // the Reset entry
            await PumpAsync();

            Assert.True(grid.Columns[1].IsVisible); // restored
            dialog.Verify(
                d => d.ShowOptOutConfirmationAsync(ConfirmationKeys.ResetColumns, It.IsAny<string>(), It.IsAny<string>()),
                Times.Once);

            Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
            Assert.Empty(parsed); // row cleared
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResetItem_Declined_LeavesStateAndRowUntouched()
    {
        (Window window, DataGrid grid) = GridTestFactory.BuildShownGrid("A", "B", "C", "D");
        try
        {
            Mock<IDialogService> dialog = new();
            dialog.Setup(d => d.ShowOptOutConfirmationAsync(ConfirmationKeys.ResetColumns, It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(false);

            ContextMenu menu = BuildMenu(grid, dialog.Object);

            grid.Columns[1].IsVisible = false;
            await DataGridColumnVisibilityPersistence.PersistAsync(grid, Repo, "k");

            SimulateClick(Item(menu, 5)); // Reset, but the prompt is declined
            await PumpAsync();

            Assert.False(grid.Columns[1].IsVisible); // unchanged

            Dictionary<string, ColumnState> parsed = await DataGridColumnVisibilityPersistence.LoadOverridesAsync(Repo, "k");
            Assert.False(parsed["B"].Visible); // the persisted override is still present
        }
        finally
        {
            window.Close();
        }
    }
}
