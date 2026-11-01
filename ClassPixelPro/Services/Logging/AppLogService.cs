using System.Collections.Concurrent;
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

    // Avalonia 调度器就绪前（宿主构建、早期初始化阶段）产生的日志先进队列，
    // 第一条 UI 线程日志写入时统一补进窗口——之前这些日志会被静默丢弃，
    // 表现为「启动后一段时间日志窗口什么都没有」。
    private static readonly ConcurrentQueue<LogEntry> PendingEntries = new();

    public void AddLog(LogEntry log)
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher == null)
        {
            PendingEntries.Enqueue(log);
            return;
        }
        _ = dispatcher.InvokeAsync(() => Append(log));
    }

    private void Append(LogEntry log)
    {
        while (PendingEntries.TryDequeue(out var pending))
        {
            Logs.Add(pending);
        }
        Logs.Add(log);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(0);
        }
    }
}