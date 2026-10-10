using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassPixelPro.Core.Abstractions.Services.Management;
using ClassPixelPro.Core.Controls;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Enums;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Shared.Models.Management;
using ClassPixelPro.ViewModels;

namespace ClassPixelPro.Controls;

/// <summary>
/// JoinManagementDialog.xaml 的交互逻辑
/// </summary>
public partial class JoinManagementDialog : MyWindow
{
    public static List<FilePickerFileType> ManagementConfigFileTypes { get; } =
    [
        new FilePickerFileType("配置文件")
        {
            Patterns =["*.json"]
        }
    ];
    
    public JoinManagementViewModel ViewModel { get; } = new();

    public IManagementService ManagementService { get; } = IAppHost.GetService<IManagementService>();

    public JoinManagementDialog()
    {
        InitializeComponent();
    }

    protected override void OnInitialized()
    {
        if (File.Exists(Services.Management.ManagementService.ManagementPresetPath))
        {
            ViewModel.ConfigFilePath = Services.Management.ManagementService.ManagementPresetPath;
            LoadManagementSettings();
        }
        base.OnInitialized();
    }

    private void FileBrowserButton_OnFileSelected(object? sender, EventArgs e)
    {
        LoadManagementSettings();
    }

    // 集控配置允许把枚举写成名称（如 "ManagementServerKind": "Serverless"）。
    // 全局序列化选项刻意不注册 JsonStringEnumConverter——那会把所有配置文件的
    // 枚举落盘格式从数字改成字符串，影响面太大。这里只在导入这一条路径上放宽。
    private static readonly JsonSerializerOptions ImportOptions = BuildImportOptions();

    private static JsonSerializerOptions BuildImportOptions()
    {
        var o = new JsonSerializerOptions(ConfigureFileHelper.SerializerOptions);
        o.Converters.Add(new JsonStringEnumConverter());
        o.AllowTrailingCommas = true;
        o.ReadCommentHandling = JsonCommentHandling.Skip;
        return o;
    }

    private void LoadManagementSettings()
    {
        var path = ViewModel.ConfigFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ViewModel.ErrorMessage = "请选择一个配置文件";
            ViewModel.IsErrorMessageOpen = true;
            return;
        }
        try
        {
            ViewModel.ManagementSettings =
                JsonSerializer.Deserialize<ManagementSettings>(File.ReadAllText(path), ImportOptions)
                ?? new ManagementSettings();
            ViewModel.IsConfigLoaded = true;
        }
        catch (Exception exception)
        {
            // 这里不用 ConfigureFileHelper.LoadConfig：它解析失败时会吞掉异常并返回
            // 一份空配置，界面看着一切正常，用户要到点「连接」才会碰到莫名其妙的报错。
            ViewModel.ErrorMessage = "无法读取配置文件：" + exception.Message;
            ViewModel.IsErrorMessageOpen = true;
        }
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
            ManagementSettings settings;
            if (!string.IsNullOrWhiteSpace(ViewModel.OrgId))
            {
                // 通过组织 ID 加入：解析短 ID → 长 UUID → 构造配置
                var baseUrl = (ViewModel.ServerBaseUrl ?? "").Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    baseUrl = "https://class.imzcz.cn";
                }
                var orgId = await ManagementService.ResolveOrganizationIdAsync(ViewModel.OrgId, baseUrl);
                settings = new ManagementSettings
                {
                    IsManagementEnabled = true,
                    ManagementServerKind = ManagementServerKind.Serverless,
                    ManagementServer = "",
                    ManagementServerGrpc = "",
                    ManifestUrlTemplate = $"{baseUrl}/api/mgmt/{orgId}/serverless/manifest?cuid={{cuid}}&id={{id}}",
                    ClassIdentity = ViewModel.ManagementSettings.ClassIdentity ?? ""
                };
            }
            else
            {
                settings = ViewModel.ManagementSettings;
            }
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