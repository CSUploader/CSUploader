// <copyright file="LocExtensionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CSUploader.Lib.Localization;

namespace CSUploader.Tests.Avalonia.Localization;

/// <summary>
/// Prep-item-8 verification of the Avalonia <see cref="LocExtension"/>: a <c>{loc:Loc key}</c>
/// binding must resolve to the active-culture value AND live-update when a culture switch raises
/// <see cref="Localizer"/>'s <c>PropertyChanged("Item[]")</c> — the invalidation chain 20 later
/// view ports depend on. Covered against BOTH target-property kinds: a styled property
/// (<see cref="TextBlock.Text"/>) and a DirectProperty (<see cref="DataGridColumn.Header"/>, the
/// grid-header case, Reality-check register #7). <see cref="Localizer.Instance"/> is process-global,
/// so every culture-touching test restores the original culture in a <c>finally</c>.
/// </summary>
public class LocExtensionTests
{
    [AvaloniaFact]
    public void CultureSwitch_ReEvaluatesLocBinding_OnStyledProperty()
    {
        CultureInfo original = Localizer.Instance.Culture;
        try
        {
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            var textBlock = (TextBlock)AvaloniaRuntimeXamlLoader.Load(
                """
                <TextBlock xmlns="https://github.com/avaloniaui"
                           xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia"
                           Text="{loc:Loc Main_Tab_Uploads}" />
                """);
            var window = new Window { Content = textBlock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Uploads", textBlock.Text);              // en neutral value (i18n-inventory.md:82)

            // The binding subscribes to Localizer.Instance weakly (the leak guard in
            // TornDownBinding_DoesNotLeakOntoLocalizerSingleton). Force a full GC here, while the control
            // is still rooted (textBlock local + shown window): a LIVE control's weak subscription must
            // survive collection and still re-evaluate below. This pins that the leak fix isn't
            // over-weakened into dropping updates on controls that are actually alive.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("アップロード", textBlock.Text);           // ja satellite (i18n-inventory.ja.md:81)
            window.Close();
        }
        finally
        {
            Localizer.Instance.Culture = original;
        }
    }

    [AvaloniaFact]
    public void CultureSwitch_ReEvaluatesLocBinding_OnDirectProperty()
    {
        // The grid-header case: DataGridColumn.Header is a DirectProperty<DataGridColumn, object>
        // (bindable, OneWay default). This test answers the one open question from Reality-check #7 —
        // does the explicit-Source loc binding not only ATTACH but also live-update on Item[]
        // invalidation? Assert both the initial localized header and the post-switch retitle.
        CultureInfo original = Localizer.Instance.Culture;
        try
        {
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            var dataGrid = (DataGrid)AvaloniaRuntimeXamlLoader.Load(
                """
                <DataGrid xmlns="https://github.com/avaloniaui"
                          xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia">
                  <DataGrid.Columns>
                    <DataGridTextColumn Header="{loc:Loc Main_Tab_Uploads}" />
                  </DataGrid.Columns>
                </DataGrid>
                """);
            var window = new Window { Content = dataGrid };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            DataGridColumn column = dataGrid.Columns[0];
            Assert.Equal("Uploads", column.Header);               // en neutral value

            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("アップロード", column.Header);            // live-updated on Item[] invalidation
            window.Close();
        }
        finally
        {
            Localizer.Instance.Culture = original;
        }
    }

    [AvaloniaFact]
    public void TornDownBinding_DoesNotLeakOntoLocalizerSingleton()
    {
        // A {loc:Loc} binding subscribes to the process-lifetime Localizer.Instance.PropertyChanged.
        // Avalonia releases an observable-binding subscription only on an explicit unbind — which
        // virtualized DataGrid rows and TabControl content regeneration never do — so if the handler
        // held its observer strongly, every bound control graph ever created would be pinned onto the
        // singleton forever and each culture switch would walk an ever-growing invocation list.
        // LocExtension's WeakSubscription holds the observer weakly and self-prunes on the next switch;
        // this pins that behavior via the singleton's PropertyChanged subscriber count (reflection).
        CultureInfo original = Localizer.Instance.Culture;
        try
        {
            // Settle: collect any controls left by earlier tests, then a culture switch prunes their
            // now-dead weak handlers — so the baseline reflects only live subscribers.
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            Collect();
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("ja");
            int baseline = SubscriberCount();

            // A bound control that nothing else roots (the helper returns only a weak reference).
            WeakReference weak = CreateBoundControl();
            Assert.Equal(baseline + 1, SubscriberCount()); // the loc binding subscribed

            // Tear down + collect: the control (and its binding/observer) must be reclaimable.
            Collect();
            Assert.False(weak.IsAlive, "the bound control was retained — LocExtension is pinning it onto Localizer.Instance");

            // A culture switch now finds the observer dead and self-prunes back to baseline.
            Localizer.Instance.Culture = CultureInfo.GetCultureInfo("en");
            Assert.Equal(baseline, SubscriberCount());
        }
        finally
        {
            Localizer.Instance.Culture = original;
        }
    }

    // Kept in a non-inlined helper so the strong reference to the control does not survive on the
    // caller's stack frame across the GC below.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateBoundControl()
    {
        var textBlock = (TextBlock)AvaloniaRuntimeXamlLoader.Load(
            """
            <TextBlock xmlns="https://github.com/avaloniaui"
                       xmlns:loc="clr-namespace:CSUploader.Lib.Localization;assembly=CSUploader.Avalonia"
                       Text="{loc:Loc Main_Tab_Uploads}" />
            """);
        _ = textBlock.Text; // force the binding to activate (subscribe) if it is lazy
        return new WeakReference(textBlock);
    }

    private static void Collect()
    {
        Dispatcher.UIThread.RunJobs(); // drain any queued work that could transiently root the observer
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // The compiler-generated backing field of Localizer's field-like PropertyChanged event; its
    // invocation-list length is the number of live subscribers.
    private static int SubscriberCount()
    {
        FieldInfo field = typeof(Localizer).GetField(
            nameof(Localizer.PropertyChanged), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (PropertyChangedEventHandler?)field.GetValue(Localizer.Instance);
        return handler?.GetInvocationList().Length ?? 0;
    }
}
