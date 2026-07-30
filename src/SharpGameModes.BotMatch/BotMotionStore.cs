using System.Text.Json;
using SharpGameModes.Contracts;

namespace SharpGameModes.BotMatch;

internal sealed class BotMotionRecording
{
    public int Tickrate { get; set; } = 64;

    public BotReplayTick[] Ticks { get; set; } = [];

    public BotSubtickMove[] Subticks { get; set; } = [];
}

internal static class BotMotionStore
{
    private const long MaximumRecordingBytes = 32L * 1024 * 1024;
    private const int MaximumRecordingTicks = 64 * 60 * 60;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        IncludeFields = true,
        WriteIndented = false,
    };

    public static bool TryResolvePath(
        string directory,
        string? requestedName,
        ulong steamId,
        out string path)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? steamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : requestedName.Trim();
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^5];
        }

        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 96
            || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('_' or '-')))
        {
            path = string.Empty;
            return false;
        }

        path = Path.Combine(directory, $"{name}.json");
        return true;
    }

    public static int Save(
        string path,
        (BotReplayTick[] Ticks, BotSubtickMove[] Subticks) motion,
        int tickrate)
    {
        if (motion.Ticks.Length == 0)
        {
            return -1;
        }

        var recording = new BotMotionRecording
        {
            Tickrate = tickrate,
            Ticks = motion.Ticks,
            Subticks = motion.Subticks,
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(recording, SerializerOptions));
        return recording.Ticks.Length;
    }

    public static BotMotionRecording Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumRecordingBytes)
        {
            return new BotMotionRecording();
        }

        var recording = JsonSerializer.Deserialize<BotMotionRecording>(
            File.ReadAllText(path),
            SerializerOptions) ?? new BotMotionRecording();
        recording.Ticks ??= [];
        recording.Subticks ??= [];
        if (recording.Ticks.Length > MaximumRecordingTicks)
        {
            return new BotMotionRecording();
        }

        return recording;
    }
}
