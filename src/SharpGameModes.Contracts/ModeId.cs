namespace SharpGameModes.Contracts;

public readonly record struct ModeId
{
    public static readonly ModeId Classic = new("classic");
    public static readonly ModeId TeamDeathmatch = new("tdm");
    public static readonly ModeId Zombie = new("zombie");
    public static readonly ModeId BotMatch = new("botmatch");

    public ModeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public static bool TryParse(string? value, out ModeId mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "classic":
            case "default":
            case "competitive":
                mode = Classic;
                return true;
            case "tdm":
            case "teamdeathmatch":
            case "deathmatch":
                mode = TeamDeathmatch;
                return true;
            case "zombie":
            case "infection":
                mode = Zombie;
                return true;
            case "botmatch":
            case "bot":
            case "bots":
            case "botclassic":
                mode = BotMatch;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static ModeId Parse(string value)
        => TryParse(value, out var mode)
            ? mode
            : throw new ArgumentException($"Unknown or retired mode '{value}'.", nameof(value));

    public override string ToString() => Value;
}
