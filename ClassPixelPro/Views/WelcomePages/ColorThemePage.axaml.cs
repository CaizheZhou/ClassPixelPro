using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Views.WelcomePages;

public partial class ColorThemePage : UserControl, IWelcomePage
{
    public ColorThemePage()
    {
        InitializeComponent();
    }

    public WelcomeViewModel ViewModel { get; set; } = null!;
}