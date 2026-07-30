using System.Text;
using System.Text.Json;

namespace SharpGameModes.BotMatch;

internal sealed class BotIdentityCatalog
{
    internal const ulong SteamId64IndividualBase = 76561197960265728UL;

    private readonly BotIdentityProfile[] _profiles;
    private readonly Dictionary<string, BotIdentityProfile> _profilesByName;

    private BotIdentityCatalog(BotIdentityProfile[] profiles)
    {
        _profiles = profiles;
        _profilesByName = profiles.ToDictionary(
            profile => profile.Name,
            StringComparer.Ordinal);
    }

    internal int Count => _profiles.Length;
    internal int CrosshairCount => _profiles.Count(profile => profile.CrosshairCode.Length > 0);
    internal int FlairCount => _profiles.Count(profile => profile.ScoreboardFlair > 0);
    internal IReadOnlyList<BotIdentityProfile> Profiles => _profiles;

    internal static BotIdentityCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Bot identity catalog root must be an object.");
        }

        var profiles = new List<BotIdentityProfile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Bot identity catalog contains duplicate name '{property.Name}'.");
            }

            profiles.Add(ParseProfile(property));
        }

        if (profiles.Count == 0)
        {
            throw new InvalidDataException("Bot identity catalog is empty.");
        }

        return new BotIdentityCatalog(profiles.ToArray());
    }

    internal bool TryGetByName(string name, out BotIdentityProfile profile)
        => _profilesByName.TryGetValue(name, out profile!);

    internal BotIdentityProfile? ChooseAvailable(
        string proposedName,
        IReadOnlySet<ulong> unavailableSteamIds,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(proposedName);
        ArgumentNullException.ThrowIfNull(unavailableSteamIds);
        ArgumentNullException.ThrowIfNull(random);

        if (TryGetByName(proposedName, out var exact)
            && !unavailableSteamIds.Contains(exact.SteamId64))
        {
            return exact;
        }

        var start = random.Next(_profiles.Length);
        for (var offset = 0; offset < _profiles.Length; offset++)
        {
            var candidate = _profiles[(start + offset) % _profiles.Length];
            if (!unavailableSteamIds.Contains(candidate.SteamId64))
            {
                return candidate;
            }
        }

        return null;
    }

    private static BotIdentityProfile ParseProfile(JsonProperty property)
    {
        if (property.Name.Length == 0
            || Encoding.UTF8.GetByteCount(property.Name) > BotIdentityProfile.MaxNameUtf8Bytes)
        {
            throw new InvalidDataException(
                $"Bot identity name '{property.Name}' exceeds the 31-byte player-name limit.");
        }
        if (property.Value.ValueKind != JsonValueKind.Object
            || !property.Value.TryGetProperty("steamid", out var steamElement)
            || !steamElement.TryGetUInt32(out var accountId))
        {
            throw new InvalidDataException(
                $"Bot identity '{property.Name}' has an invalid Steam account ID.");
        }

        var crosshair = string.Empty;
        if (property.Value.TryGetProperty("crosshair_code", out var crosshairElement)
            && crosshairElement.ValueKind != JsonValueKind.Null)
        {
            crosshair = crosshairElement.GetString() ?? string.Empty;
        }
        if (Encoding.UTF8.GetByteCount(crosshair) > BotIdentityProfile.MaxCrosshairUtf8Bytes
            || (crosshair.Length > 0
                && !crosshair.StartsWith("CSGO-", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Bot identity '{property.Name}' has an invalid crosshair share code.");
        }

        uint scoreboardFlair = 0;
        if (property.Value.TryGetProperty("scoreboard_flair", out var flairElement)
            && !flairElement.TryGetUInt32(out scoreboardFlair))
        {
            throw new InvalidDataException(
                $"Bot identity '{property.Name}' has an invalid scoreboard flair.");
        }
        if (scoreboardFlair > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"Bot identity '{property.Name}' has an out-of-range scoreboard flair.");
        }

        return new BotIdentityProfile(
            property.Name,
            accountId,
            SteamId64IndividualBase + accountId,
            crosshair,
            scoreboardFlair);
    }
}

internal sealed record BotIdentityProfile(
    string Name,
    uint AccountId,
    ulong SteamId64,
    string CrosshairCode,
    uint ScoreboardFlair)
{
    internal const int MaxNameUtf8Bytes = 31;
    internal const int MaxCrosshairUtf8Bytes = 63;
}
