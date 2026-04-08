// <copyright file="AutoScrollBehavior.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace CSUploader.Behaviors;

public static class AutoScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataGrid dataGrid)
        {
            if ((bool)e.NewValue)
            {
                ((INotifyCollectionChanged)dataGrid.Items).CollectionChanged += (s, args) =>
                {
                    if (dataGrid.Items.Count > 0)
                    {
                        dataGrid.ScrollIntoView(dataGrid.Items[^1]);
                    }
                };
            }
        }
    }
}
