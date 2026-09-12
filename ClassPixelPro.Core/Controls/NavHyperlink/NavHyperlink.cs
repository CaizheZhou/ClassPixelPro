using System.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Commands;
using ClassPixelPro.Shared;

namespace ClassPixelPro.Core.Controls.NavHyperlink;

/// <summary>
/// 可调用应用 Uri 导航的 <see cref="HyperlinkButton"/>
/// </summary>
public class NavHyperlink : HyperlinkButton
{
    public static readonly StyledProperty<string> NavTargetProperty = AvaloniaProperty.Register<NavHyperlink, string>(
        nameof(NavTarget));

    public string NavTarget
    {
        get => GetValue(NavTargetProperty);
        set => SetValue(NavTargetProperty, value);
    }

    protected override Type StyleKeyOverride { get; } = typeof(HyperlinkButton);

    /// <inheritdoc />
    public NavHyperlink()
    {
        Click += OnClick;
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(NavTarget,UriKind.Absolute ,out var uri))
        {
            IAppHost.TryGetService<IUriNavigationService>()?.NavigateWrapped(uri);
        }
    }
}