using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Enums;

namespace ClassPixelPro.Views;

public partial class DataTransferWindow : MyWindow
{
    public int ImportStage { init; get; } = -1;
    
    public string? ImportName { get; init; }

    private DataTransferPage _dataTransferPage;
    
    
    public DataTransferWindow()
    {
        InitializeComponent();
        Content = _dataTransferPage = new DataTransferPage();
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = -1;
            SystemDecorations = SystemDecorations.Full;
        }
    }

    public async Task PerformClassPixelProImport(string root, ImportEntries importEntries)
    {
#pragma warning disable CS0612 // 类型或成员已过时
        await _dataTransferPage.PerformClassPixelProImport(root, importEntries);
#pragma warning restore CS0612 // 类型或成员已过时
    }
    
    public async Task PerformClassPixelPro2Import(string root, ImportEntries importEntries)
    {
        await _dataTransferPage.PerformClassPixelPro2Import(root, importEntries);
    }

    private void Control_OnLoaded(object? sender, RoutedEventArgs e)
    {
        
    }

    public void ImportComplete(bool importV1)
    {
        _dataTransferPage.ViewModel.PageIndex = 4;
        _dataTransferPage.ImportComplete(importV1);
    }
}