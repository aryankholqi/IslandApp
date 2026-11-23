using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IslandApp.Services;

/// <summary>Claude subscription usage as last reported by Claude Code. Percentages are 0..100.</summary>
public sealed record ClaudeUsage(
    double? WeeklyPercent,
    DateTimeOffset? WeeklyResetsAt,
    double? FiveHourPercent,
    DateTimeOffset? FiveHourResetsAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Bridge to Claude Code's documented status line (https://code.claude.com/docs/en/statusline).
/// Claude Code runs <c>Island.exe --claude-statusline</c> with session JSON on stdin; we keep its
/// <c>rate_limits</c> in %AppData%\Island\claude-usage.json, which the island watches.
/// It only updates while a Claude Code session is running (Pro/Max subscriptions only).
/// </summary>
public static class ClaudeCode
{
    public const string StatusLineArgument = "--claude-statusline";

    private static readonly string IslandFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");

    private static readonly string BackupFile = Path.Combine(IslandFolder, "claude-statusline-backup.json");

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    // Claude Code's settings are hand-edited: keep them indented and don't escape quotes or non-ASCII text.
    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string UsageFile { get; } = Path.Combine(IslandFolder, "claude-usage.json");

    /// <summary>Whether Claude Code's status line currently points at Island. Throws if its settings file is unreadable.</summary>
    public static bool IsConnected => IsOurs(ReadSettings()?["statusLine"]);

    /// <summary>Points Claude Code's status line at this exe, backing up any status line the user already had.</summary>
    public static void Connect()
    {
        // ReadSettings throws on invalid JSON, so a file we can't parse is never overwritten.
        var settings = ReadSettings() ?? [];
        if (settings["statusLine"] is { } existing && !IsOurs(existing))
        {
            Directory.CreateDirectory(IslandFolder);
            File.WriteAllText(BackupFile, existing.ToJsonString());
        }

        // Forward slashes work whether Claude Code runs the command through Git Bash or cmd.
        var exe = Environment.ProcessPath!.Replace('\\', '/');
        settings["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"\"{exe}\" {StatusLineArgument}",
            ["padding"] = 0,
        };
        WriteAtomically(SettingsFile, settings.ToJsonString(Readable));
    }

    /// <summary>Removes Island's status line and restores the user's previous one, if any.</summary>
    public static void Disconnect()
    {
        var settings = ReadSettings();
        if (settings is null || !IsOurs(settings["statusLine"]))
        {
            return;
        }

        if (File.Exists(BackupFile))
        {
            settings["statusLine"] = JsonNode.Parse(File.ReadAllText(BackupFile));
            File.Delete(BackupFile);
        }
        else
        {
            settings.Remove("statusLine");
        }

        WriteAtomically(SettingsFile, settings.ToJsonString(Readable));
        File.Delete(UsageFile); // stale once nothing updates it
    }

    /// <summary>
    /// The status line command itself: saves rate limits from Claude Code's JSON and returns the line to print.
    /// Never throws; Claude Code shows whatever we print.
    /// </summary>
    public static string RunStatusLine(string input)
    {
        try
        {
            var root = JsonNode.Parse(input, documentOptions: LenientJson);

            // rate_limits only appears after the session's first response; keep the last saved values until then.
            if (root?["rate_limits"] is JsonObject limits)
            {
                Directory.CreateDirectory(IslandFolder);
                var saved = new JsonObject
                {
                    ["seven_day"] = limits["seven_day"]?.DeepClone(),
                    ["five_hour"] = limits["five_hour"]?.DeepClone(),
                    ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                WriteAtomically(UsageFile, saved.ToJsonString());
            }

            var parts = new List<string>();
            if (Text(root?["model"]?["display_name"]) is { Length: > 0 } model)
            {
                parts.Add(model);
            }

            if (Number(root?["rate_limits"]?["seven_day"]?["used_percentage"]) is { } week)
            {
                parts.Add($"week {week:0}%");
            }

            if (Number(root?["rate_limits"]?["five_hour"]?["used_percentage"]) is { } fiveHour)
            {
                parts.Add($"5h {fiveHour:0}%");
            }

            return string.Join(" · ", parts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public static ClaudeUsage? ReadUsage()
    {
        try
        {
            if (!File.Exists(UsageFile))
            {
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(UsageFile));
            var week = root?["seven_day"];
            var fiveHour = root?["five_hour"];
            return new ClaudeUsage(
                Number(week?["used_percentage"]),
                Time(week?["resets_at"]),
                Number(fiveHour?["used_percentage"]),
                Time(fiveHour?["resets_at"]),
                Time(root?["updated_at"]) ?? DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Mid-replace or corrupt: the next change event reads it again.
            return null;
        }
    }

    /// <summary>Reads stdin and writes the status line to stdout as UTF-8 (Console's encoding is unreliable in a GUI exe).</summary>
    public static void RunStatusLineOverStdio()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var line = RunStatusLine(reader.ReadToEnd());
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(line));
    }

    private static bool IsOurs(JsonNode? statusLine) =>
        Text(statusLine?["command"])?.Contains(StatusLineArgument, StringComparison.Ordinal) == true;

    private static JsonObject? ReadSettings() =>
        File.Exists(SettingsFile)
            ? JsonNode.Parse(File.ReadAllText(SettingsFile), documentOptions: LenientJson) as JsonObject
                ?? throw new JsonException("Claude Code settings.json is not a JSON object.")
            : null;

    /// <summary>Write to a temp file, then swap it in, so readers never see a half-written file.</summary>
    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        Number(node) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime() : null;
}
