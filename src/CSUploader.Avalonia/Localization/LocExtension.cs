// <copyright file="LocExtension.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Avalonia; // AvaloniaObjectExtensions.ToBinding
using Avalonia.Data;

namespace CSUploader.Lib.Localization;

/// <summary>
/// Avalonia twin of the WPF head's LocExtension (src/Lib/Localization/LocExtension.cs):
/// <c>{loc:Loc Common_OK}</c> produces a one-way binding to the localized value for a key, so a
/// live culture switch re-evaluates every bound value in place. Same namespace as the WPF
/// extension ON PURPOSE — ported XAML keeps its xmlns:loc declaration unchanged. Avalonia markup
/// extensions are duck-typed: no base class, just a ProvideValue method; returning an
/// <see cref="IBinding"/> makes the XAML loader attach it as a binding to the target (styled or
/// direct) property.
/// </summary>
/// <remarks>
/// NOT a <c>{Binding [key], Source=Localizer.Instance}</c> indexer binding (the shape the plan
/// sketched and the WPF head uses): verified against Avalonia 11.3.18, the reflection indexer node
/// does NOT re-read on <see cref="Localizer"/>'s <c>PropertyChanged("Item[]")</c>/<c>("")</c>
/// invalidation — the initial value resolves but never live-updates (proven for both a styled
/// property and a DataGridColumn.Header DirectProperty; Reality-check register #7/#10). A plain-
/// property binding DOES honor its notification, so we bind to an observable that re-emits the
/// localized value on every culture change instead. This keeps live switching working for every
/// target-property kind uniformly.
/// </remarks>
public sealed class LocExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public object ProvideValue(IServiceProvider serviceProvider)
        => new LocalizedValueObservable(Key).ToBinding();

    /// <summary>
    /// Cold observable that emits <c>Localizer.Instance[key]</c> on subscribe and re-emits it on
    /// every <see cref="Localizer.PropertyChanged"/> (Localizer raises it only on culture change),
    /// so the OneWay binding pushes the new value to its target. The subscription is released when
    /// the binding is torn down, unhooking the Localizer event — no leak on the app-lifetime singleton.
    /// </summary>
    private sealed class LocalizedValueObservable(string key) : IObservable<object?>
    {
        public IDisposable Subscribe(IObserver<object?> observer)
        {
            PropertyChangedEventHandler handler = (_, _) => observer.OnNext(Localizer.Instance[key]);
            Localizer.Instance.PropertyChanged += handler;
            observer.OnNext(Localizer.Instance[key]); // seed the current-culture value
            return new Unsubscriber(() => Localizer.Instance.PropertyChanged -= handler);
        }

        private sealed class Unsubscriber(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
