using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Models.Management;
using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Controls;

/// <summary>
/// JoinManagementDialog.xaml 的交互逻辑
/// </summary>
public partial class JoinManagementDialog : MyWindow
{
    public JoinManagementViewModel ViewModel { get; } = new();

    public IManagementService ManagementService { get; } = IAppHost.GetService<IManagementService>();

    public JoinManagementDialog()
    {
        InitializeComponent();
    }

    private async void ButtonOk_OnClick(object sender, RoutedEventArgs e)
    {
        await JoinManagement();
    }

    private async Task JoinManagement()
    {
        ViewModel.IsWorking = true;
        try
        {
            // 通过组织 ID 加入：解析短 ID → 长 UUID → 构造配置。
            // 服务器地址固定为官方集控地址，不开放给用户修改。
            const string baseUrl = "https://class.imzcz.cn";
            var orgId = await ManagementService.ResolveOrganizationIdAsync(ViewModel.OrgId, baseUrl);
            var settings = new ManagementSettings
            {
                IsManagementEnabled = true,
                ManagementServerKind = ManagementServerKind.Serverless,
                ManagementServer = "",
                ManagementServerGrpc = "",
                ManifestUrlTemplate = $"{baseUrl}/api/mgmt/{orgId}/serverless/manifest?cuid={{cuid}}&id={{id}}",
                ClassIdentity = ViewModel.ManagementSettings.ClassIdentity ?? ""
            };
            await ManagementService.JoinManagementAsync(settings);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = exception.Message;
            ViewModel.IsErrorMessageOpen = true;
        }
        ViewModel.IsWorking = false;
    }
}
