using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Views.WelcomePages;

public interface IWelcomePage
{
    WelcomeViewModel ViewModel { get; set; }
}