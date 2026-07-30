using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpGameModes.BotMatch;

internal sealed class NadeLineupCatalog
{
    private const float CellSize = 200f;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<(int X, int Y), List<NadeLineup>> _cells = [];
    private readonly Dictionary<string, NadeLineup> _byId;

    private NadeLineupCatalog(IReadOnlyList<NadeLineup> lineups)
    {
        Lineups = lineups;
        _byId = new Dictionary<string, NadeLineup>(StringComparer.Ordinal);
        foreach (var lineup in lineups)
        {
            _byId.TryAdd(lineup.Id, lineup);
            var cell = GetCell(lineup.ProjectilePosition.X, lineup.ProjectilePosition.Y);
            if (!_cells.TryGetValue(cell, out var entries))
            {
                entries = [];
                _cells[cell] = entries;
            }

            entries.Add(lineup);
        }
    }

    public IReadOnlyList<NadeLineup> Lineups { get; }

    public NadeLineup? Find(string id)
        => _byId.GetValueOrDefault(id);

    public static NadeLineupCatalog Load(string directory, string mapName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Nade lineup directory does not exist: {directory}");
        }

        var lineups = new List<NadeLineup>();
        var order = 0;
        foreach (var file in Directory
                     .EnumerateFiles(directory, $"{mapName}_*.json")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var entries = JsonSerializer.Deserialize<List<NadeLineup>>(
                File.ReadAllText(file),
                SerializerOptions);
            if (entries is null)
            {
                continue;
            }

            foreach (var lineup in entries)
            {
                lineup.Description ??= string.Empty;
                lineup.GrenadeType = NadeSystemPolicy.NormalizeType(
                    lineup.Description.Contains(
                        "decoy",
                        StringComparison.OrdinalIgnoreCase)
                        ? "decoy"
                        : lineup.GrenadeType);
                lineup.TeamTag = lineup.Description.StartsWith(
                    "CT",
                    StringComparison.OrdinalIgnoreCase)
                    ? "CT"
                    : lineup.Description.StartsWith(
                        "T",
                        StringComparison.OrdinalIgnoreCase)
                        ? "T"
                        : string.Empty;
                lineup.Order = order++;
                Validate(lineup, file);
                if (lineup.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                {
                    lineups.Add(lineup);
                }
            }
        }

        return new NadeLineupCatalog(lineups);
    }

    public IEnumerable<NadeLineup> Query(float x, float y)
    {
        var center = GetCell(x, y);
        for (var cellX = center.X - 1; cellX <= center.X + 1; cellX++)
        {
            for (var cellY = center.Y - 1; cellY <= center.Y + 1; cellY++)
            {
                if (_cells.TryGetValue((cellX, cellY), out var entries))
                {
                    foreach (var lineup in entries)
                    {
                        yield return lineup;
                    }
                }
            }
        }
    }

    private static (int X, int Y) GetCell(float x, float y)
        => ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(y / CellSize));

    private static void Validate(NadeLineup lineup, string file)
    {
        if (string.IsNullOrWhiteSpace(lineup.Id)
            || string.IsNullOrWhiteSpace(lineup.MapName)
            || lineup.GrenadeType is not ("flash" or "smoke" or "he" or "molotov" or "decoy"))
        {
            throw new InvalidDataException(
                $"Invalid nade lineup in {file}: id='{lineup.Id}', map='{lineup.MapName}', type='{lineup.GrenadeType}'.");
        }
    }
}

internal sealed class NadeLineup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = string.Empty;

    [JsonPropertyName("grenadeType")]
    public string GrenadeType { get; set; } = string.Empty;

    [JsonPropertyName("projectilePosition")]
    public NadeVector ProjectilePosition { get; set; } = new();

    [JsonPropertyName("projectileVelocity")]
    public NadeVector ProjectileVelocity { get; set; } = new();

    [JsonPropertyName("landingPosition")]
    public NadeVector LandingPosition { get; set; } = new();

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonIgnore]
    public string TeamTag { get; set; } = string.Empty;

    [JsonIgnore]
    public int Order { get; set; }

    [JsonIgnore]
    public float ZoneRadius => GrenadeType == "smoke" ? 150f : 100f;
}

internal sealed class NadeVector
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }
}
