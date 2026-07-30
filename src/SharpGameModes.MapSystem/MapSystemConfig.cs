using SharpGameModes.Contracts;

namespace SharpGameModes.MapSystem;

public sealed class MapSystemConfig
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string StatePath { get; init; } = "data/sharp-gamemodes/map-system-state.json";
    public MapVoteConfig Vote { get; init; } = new();
    public MapChangeConfig MapChange { get; init; } = new();
    public RtvConfig Rtv { get; init; } = new();
    public NominationConfig Nomination { get; init; } = new();
    public SourceOfferConfig SourceOffer { get; init; } = new();
    public Dictionary<string, ModeAutoChangeConfig> ModeAutoChangeRules { get; init; }
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["classic"] = new(),
        };

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported map-system schema_version {SchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(StatePath);
        Vote.Validate();
        MapChange.Validate();
        Rtv.Validate();
        Nomination.Validate();
        SourceOffer.Validate();
        foreach (var (rawMode, rule) in ModeAutoChangeRules)
        {
            _ = ModeId.Parse(rawMode);
            rule.Validate(rawMode);
        }
    }

    public ModeAutoChangeConfig? GetAutoChangeRule(ModeId mode)
        => ModeAutoChangeRules.FirstOrDefault(pair => ModeId.Parse(pair.Key) == mode).Value;
}

public sealed class MapVoteConfig
{
    public int DurationSeconds { get; init; } = 25;
    public int MapsInVote { get; init; } = 5;
    public int RememberPlayedMaps { get; init; } = 3;

    public void Validate()
    {
        if (DurationSeconds is < 5 or > 300)
        {
            throw new InvalidDataException("vote.duration_seconds must be between 5 and 300.");
        }

        if (MapsInVote is < 1 or > 9)
        {
            throw new InvalidDataException("vote.maps_in_vote must be between 1 and 9.");
        }

        if (RememberPlayedMaps is < 0 or > 100)
        {
            throw new InvalidDataException("vote.remember_played_maps must be between 0 and 100.");
        }
    }
}

public sealed class MapChangeConfig
{
    public double DelayAfterMatchSeconds { get; init; } = 8;

    public void Validate()
    {
        if (!double.IsFinite(DelayAfterMatchSeconds) || DelayAfterMatchSeconds is < 0 or > 120)
        {
            throw new InvalidDataException("map_change.delay_after_match_seconds must be between 0 and 120.");
        }
    }
}

public sealed class RtvConfig
{
    public bool Enabled { get; init; } = true;
    public int InitialDelaySeconds { get; init; } = 90;
    public double RequiredRatio { get; init; } = 0.6;
    public int CooldownSecondsAfterVote { get; init; }

    public void Validate()
    {
        if (InitialDelaySeconds is < 0 or > 3600)
        {
            throw new InvalidDataException("rtv.initial_delay_seconds must be between 0 and 3600.");
        }

        if (!double.IsFinite(RequiredRatio) || RequiredRatio is < 0.01 or > 1)
        {
            throw new InvalidDataException("rtv.required_ratio must be between 0.01 and 1.");
        }

        if (CooldownSecondsAfterVote is < 0 or > 3600)
        {
            throw new InvalidDataException("rtv.cooldown_seconds_after_vote must be between 0 and 3600.");
        }
    }
}

public sealed class SourceOfferConfig
{
    public bool ShowOnJoin { get; init; }
    public double JoinDelaySeconds { get; init; } = 8;
    public string Url { get; init; } = "https://github.com/rainyKnight/SharpGameModes";
    public string Prefix { get; init; } = "[SharpGameModes]";
    public string Message { get; init; } = "Source code / 源码: {url}";
    public string[] Commands { get; init; } = ["source", "源码"];

    public void Validate()
    {
        if (!double.IsFinite(JoinDelaySeconds) || JoinDelaySeconds is < 0 or > 120)
        {
            throw new InvalidDataException("source_offer.join_delay_seconds must be between 0 and 120.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(Message);
        if (!Message.Contains("{url}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("source_offer.message must contain the {url} placeholder.");
        }

        if (Commands is null
            || Commands.Length == 0
            || Commands.Any(string.IsNullOrWhiteSpace)
            || Commands.Select(NormalizeCommand).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Commands.Length)
        {
            throw new InvalidDataException("source_offer.commands must contain unique, non-empty command names.");
        }
    }

    public bool MatchesCommand(string command)
        => Commands.Any(candidate => NormalizeCommand(candidate).Equals(
            NormalizeCommand(command),
            StringComparison.OrdinalIgnoreCase));

    public string FormatMessage()
    {
        var message = Message.Replace("{url}", Url.Trim(), StringComparison.OrdinalIgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(Prefix)
            ? message
            : $"{Prefix.Trim()} {message}";
    }

    private static string NormalizeCommand(string command)
        => command.Trim().TrimStart('!', '！', '/');
}

public sealed class NominationConfig
{
    public bool Enabled { get; init; } = true;
    public int PageSize { get; init; } = 5;

    public void Validate()
    {
        if (PageSize is < 1 or > 9)
        {
            throw new InvalidDataException("nomination.page_size must be between 1 and 9.");
        }
    }
}

public sealed class ModeAutoChangeConfig
{
    public bool Enabled { get; init; } = true;
    public string AutoChangeMode { get; init; } = "rounds";
    public int VoteStartRound { get; init; } = 8;
    public int ChangeAfterRound { get; init; }
    public double VoteStartMinutes { get; init; }
    public double ChangeAfterMinutes { get; init; }

    public void Validate(string mode)
    {
        if (!AutoChangeMode.Equals("rounds", StringComparison.OrdinalIgnoreCase)
            && !AutoChangeMode.Equals("rounds_sum", StringComparison.OrdinalIgnoreCase)
            && !AutoChangeMode.Equals("timed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Mode '{mode}' has unsupported auto_change_mode '{AutoChangeMode}'.");
        }

        if (VoteStartRound is < 1 or > 1000)
        {
            throw new InvalidDataException($"Mode '{mode}' vote_start_round must be between 1 and 1000.");
        }

        if (ChangeAfterRound is < 0 or > 1000)
        {
            throw new InvalidDataException($"Mode '{mode}' change_after_round must be between 0 and 1000.");
        }

        if (!double.IsFinite(VoteStartMinutes) || VoteStartMinutes is < 0 or > 1440)
        {
            throw new InvalidDataException($"Mode '{mode}' vote_start_minutes must be between 0 and 1440.");
        }

        if (!double.IsFinite(ChangeAfterMinutes) || ChangeAfterMinutes is < 0 or > 1440)
        {
            throw new InvalidDataException($"Mode '{mode}' change_after_minutes must be between 0 and 1440.");
        }

        if (AutoChangeMode.Equals("timed", StringComparison.OrdinalIgnoreCase)
            && (VoteStartMinutes <= 0
                || ChangeAfterMinutes <= VoteStartMinutes))
        {
            throw new InvalidDataException(
                $"Timed mode '{mode}' requires change_after_minutes to be greater than a positive vote_start_minutes.");
        }


        if (AutoChangeMode.Equals("rounds_sum", StringComparison.OrdinalIgnoreCase)
            && ChangeAfterRound > 0
            && ChangeAfterRound < VoteStartRound)
        {
            throw new InvalidDataException(
                $"Round-sum mode '{mode}' requires change_after_round to be at least vote_start_round.");
        }
    }
}
