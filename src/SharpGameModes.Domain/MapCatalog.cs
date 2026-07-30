using System.Text.Json;
using SharpGameModes.Contracts;

namespace SharpGameModes.Domain;

public sealed class MapPoolDocument
{
    public string Mode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public AutoTeamRuleOverrides? AutoTeam { get; init; }
    public Dictionary<string, MapDefinition> Maps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MapDefinition
{
    public bool Enabled { get; init; } = true;
    public string DisplayName { get; init; } = string.Empty;
    public bool Workshop { get; init; }
    public string WorkshopId { get; init; } = string.Empty;
    public int MinPlayers { get; init; }
    public int MaxPlayers { get; init; } = 64;
    public int Weight { get; init; } = 1;
    public AutoTeamRuleOverrides? AutoTeam { get; init; }
}

public sealed record MapPoolEntry(
    string EntryId,
    ModeId Mode,
    string ModeDisplayName,
    string MapName,
    string DisplayName,
    bool Enabled,
    bool Workshop,
    ulong? WorkshopId,
    int MinPlayers,
    int MaxPlayers,
    int Weight,
    AutoTeamRuleOverrides? AutoTeam = null)
{
    public MapSelection ToSelection()
        => new(EntryId, Mode, MapName, DisplayName, Workshop, WorkshopId, AutoTeam);
}

public sealed class MapCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<MapPoolEntry> _entries;

    private MapCatalog(IReadOnlyList<MapPoolEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<MapPoolEntry> Entries => _entries;

    public static MapCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = JsonSerializer.Deserialize<MapPoolDocument>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException($"Map pool '{path}' is empty.");
        return FromDocument(document, path);
    }

    public static MapCatalog FromDocument(MapPoolDocument document, string source = "memory")
    {
        ArgumentNullException.ThrowIfNull(document);
        var mode = ModeId.Parse(document.Mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.DisplayName);
        AutoTeamRuleResolver.Validate(document.AutoTeam, $"Mode '{mode}'");

        if (document.Maps.Count == 0)
        {
            throw new InvalidDataException($"Map pool '{source}' has no maps.");
        }

        var entries = new List<MapPoolEntry>(document.Maps.Count);
        foreach (var (rawMapName, definition) in document.Maps.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mapName = rawMapName.Trim();
            if (mapName.Length == 0)
            {
                throw new InvalidDataException($"Map pool '{source}' contains an empty map key.");
            }

            if (definition.MinPlayers < 0 || definition.MaxPlayers is < 1 or > 64 || definition.MinPlayers > definition.MaxPlayers)
            {
                throw new InvalidDataException($"Map '{mapName}' has invalid player limits.");
            }

            ulong? workshopId = null;
            if (definition.Workshop && (!ulong.TryParse(definition.WorkshopId, out var parsedId) || parsedId == 0))
            {
                throw new InvalidDataException($"Workshop map '{mapName}' has an invalid workshop_id.");
            }

            if (definition.Workshop)
            {
                workshopId = ulong.Parse(definition.WorkshopId);
            }

            var autoTeam = AutoTeamRuleResolver.Merge(document.AutoTeam, definition.AutoTeam);
            entries.Add(new MapPoolEntry(
                $"{mode.Value}:{mapName.ToLowerInvariant()}",
                mode,
                document.DisplayName.Trim(),
                mapName,
                string.IsNullOrWhiteSpace(definition.DisplayName) ? mapName : definition.DisplayName.Trim(),
                definition.Enabled,
                definition.Workshop,
                workshopId,
                definition.MinPlayers,
                definition.MaxPlayers,
                definition.Weight,
                autoTeam));
        }

        return new MapCatalog(entries);
    }

    public static MapCatalog Combine(IEnumerable<MapCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var entries = catalogs.SelectMany(catalog => catalog.Entries).ToArray();
        var duplicate = entries
            .GroupBy(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate map entry id '{duplicate.Key}'.");
        }

        return new MapCatalog(entries);
    }

    public MapPoolEntry? ResolveEntryId(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return _entries.FirstOrDefault(entry => entry.EntryId.Equals(entryId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public MapPoolEntry? ResolvePhysicalMap(string mapName, ModeId mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        return _entries.FirstOrDefault(entry => entry.Mode == mode
            && entry.MapName.Equals(mapName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public MapPoolEntry? ResolvePhysicalMap(string mapName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        return _entries.FirstOrDefault(entry => entry.MapName.Equals(mapName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<MapPoolEntry> GetEligibleCandidates(int humanPlayerCount)
    {
        if (humanPlayerCount is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(humanPlayerCount));
        }

        return _entries
            .Where(entry => entry.Enabled
                && entry.Weight > 0
                && humanPlayerCount >= entry.MinPlayers
                && humanPlayerCount <= entry.MaxPlayers)
            .ToArray();
    }
}
