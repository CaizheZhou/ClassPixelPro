using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Controls;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Core.Models.UI;
using ClassPixelPro.ViewModels;
using FluentAvalonia.UI.Controls;

namespace ClassPixelPro.Views.WelcomePages;

public partial class WelcomePage : UserControl, IWelcomePage
{
    public WelcomeViewModel ViewModel { get; set; } = null!;
    
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void ButtonNext_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateNext();
    }

    private void NavigateNext()
    {
        this.ShowToast(new ToastMessage()
        {
            Title = "欢迎使用 ClassPixelPro",
            Message = "感谢您选择 ClassPixelPro，祝您使用愉快！",
            AutoClose = false,
            Severity = InfoBarSeverity.Warning
        });
        WelcomeWindow.WelcomeNavigateForwardCommand.Execute(this);
    }

    private void Intro_OnAnimationEnd(object? sender, EventArgs e)
    {
        ContentRoot.Classes.Add("anim");
    }

    private void ButtonDataMigration_OnClick(object? sender, RoutedEventArgs e)
    {
        var welcomeWindow = TopLevel.GetTopLevel(this) as WelcomeWindow;
        if (welcomeWindow == null)
        {
            return;
        }

        welcomeWindow.Pages.Clear();
        welcomeWindow.Pages.AddRange([typeof(WelcomePage), typeof(LicensePage), typeof(DataTransferPage)]);
        
        NavigateNext();
    }

    private void ButtonEnterRecovery_OnClick(object? sender, RoutedEventArgs e)
    {
        AppBase.Current.Restart(["-m", "-r"]);
    }

    private void ButtonJoinManagement_OnClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new JoinManagementDialog();
        dialog.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }
}