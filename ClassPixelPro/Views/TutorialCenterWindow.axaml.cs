using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Core.Models.Tutorial;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.ViewModels;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Sentry;

namespace ClassPixelPro.Views;

public partial class TutorialCenterWindow : MyWindow
{
    public TutorialCenterViewModel ViewModel { get; } = IAppHost.GetService<TutorialCenterViewModel>();
    
    private bool IsOpened { get; set; } = false;
    
    public TutorialCenterWindow()
    {
        DataContext = this;
        InitializeComponent();
        
    }
    
    public void Open()
    {
        if (!IsOpened)
        {
            SentrySdk.Metrics.EmitCounter("views.TutorialCenterWindow.open", 1);
            IsOpened = true;
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

    [RelayCommand]
    private void SetSelectedTutorial(Tutorial tutorial)
    {
        ViewModel.SelectedTutorial = tutorial;
    }

    private void ButtonPlaySelectedTutorial_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTutorial != null) 
            ViewModel.TutorialService.BeginTutorial(ViewModel.SelectedTutorial);
    }

    [RelayCommand]
    private void PlaySelectedParagraph(TutorialParagraph paragraph)
    {
        if (ViewModel.SelectedTutorial != null) 
            ViewModel.TutorialService.JumpToParagraph(ViewModel.SelectedTutorial, paragraph);
    }

    private void ButtonStopCurrentTutorial_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.TutorialService.StopTutorial();
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        IsOpened = false;
    }
}