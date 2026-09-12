using System.Collections.ObjectModel;
using System.Windows;
using Avalonia.Threading;
using ClassPixelPro.Core.Abstractions.Services.Logging;
using ClassPixelPro.Core.Models.Logging;
using DynamicData;

namespace ClassPixelPro.Services.Logging;

public class AppLogService : IAppLogService
{
    public static readonly int MaxLogEntries = 1000;

    public SourceList<LogEntry> Logs { get; } = new();

    public void AddLog(LogEntry log)
    {
        var dispatcher = Dispatcher.UIThread;
        _ = dispatcher?.InvokeAsync(() =>
        {
            Logs.Add(log);
            while (Logs.Count > MaxLogEntries)
            {
                Logs.RemoveAt(0);
            }
        });
    }
}