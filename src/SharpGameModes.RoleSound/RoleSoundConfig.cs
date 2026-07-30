namespace SharpGameModes.RoleSound;

public sealed class RoleSoundConfig
{
    public int ConfigVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string ChatPrefix { get; set; } = "[RoleSound]";
    public string SoundScanRoot { get; set; } = string.Empty;
    public string SoundPathPrefix { get; set; } = "sounds/rolesound";
    public List<string> SoundEventResources { get; set; } = ["soundevents/soundevents_zedcheer.vsndevts"];
    public string SoundEventTemplate { get; set; } = "rolesound.{profile}.{sound_event}";
    public string PlaybackMode { get; set; } = "SoundEvent";
    public string ClientCommandName { get; set; } = "play";
    public List<string> ClientCommandEvents { get; set; } = [];
    public bool StripCompiledExtension { get; set; } = true;
    public bool BlockDefaultRadio { get; set; } = true;
    public bool BlockRadioWhenNoVoice { get; set; }
    public bool ShowRadioText { get; set; } = true;
    public bool ShowCooldownMessage { get; set; } = true;
    public string RadioAudience { get; set; } = "Nearby";
    public string RadioTextAudience { get; set; } = "Nearby";
    public string RadioTextFormat { get; set; } = "{role}: {text}";
    public string CooldownMessageFormat { get; set; } = "{role}: 无线电冷却中，还剩 {seconds} 秒";
    public float NearbyDistance { get; set; } = 900;
    public float Volume { get; set; } = 0.5f;
    public float Pitch { get; set; } = 1;
    public bool EnableReload { get; set; }
    public bool EnableDeath { get; set; } = true;
    public bool EnableRadio { get; set; } = true;
    public bool EnableHurt { get; set; } = true;
    public bool EnableKill { get; set; } = true;
    public bool EnableThrow { get; set; } = true;
    public bool EnableRoundStart { get; set; } = true;
    public bool EnableRoundEnd { get; set; } = true;
    public double RoundStartDelaySeconds { get; set; } = 1;
    public string DefaultVoiceProfile { get; set; } = string.Empty;
    public Dictionary<string, double> CooldownsSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SoundEventNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RadioCommandToKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RadioSlotToKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ModelFolderToVoiceProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ModelPathToVoiceProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> EventFallbackVoiceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> EventKeywords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VoiceProfileConfig> VoiceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        SoundEventResources = NormalizeList(SoundEventResources, normalizePaths: true);
        ClientCommandEvents = NormalizeList(ClientCommandEvents);
        CooldownsSeconds = CooldownsSeconds
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => RoleSoundCatalog.NormalizeKey(pair.Key),
                pair => Math.Max(0, pair.Value),
                StringComparer.OrdinalIgnoreCase);
        SoundEventNames = NormalizeDictionary(SoundEventNames);
        RadioCommandToKey = NormalizeDictionary(RadioCommandToKey);
        RadioSlotToKey = NormalizeDictionary(RadioSlotToKey);
        ModelFolderToVoiceProfile = NormalizeDictionary(ModelFolderToVoiceProfile);
        ModelPathToVoiceProfile = NormalizeDictionary(ModelPathToVoiceProfile, normalizePathKeys: true);
        EventFallbackVoiceProfiles = NormalizeListDictionary(EventFallbackVoiceProfiles);
        EventKeywords = NormalizeListDictionary(EventKeywords);
        VoiceProfiles = (VoiceProfiles ?? new Dictionary<string, VoiceProfileConfig>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => RoleSoundCatalog.NormalizeKey(pair.Key),
                pair => pair.Value ?? new VoiceProfileConfig(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var profile in VoiceProfiles.Values)
        {
            profile.Events = NormalizeListDictionary(profile.Events, normalizePaths: true);
        }

        if (SoundEventResources.Count == 0)
        {
            SoundEventResources.Add("soundevents/soundevents_zedcheer.vsndevts");
        }

        SoundEventTemplate = string.IsNullOrWhiteSpace(SoundEventTemplate)
            ? "rolesound.{profile}.{sound_event}"
            : SoundEventTemplate.Trim();
        PlaybackMode = string.IsNullOrWhiteSpace(PlaybackMode) ? "SoundEvent" : PlaybackMode.Trim();
        ClientCommandName = string.IsNullOrWhiteSpace(ClientCommandName) ? "play" : ClientCommandName.Trim();
        SoundPathPrefix = RoleSoundCatalog.NormalizePath(SoundPathPrefix).Trim('/');
        RoundStartDelaySeconds = Math.Max(0, RoundStartDelaySeconds);
        NearbyDistance = Math.Max(0, NearbyDistance);
        Volume = Math.Clamp(Volume, 0, 1);
        RadioAudience = string.IsNullOrWhiteSpace(RadioAudience) ? "Nearby" : RadioAudience.Trim();
        RadioTextAudience = string.IsNullOrWhiteSpace(RadioTextAudience) ? "Nearby" : RadioTextAudience.Trim();
        RadioTextFormat = string.IsNullOrWhiteSpace(RadioTextFormat) ? "{role}: {text}" : RadioTextFormat;
        CooldownMessageFormat = string.IsNullOrWhiteSpace(CooldownMessageFormat)
            ? "{role}: 无线电冷却中，还剩 {seconds} 秒"
            : CooldownMessageFormat;
    }

    private static Dictionary<string, string> NormalizeDictionary(
        Dictionary<string, string>? source,
        bool normalizePathKeys = false)
        => (source ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => normalizePathKeys
                    ? RoleSoundCatalog.NormalizePath(pair.Key)
                    : RoleSoundCatalog.NormalizeKey(pair.Key),
                pair => RoleSoundCatalog.NormalizeKey(pair.Value),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<string>> NormalizeListDictionary(
        Dictionary<string, List<string>>? source,
        bool normalizePaths = false)
        => (source ?? new Dictionary<string, List<string>>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => RoleSoundCatalog.NormalizeKey(pair.Key),
                pair => NormalizeList(pair.Value, normalizePaths),
                StringComparer.OrdinalIgnoreCase);

    private static List<string> NormalizeList(List<string>? source, bool normalizePaths = false)
        => (source ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => normalizePaths
                ? RoleSoundCatalog.NormalizePath(value)
                : RoleSoundCatalog.NormalizeKey(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed class VoiceProfileConfig
{
    public Dictionary<string, List<string>> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
