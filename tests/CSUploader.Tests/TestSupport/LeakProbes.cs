// <copyright file="LeakProbes.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Specialized;
using System.Reflection;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Reflection probe shared by the subscription-leak tests. Hoisted from DataGridBehaviorTests /
/// LogsViewTests / UploadedViewTests so the trick lives in one place — and so the
/// <c>ReflectionContractTests.ObservableCollection_CollectionChangedBackingField_StillExists</c> canary
/// has a single named dependent to guard.
/// </summary>
internal static class LeakProbes
{
    /// <summary>
    /// The invocation-list length of an <see cref="ObservableCollection{T}"/>'s field-like
    /// <c>CollectionChanged</c> event = its live subscriber count. Reading the compiler-generated backing
    /// field is the only way to see how many handlers are attached; a framework rename would make this
    /// measure 0 (guarded by the reflection-contract canary).
    /// </summary>
    internal static int CollectionChangedSubscriberCount(INotifyCollectionChanged source)
    {
        FieldInfo field = source.GetType().GetField(
            "CollectionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (NotifyCollectionChangedEventHandler?)field.GetValue(source);
        return handler?.GetInvocationList().Length ?? 0;
    }
}
