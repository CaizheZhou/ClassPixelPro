using System;
using System.Linq;
using ClassPixelPro.Services;
using ClassPixelPro.Shared.Helpers;
using ClassPixelPro.Shared.Models.Profile;

namespace ClassPixelPro.Helpers.ProfileTransferHelpers;

public static class ClassPixelProV1ProfileTransferHelper
{
    [Obsolete]
    public static Profile TransferClassPixelProV1ProfileToClassPixelProProfile(string path)
    {
        var config = ConfigureFileHelper.LoadConfigUnWrapped<Profile>(path, false);
        foreach (var tl in config.TimeLayouts)
        {
            foreach (var layoutItem in tl.Value.Layouts.Where(x =>
                         !string.IsNullOrWhiteSpace(x.StartSecond) && !string.IsNullOrWhiteSpace(x.EndSecond)))
            {
                layoutItem.StartTime = DateTime.TryParse(layoutItem.StartSecond, out var r1)
                    ? r1.TimeOfDay
                    : TimeSpan.Zero;
                layoutItem.EndTime = DateTime.TryParse(layoutItem.EndSecond, out var r2)
                    ? r2.TimeOfDay
                    : TimeSpan.Zero;
            }
        }

        return config;
    }
}