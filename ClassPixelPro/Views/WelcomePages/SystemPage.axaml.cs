using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Views.WelcomePages;

public partial class SystemPage : UserControl, IWelcomePage
{
    public SystemPage()
    {
        InitializeComponent();
    }

    public WelcomeViewModel ViewModel { get; set; } = null!;
}