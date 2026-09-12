using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using ClassPixelPro.Core.Abstractions.Controls.ProfileTransferProviders;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Core.Helpers.UI;
using ClassPixelPro.Helpers.ProfileTransferHelpers;
using ClassPixelPro.Services;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassPixelPro.Controls.ProfileTransferProviders;

public class ClassPixelPro1ImportProvider : GenericImportProviderBase
{
    private IProfileService ProfileService { get; } = IAppHost.GetService<IProfileService>();
    
    public ClassPixelPro1ImportProvider() : base()
    {
        ImportFileHeader = "ClassPixelPro 1.x 课表文件路径";
        FileTypes =
        [
            new FilePickerFileType("ClassPixelPro 1.x 课表文件")
            {
                Patterns = ["*.json"]
            }
        ];
        AllowMergeToCurrentProfile = false;
    }
    public override async Task<bool> InvokeTransfer()
    {
        try
        {
            var profile = ClassPixelProV1ProfileTransferHelper.TransferClassPixelProV1ProfileToClassPixelProProfile(SourceFilePath);
            if (ImportType == 1)
            {
                var path = Path.Combine(Services.ProfileService.ProfilePath, NewProfileName + ".json");
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
            logger.LogError(exception, "导入 ClassPixelPro 1.x 课表失败");
            this.ShowErrorToast($"无法导入 ClassPixelPro 1.x 课表", exception);
            return false;
        }
    }
}