using System.Globalization;

namespace IslandApp.Core;

public static class TimeFormat
{
    /// <summary>Formats as m:ss, or h:mm:ss for an hour or more.</summary>
    public static string Short(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }
}
