using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Core.Models.Logging;
using ClassPixelPro.Services.Logging;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels;
using DynamicData;
using DynamicData.Binding;
using FluentAvalonia.UI.Data;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using WebSocketSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ClassPixelPro.Views;

/// <summary>
/// AppLogsWindow.xaml 的交互逻辑
/// </summary>
public partial class AppLogsWindow : MyWindow
{
    public static readonly FuncValueConverter<LogLevel, string> LogLevelToIconGlyphConverter = new(x => x switch
    {
        LogLevel.Trace => "\uE50F",
        LogLevel.Debug => "\uE2C7",
        LogLevel.Information => "\uE9E4",
        LogLevel.Warning => "\uF430",
        LogLevel.Error => "\uE808",
        LogLevel.Critical => "\uE84E",
        LogLevel.None => "\uEDF6;",
        _ => throw new ArgumentOutOfRangeException(nameof(x), x, null)
    });
    
    public static readonly FuncValueConverter<LogLevel, string> LogLevelToNameConverter = new(x => x switch
    {
        LogLevel.Trace => "追踪",
        LogLevel.Debug => "调试",
        LogLevel.Information => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        LogLevel.Critical => "灾难",
        LogLevel.None => "？？？",
        _ => throw new ArgumentOutOfRangeException(nameof(x), x, null)
    });
    
    public AppLogsViewModel ViewModel { get; } = IAppHost.GetService<AppLogsViewModel>();

    private bool _isOpened = false;

    public AppLogsWindow()
    {
        InitializeComponent();
        DataContext = this;

        // 新日志到达时自动滚到最后一行：稳定运行期间 Info 级日志稀疏，
        // 不自动滚动的话窗口看起来就像「很久没有产生日志」。
        ((System.Collections.Specialized.INotifyCollectionChanged)ViewModel.Logs).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || ViewModel.Logs.Count == 0) return;
            var last = ViewModel.Logs[^1];
            var column = DataGridMain.Columns.LastOrDefault();
            if (column != null) DataGridMain.ScrollIntoView(last, column);
        };
        RefreshView();
    }

    public void Open()
    {
        if (!_isOpened)
        {
            _isOpened = true;
            Show();
        }
        else
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
    }

    private void LogsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
    }

    private void AppLogsWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }
        e.Cancel = true;
        Hide();
        _isOpened = false;
    }

    private void MainListView_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count <= 0)
            return;
        var o = e.AddedItems[0];
        if (o == null)
            return;
    }

    private void TextBoxBase_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshView();
    }

    private void RefreshView()
    {
        
    }

    private void Selector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshView();
    }


    private void ButtonCopySelectedLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logs = DataGridMain.SelectedItems.Cast<object?>().Select(x => x?.ToString() ?? "").ToList();
            Clipboard?.SetTextAsync(string.Join(Environment.NewLine, logs));
            this.ShowSuccessToast($"已将 {logs.Count} 条日志复制到剪贴板。");
        }
        catch (Exception ex)
        {
            App.GetService<ILogger<AppLogsWindow>>().LogError(ex, "无法复制日志到剪切板。");
            this.ShowErrorToast("无法复制日志到剪切板。", ex);
        }
    }

    private void ButtonClearLogs_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AppLogService.Logs.Clear();
    }
}

