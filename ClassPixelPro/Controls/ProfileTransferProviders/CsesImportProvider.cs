using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Abstractions.Controls.ProfileTransferProviders;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Extensions;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Shared.Models.Profile;
using CsesSharp;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Controls.ProfileTransferProviders;

public partial class CsesImportProvider : GenericImportProviderBase
{
    private IProfileService ProfileService { get; } = IAppHost.GetService<IProfileService>();
    
    public CsesImportProvider() : base()
    {
        ImportFileHeader = "CESE 文件路径";
        FileTypes =
        [
            new FilePickerFileType("CSES 课表文件")
            {
                Patterns = ["*.yml", "*.yaml"]
            }
        ];
    }

    public override async Task<bool> InvokeTransfer()
    {
        try
        {
            var csesProfile = CsesLoader.LoadFromYamlFile(SourceFilePath);
            var templateProfileJson =
                await new StreamReader(AssetLoader.Open(new Uri("avares://ClassPixelPro/Assets/default-subjects.json"))).ReadToEndAsync();
            var templateProfile = JsonSerializer.Deserialize<Profile>(templateProfileJson);
            var profile = csesProfile.ToClassPixelProObject(ImportType == 0 ? ProfileService.Profile : templateProfile);
            if (ImportType == 1)
            {
                var path = System.IO.Path.Combine(Services.ProfileService.ProfilePath, NewProfileName + ".json");
                if (File.Exists(path))
                {
                    throw new InvalidOperationException($"无法导入课表：{path} 已存在。");
                }
                ConfigureFileHelper.SaveConfig(path, profile);
            }

            this.ShowSuccessToast("导入成功。");
            return true;
        }
        catch (Exception exception)
        {
            var logger = IAppHost.GetService<ILogger<CsesImportProvider>>();
            logger.LogError(exception, "导入 CSES 课表失败");
            this.ShowErrorToast($"无法导入 CSES 课表", exception);
            return false;
        }
    }
}