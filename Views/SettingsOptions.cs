namespace IslandApp.Views;

/// <summary>One choice in a segmented picker: what it says, and the setting value it stands for.</summary>
public sealed record Option(string Label, object Value);

/// <summary>Choices for Island Settings' pickers. Values must match the AppSettings property types exactly (double vs int).</summary>
public static class SettingsOptions
{
    public static Option[] Accents { get; } =
    [
        new("Red orange", "#F42902"), new("Orange", "#FF9F0A"), new("Yellow", "#FFD60A"), new("Green", "#30D158"),
        new("Cyan", "#64D2FF"), new("Blue", "#0A84FF"), new("Purple", "#BF5AF2"), new("Pink", "#FF375F"),
    ];

    public static Option[] Sizes { get; } = [new("Small", 0.9), new("Normal", 1.0), new("Large", 1.15)];

    public static Option[] Glows { get; } = [new("Off", 0.0), new("Soft", 0.25), new("Normal", 0.45), new("Strong", 0.7)];

    public static Option[] Speeds { get; } = [new("Calm", 1.35), new("Normal", 1.0), new("Snappy", 0.7)];

    public static Option[] TopOffsets { get; } = [new("0", 0.0), new("8", 8.0), new("16", 16.0), new("32 px", 32.0)];

    public static Option[] CollapseDelays { get; } =
        [new("1 s", 1.0), new("1.5 s", 1.5), new("3 s", 3.0), new("5 s", 5.0), new("Never", 0.0)];

    public static Option[] VolumeDurations { get; } = [new("1 s", 1.0), new("1.8 s", 1.8), new("3 s", 3.0), new("5 s", 5.0)];

    public static Option[] ChargingDurations { get; } = [new("1.5 s", 1.5), new("2.5 s", 2.5), new("4 s", 4.0)];

    public static Option[] NotificationDurations { get; } = [new("3 s", 3.0), new("5 s", 5.0), new("8 s", 8.0), new("12 s", 12.0)];

    public static Option[] NotificationSources { get; } = [new("Telegram & Windows", false), new("All apps", true)];

    public static Option[] BatteryLevels { get; } = [new("10%", 10), new("20%", 20), new("30%", 30)];

    public static Option[] ClaudeSteps { get; } = [new("5%", 5), new("10%", 10), new("25%", 25), new("Never", 0)];
}
