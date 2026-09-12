using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Abstractions.Services.Metadata;
using ClassPixelPro.Core.Commands;
using ClassPixelPro.Core.Enums.Metadata.Announcement;
using ClassPixelPro.Core.Models.Metadata.Announcement;
using ClassPixelPro.Shared;
using FluentAvalonia.UI.Controls;

namespace ClassPixelPro.Core.Controls;

/// <summary>
/// AnnouncementControl.xaml 的交互逻辑
/// </summary>
public partial class AnnouncementControl : UserControl
{
    public static readonly StyledProperty<Announcement> AnnouncementProperty = AvaloniaProperty.Register<AnnouncementControl, Announcement>(
        nameof(Announcement));

    public AnnouncementControl()
    {
        InitializeComponent();
    }

    public Announcement Announcement
    {
        get => GetValue(AnnouncementProperty);
        set => SetValue(AnnouncementProperty, value);
    }

    public static FuncValueConverter<Severity, InfoBarSeverity> AnnouncementSeverityToInfoBarSeverityConverter =
        new (x => x switch
        {
            Severity.Announcement => InfoBarSeverity.Informational,
            Severity.Info => InfoBarSeverity.Informational,
            Severity.Important => InfoBarSeverity.Warning,
            Severity.Warning => InfoBarSeverity.Warning,
            Severity.Critical => InfoBarSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(x), x, null)
        });

    private void ButtonRead_OnClick(InfoBar infoBar, EventArgs args)
    {
        var service = IAppHost.GetService<IAnnouncementService>();
        var readStateStorage = Announcement.ReadStateStorageScope switch
        {
            ReadStateStorageScope.Local => service.ReadAnnouncementsLocal,
            ReadStateStorageScope.Machine => service.ReadAnnouncementsMachine,
            _ => throw new ArgumentOutOfRangeException()
        };
        readStateStorage.Add(Announcement.Guid);
        
    }

    private void ButtonDetails_OnClick(object? sender, RoutedEventArgs e)
    {
        // 防护：无详情或详情链接为空时忽略点击，避免空 URI 崩溃
        if (Announcement is not { HasDetails: true })
            return;
        if (string.IsNullOrWhiteSpace(Announcement.DetailsUri))
        {
            IAppHost.GetService<IUriNavigationService>().NavigateWrapped(new Uri("https://class.imzcz.cn/#/announcements"));
            return;
        }
        if (!Uri.TryCreate(Announcement.DetailsUri, UriKind.Absolute, out var uri))
            return;
        UriNavigationCommands.UriNavigationCommand.Execute(uri);
    }
}
