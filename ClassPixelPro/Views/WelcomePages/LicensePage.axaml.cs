using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Controls;

namespace ClassPixelPro.Views.WelcomePages;

public partial class LicensePage : UserControl
{
    public LicensePage()
    {
        InitializeComponent();
    }

    private void ButtonShowPrivacyLicense_OnClick(object? sender, RoutedEventArgs e)
    {
        var mdReader = new DocumentReaderWindow()
        {
            Source = new Uri("avares://ClassPixelPro/Assets/Documents/Privacy_.md"),
            Title = "隐私政策"
        }.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }
}
