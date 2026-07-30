using System.Text.Json;
using System.Text.Json.Serialization;
using SharpGameModes.Contracts;

namespace SharpGameModes.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<PlayerModelSide>))]
public enum PlayerModelSide
{
    All,
    T,
    CT,
}

public static class PlayerModelModePolicy
{
    public static bool CanApplyPlayerModel(ModeId? mode, PlayerModelSide side)
        => mode != ModeId.Zombie || side == PlayerModelSide.CT;
}

public sealed class PlayerModelDefinition
{
    [JsonIgnore]
    public string Index { get; internal set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];

    [JsonPropertyName("permissionsOr")]
    public string[] PermissionsOr { get; init; } = [];

    [JsonPropertyName("side")]
    public PlayerModelSide Side { get; init; } = PlayerModelSide.All;

    [JsonPropertyName("disableleg")]
    public bool DisableLeg { get; init; }

    [JsonPropertyName("hideinmenu")]
    public bool HideInMenu { get; init; }

    [JsonPropertyName("fixedmeshgroups")]
    public Dictionary<int, int> FixedMeshGroups { get; init; } = [];

    [JsonPropertyName("meshgroups")]
    public Dictionary<string, JsonElement> MeshGroups { get; init; } = [];

    [JsonPropertyName("fixedskin")]
    public int FixedSkin { get; init; } = -1;

    [JsonPropertyName("skins")]
    public Dictionary<string, int> Skins { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Index : Name;

    public bool Supports(PlayerModelSide side)
        => Side == PlayerModelSide.All || Side == side;
}

public sealed class PlayerModelInspectionConfig
{
    public bool Enable { get; init; } = true;
    public string Mode { get; init; } = "rotation";
}

public sealed class PlayerModelCatalogConfig
{
    public bool Enabled { get; init; } = true;
    public string Prefix { get; init; } = "[PlayerModels]";

    [JsonPropertyName("Models")]
    public Dictionary<string, PlayerModelDefinition> Models { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("ModelChangeCooldownSecond")]
    public float ModelChangeCooldownSecond { get; init; }

    [JsonPropertyName("Inspection")]
    public PlayerModelInspectionConfig Inspection { get; init; } = new();

    [JsonPropertyName("BasicPermission")]
    public string BasicPermission { get; init; } = string.Empty;

    [JsonPropertyName("DisableDefaultModelLeg")]
    public bool DisableDefaultModelLeg { get; init; }

    [JsonPropertyName("DisableInstantChange")]
    public bool DisableInstantChange { get; init; }

    [JsonPropertyName("DisablePrecache")]
    public bool DisablePrecache { get; init; }

    [JsonPropertyName("DisableRandomModel")]
    public bool DisableRandomModel { get; init; }

    [JsonPropertyName("DisableAutoCheck")]
    public bool DisableAutoCheck { get; init; }

    [JsonPropertyName("DisablePlayerSelection")]
    public bool DisablePlayerSelection { get; init; }

    public void Validate()
    {
        if (ModelChangeCooldownSecond is < 0 or > 3600)
        {
            throw new InvalidDataException("ModelChangeCooldownSecond must be between 0 and 3600.");
        }

        if (Models.Count == 0)
        {
            throw new InvalidDataException("At least one player model must be configured.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, model) in Models)
        {
            if (string.IsNullOrWhiteSpace(index) || string.IsNullOrWhiteSpace(model.Path))
            {
                throw new InvalidDataException("Player model indexes and paths cannot be blank.");
            }

            model.Index = index;
            if (!names.Add(model.DisplayName))
            {
                throw new InvalidDataException($"Player model name '{model.DisplayName}' is duplicated.");
            }

            if (model.FixedSkin < -1)
            {
                throw new InvalidDataException($"Player model '{index}' has an invalid fixed skin.");
            }

            foreach (var (group, value) in model.FixedMeshGroups)
            {
                if (group is < 0 or > 63 || value is not (0 or 1))
                {
                    throw new InvalidDataException($"Player model '{index}' has an invalid fixed mesh group.");
                }
            }
        }
    }
}

public sealed class PlayerModelDefaultRule
{
    [JsonPropertyName("index")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public string[] Index { get; init; } = [];

    [JsonPropertyName("force")]
    public bool Force { get; init; }
}

public sealed class PlayerModelDefaultRuleSet
{
    [JsonPropertyName("all")]
    public Dictionary<string, PlayerModelDefaultRule> All { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("t")]
    public Dictionary<string, PlayerModelDefaultRule> T { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("ct")]
    public Dictionary<string, PlayerModelDefaultRule> CT { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlayerModelDefaultsConfig
{
    [JsonPropertyName("DefaultModels")]
    public PlayerModelDefaultRuleSet DefaultModels { get; init; } = new();
}

public sealed class StringOrArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString() ?? string.Empty];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a string or an array of strings.");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Default model indexes must be strings.");
            }

            values.Add(reader.GetString() ?? string.Empty);
        }

        return [.. values];
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}

public static class PlayerModelMeshGroups
{
    public static ulong CalculateMask(IEnumerable<int> enabled, IReadOnlyDictionary<int, int> fixedGroups)
    {
        ulong mask = 0;
        foreach (var group in enabled.Distinct())
        {
            if (group is < 0 or > 63)
            {
                throw new ArgumentOutOfRangeException(nameof(enabled), group, "Mesh group must be between 0 and 63.");
            }

            mask |= 1UL << group;
        }

        foreach (var (group, state) in fixedGroups)
        {
            if (group is < 0 or > 63 || state is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(fixedGroups), "Fixed mesh groups are invalid.");
            }

            if (state == 0)
            {
                mask &= ~(1UL << group);
            }
            else
            {
                mask |= 1UL << group;
            }
        }

        return mask;
    }

    public static int[] EnabledGroups(ulong mask)
        => Enumerable.Range(0, 64).Where(group => (mask & (1UL << group)) != 0).ToArray();
}
