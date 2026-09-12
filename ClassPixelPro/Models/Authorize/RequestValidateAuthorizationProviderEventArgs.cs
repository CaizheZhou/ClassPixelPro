using Avalonia.Interactivity;
using ClassPixelPro.Controls;
using ClassPixelPro.Views;

namespace ClassPixelPro.Models.Authorize;

public class RequestValidateAuthorizationProviderEventArgs(object? source) : RoutedEventArgs(AuthorizeProviderPresenter.RequestValidateAuthorizationProvidersEvent, source)
{
    public bool IsError { get; set; } = false;
}