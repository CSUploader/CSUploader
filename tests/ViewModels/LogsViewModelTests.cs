// <copyright file="LogsViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.ViewModels;

namespace CSUploader.Tests.ViewModels;

public class LogsViewModelTests
{
    [Fact]
    public void ClearStatusLogs_RemovesOnlyStatusEntries()
    {
        LogsViewModel vm = SeedAllTypes();

        vm.ClearStatusLogsCommand.Execute(null);

        Assert.Empty(vm.StatusLogs);
        Assert.Single(vm.HttpLogs);
        Assert.Single(vm.ErrorLogs);
        Assert.Single(vm.UILogs);
    }

    [Fact]
    public void ClearHttpLogs_RemovesOnlyHttpEntries()
    {
        LogsViewModel vm = SeedAllTypes();

        vm.ClearHttpLogsCommand.Execute(null);

        Assert.Single(vm.StatusLogs);
        Assert.Empty(vm.HttpLogs);
        Assert.Single(vm.ErrorLogs);
        Assert.Single(vm.UILogs);
    }

    [Fact]
    public void ClearErrorLogs_RemovesOnlyErrorEntries()
    {
        LogsViewModel vm = SeedAllTypes();

        vm.ClearErrorLogsCommand.Execute(null);

        Assert.Single(vm.StatusLogs);
        Assert.Single(vm.HttpLogs);
        Assert.Empty(vm.ErrorLogs);
        Assert.Single(vm.UILogs);
    }

    [Fact]
    public void ClearUILogs_RemovesOnlyUIEntries()
    {
        LogsViewModel vm = SeedAllTypes();

        vm.ClearUILogsCommand.Execute(null);

        Assert.Single(vm.StatusLogs);
        Assert.Single(vm.HttpLogs);
        Assert.Single(vm.ErrorLogs);
        Assert.Empty(vm.UILogs);
    }

    private static LogsViewModel SeedAllTypes()
    {
        LogsViewModel vm = new();
        foreach (LogType type in Enum.GetValues<LogType>())
        {
            vm.AddLogEntry(new LogEvent { LogType = type, DateTime = DateTime.Now, Message = type.ToString() });
        }

        return vm;
    }
}
