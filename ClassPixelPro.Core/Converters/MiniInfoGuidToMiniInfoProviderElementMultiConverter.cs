using System.Globalization;

using Avalonia.Data.Converters;
using ClassPixelPro.Shared;
using ClassPixelPro.Shared.ComponentModels;
using ClassPixelPro.Shared.Interfaces;

namespace ClassPixelPro.Core.Converters;

public class MiniInfoGuidToMiniInfoProviderElementMultiConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object parameter, CultureInfo culture)
    {
        var guid = (string?)values[0];
        if (guid == null) return null;
        var providers = (ObservableDictionary<string, IMiniInfoProvider>)values[1];
        if (!providers.ContainsKey(guid)) return null;
        return providers[guid].InfoElement;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return Array.Empty<object>();
    }
}