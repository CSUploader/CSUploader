// <copyright file="DaysTimespanConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Extensions;

public class DaysTimespanConverter : TimeUnitConverterBase
{
    protected override TimeSpan FromValue(double value) => TimeSpan.FromDays(value);
}
