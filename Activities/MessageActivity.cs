using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>An incoming notification (Telegram or Windows), shown briefly.</summary>
public sealed partial class MessageActivity(Action open) : IslandActivity
{
    private static readonly Color TelegramBlue = Color.FromRgb(0x2A, 0xAB, 0xEE);
    private static readonly Color WindowsBlue = Color.FromRgb(0x00, 0x78, 0xD4);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlowColor))]
    private bool _isTelegram;

    /// <summary>Sender app's logo for non-Telegram notifications; null shows a bell glyph.</summary>
    [ObservableProperty]
    private ImageSource? _appLogo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactSize), nameof(CurrentSize))]
    private string _sender = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRightToLeft), nameof(CompactSize), nameof(CurrentSize))]
    private string _text = string.Empty;

    /// <summary>True when the first letter is Hebrew/Arabic-script (Persian), so the text lays out right-to-left.</summary>
    public bool IsRightToLeft => Text.FirstOrDefault(char.IsLetter) is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ';

    public override ActivityPriority Priority => ActivityPriority.High;

    public override Color? GlowColor => IsTelegram ? TelegramBlue : WindowsBlue;

    /// <summary>Fits the message: short ones stay narrow, long ones stop at <see cref="MaxWidth"/> and the text ends in "…".</summary>
    public override Size CompactSize
    {
        get
        {
            var sender = Math.Min(SenderMaxWidth, Measure(Sender, FontWeights.SemiBold));
            var text = Text.Length > 0 ? SenderGap + Measure(Text, FontWeights.Normal) : 0;
            return new(Math.Clamp(Math.Ceiling(Chrome + sender + text), MinWidth, MaxWidth), 40);
        }
    }

    // Must match the MessageActivity template: margins 10 + 18, icon 22 + 10 gap, sender MaxWidth 130 + 12 gap. +2 for rounding.
    private const double Chrome = 10 + 22 + 10 + 18 + 2;
    private const double SenderMaxWidth = 130;
    private const double SenderGap = 12;
    private const double MinWidth = 180;
    private const double MaxWidth = 420;
    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(Regular.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static double Measure(string text, FontWeight weight) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            weight == FontWeights.SemiBold ? SemiBold : Regular, 13, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

    [RelayCommand]
    private void Open() => open();
}
