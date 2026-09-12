using System;
using ClassPixelPro.Core.Abstractions.Services;
using ClassPixelPro.Shared;
namespace ClassPixelPro.Converters;

public static class DateTimeToCurrentDateTimeConverter
{
    public static DateTime Convert(DateTime dateTime)
    {
        var now = IAppHost.GetService<IExactTimeService>().GetCurrentLocalDateTime();
        return new DateTime(now.Year, now.Month, now.Day, dateTime.Hour, dateTime.Minute,
            dateTime.Second);
    }
}