using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SharpGameModes.RoleSound;

public sealed class RoleSoundCatalog
{
    private const string RadioPrefix = "radio.";
    private static readonly Regex RedundantRadioTextRegex = new(
        @"(?i)\b(radio|roger|negative|cheer|compliment|holdpos|hold|followme|follow|thanks|thank)\b|无线电|無線電|明白|拒绝|拒絕|欢呼|歡呼|谢了|謝了|谢谢|謝謝|感谢|感謝|跟着我|跟著我|守住",
        RegexOptions.Compiled);

    private readonly RoleSoundConfig _config;
    private readonly Dictionary<string, VoiceProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public RoleSoundCatalog(RoleSoundConfig config)
    {
        _config = config;
        foreach (var (profileName, configuredProfile) in config.VoiceProfiles)
        {
            var normalizedName = NormalizeKey(profileName);
            var profile = new VoiceProfile(normalizedName);
            foreach (var (eventKey, configuredSounds) in configuredProfile.Events)
            {
                var sounds = configuredSounds
                    .Select(path => NormalizePath(path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new SoundEntry(path, BuildTextFromFileName(path, eventKey)))
                    .ToList();
                if (sounds.Count > 0)
                {
                    profile.Events[NormalizeKey(eventKey)] = sounds;
                }
            }

            _profiles[normalizedName] = profile;
        }
    }

    public int ProfileCount => _profiles.Count;
    public IReadOnlyCollection<string> ProfileNames => _profiles.Keys;

    public string? ResolveProfileName(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var normalizedPath = NormalizePath(modelPath);
        if (_config.ModelPathToVoiceProfile.TryGetValue(normalizedPath, out var exactProfile))
        {
            return exactProfile;
        }

        var folder = ExtractModelFolder(normalizedPath);
        if (folder is not null && _config.ModelFolderToVoiceProfile.TryGetValue(folder, out var folderProfile))
        {
            return folderProfile;
        }

        if (folder is not null && _profiles.ContainsKey(folder))
        {
            return folder;
        }

        return string.IsNullOrWhiteSpace(_config.DefaultVoiceProfile)
            ? null
            : NormalizeKey(_config.DefaultVoiceProfile);
    }

    public bool TrySelect(
        string profileName,
        string eventKey,
        Random random,
        [NotNullWhen(true)] out SelectedSound? selected)
    {
        var normalizedProfile = NormalizeKey(profileName);
        var normalizedEvent = NormalizeKey(eventKey);
        if (TryGetSounds(normalizedProfile, normalizedEvent, out var profile, out var sounds))
        {
            selected = new SelectedSound(profile.Name, normalizedEvent, sounds[random.Next(sounds.Count)]);
            return true;
        }

        foreach (var fallback in GetFallbackProfiles(normalizedEvent, normalizedProfile))
        {
            if (TryGetSounds(fallback, normalizedEvent, out profile, out sounds))
            {
                selected = new SelectedSound(profile.Name, normalizedEvent, sounds[random.Next(sounds.Count)]);
                return true;
            }
        }

        selected = null;
        return false;
    }

    public IReadOnlyCollection<string> GetEvents(string profileName)
        => _profiles.TryGetValue(NormalizeKey(profileName), out var profile)
            ? profile.Events.Keys
            : [];

    public string BuildSoundEventName(SelectedSound selected)
    {
        var path = NormalizePath(selected.Sound.Sound);
        var pathWithoutCompiledExtension = _config.StripCompiledExtension
            ? StripCompiledExtension(path)
            : path;
        var file = Path.GetFileName(pathWithoutCompiledExtension);
        var fileWithoutExtension = StripAnyExtension(file);
        var soundEventKey = _config.SoundEventNames.TryGetValue(selected.EventKey, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped)
                ? mapped
                : selected.EventKey.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase)
                    ? selected.EventKey[RadioPrefix.Length..]
                    : selected.EventKey;

        return _config.SoundEventTemplate
            .Replace("{path}", path, StringComparison.OrdinalIgnoreCase)
            .Replace("{path_no_ext}", pathWithoutCompiledExtension, StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", file, StringComparison.OrdinalIgnoreCase)
            .Replace("{file_no_ext}", fileWithoutExtension, StringComparison.OrdinalIgnoreCase)
            .Replace("{profile}", selected.ProfileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{event}", selected.EventKey, StringComparison.OrdinalIgnoreCase)
            .Replace("{sound_event}", soundEventKey, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractModelFolder(string modelPath)
    {
        var normalized = NormalizePath(modelPath);
        const string marker = "characters/models/";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var afterMarker = normalized[(markerIndex + marker.Length)..];
        var slashIndex = afterMarker.IndexOf('/');
        return slashIndex <= 0 ? null : NormalizeKey(afterMarker[..slashIndex]);
    }

    public static string NormalizePath(string value)
        => value.Trim().Trim('"').Replace('\\', '/');

    public static string NormalizeKey(string value)
        => NormalizePath(value).ToLowerInvariant();

    private bool TryGetSounds(
        string profileName,
        string eventKey,
        out VoiceProfile profile,
        out List<SoundEntry> sounds)
    {
        if (_profiles.TryGetValue(profileName, out profile!)
            && profile.Events.TryGetValue(eventKey, out sounds!)
            && sounds.Count > 0)
        {
            return true;
        }

        profile = null!;
        sounds = null!;
        return false;
    }

    private IEnumerable<string> GetFallbackProfiles(string eventKey, string primaryProfile)
    {
        if (_config.EventFallbackVoiceProfiles.TryGetValue(eventKey, out var exactProfiles))
        {
            foreach (var profile in exactProfiles.Where(
                         profile => !profile.Equals(primaryProfile, StringComparison.OrdinalIgnoreCase)))
            {
                yield return profile;
            }
        }

        if (eventKey.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase)
            && _config.EventFallbackVoiceProfiles.TryGetValue("radio", out var radioProfiles))
        {
            foreach (var profile in radioProfiles.Where(
                         profile => !profile.Equals(primaryProfile, StringComparison.OrdinalIgnoreCase)))
            {
                yield return profile;
            }
        }
    }

    private static string BuildTextFromFileName(string soundPath, string eventKey)
    {
        if (!eventKey.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(StripCompiledExtension(NormalizePath(soundPath)));
        name = StripAnyExtension(name);
        var underscore = name.IndexOf('_');
        if (underscore >= 0 && underscore < name.Length - 1)
        {
            var tail = name[(underscore + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(tail) && !tail.All(char.IsDigit))
            {
                name = tail;
            }
        }

        name = RedundantRadioTextRegex.Replace(name, string.Empty);
        name = Regex.Replace(name, @"[\(\)（）【】\[\]_\-]+", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = name.Trim('.', '。', ',', '，', '!', '！', '?', '？', ':', '：');
        return string.IsNullOrWhiteSpace(name) ? GetRadioDisplayName(eventKey) : name;
    }

    public static string GetRadioDisplayName(string eventKey)
        => NormalizeKey(eventKey) switch
        {
            "radio.roger" => "收到",
            "radio.negative" => "拒绝",
            "radio.cheer" => "好厉害",
            "radio.holdpos" => "守住这里",
            "radio.followme" => "跟着我",
            "radio.thanks" => "谢谢",
            _ => "无线电",
        };

    private static string StripCompiledExtension(string path)
        => path.EndsWith(".vsnd_c", StringComparison.OrdinalIgnoreCase)
            ? path[..^".vsnd_c".Length]
            : path;

    private static string StripAnyExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? fileName : fileName[..^extension.Length];
    }
}

public sealed class VoiceProfile(string name)
{
    public string Name { get; } = name;
    public Dictionary<string, List<SoundEntry>> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record SoundEntry(string Sound, string Text);
public sealed record SelectedSound(string ProfileName, string EventKey, SoundEntry Sound);

public enum VoiceAudience
{
    Self,
    Nearby,
    Everyone,
}

public enum RadioPlaybackResult
{
    NoSound,
    Cooldown,
    Played,
}
