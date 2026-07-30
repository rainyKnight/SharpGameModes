namespace SharpGameModes.BotMatch;

internal enum DamageRecapStyle
{
    Auto,
    Classic,
    PerfectWorld,
}

internal static partial class RoundDamageRecapPolicy
{
    public static string FormatDifficultyAnnouncement(
        string difficultyTier)
        => BotDifficultyTierPolicy.FormatAnnouncement(difficultyTier);
}

internal sealed record DamageRecapParticipant(
    int Key,
    string Name,
    int Team,
    bool Alive,
    int Health);

internal sealed record DamageRecapEntry(
    int TotalDamage,
    int HitCount,
    int LastKnownHealth)
{
    public static readonly DamageRecapEntry Empty = new(0, 0, 100);
}

internal sealed record DamageRecapLine(
    string EnemyName,
    DamageRecapEntry Dealt,
    DamageRecapEntry Taken,
    int RemainingHealth);

internal sealed class RoundDamageRecapTracker
{
    private readonly Dictionary<int, Dictionary<int, MutableDamageEntry>> _damageByAttacker = [];

    public void ResetRound() => _damageByAttacker.Clear();

    public void RegisterDamage(
        int attackerKey,
        int victimKey,
        int damage,
        int remainingHealth)
    {
        if (attackerKey == victimKey)
        {
            return;
        }

        if (!_damageByAttacker.TryGetValue(attackerKey, out var victims))
        {
            victims = [];
            _damageByAttacker.Add(attackerKey, victims);
        }

        if (!victims.TryGetValue(victimKey, out var entry))
        {
            entry = new MutableDamageEntry();
            victims.Add(victimKey, entry);
        }

        entry.TotalDamage += Math.Max(0, damage);
        entry.HitCount++;
        entry.LastKnownHealth = Math.Max(0, remainingHealth);
    }

    public void RemovePlayer(int key)
    {
        _damageByAttacker.Remove(key);
        foreach (var victims in _damageByAttacker.Values)
        {
            victims.Remove(key);
        }
    }

    public DamageRecapEntry GetDamage(int attackerKey, int victimKey)
    {
        if (_damageByAttacker.TryGetValue(attackerKey, out var victims)
            && victims.TryGetValue(victimKey, out var entry))
        {
            return entry.ToSnapshot();
        }

        return DamageRecapEntry.Empty;
    }

    public IReadOnlyList<DamageRecapLine> BuildLines(
        int recipientKey,
        int recipientTeam,
        IEnumerable<DamageRecapParticipant> participants)
    {
        var enemyTeam = recipientTeam switch
        {
            2 => 3,
            3 => 2,
            _ => 0,
        };
        if (enemyTeam == 0)
        {
            return [];
        }

        return participants
            .Where(player => player.Team == enemyTeam)
            .Select(player =>
            {
                var dealt = GetDamage(recipientKey, player.Key);
                var taken = GetDamage(player.Key, recipientKey);
                var remainingHealth = player.Alive
                    ? Math.Max(0, player.Health)
                    : dealt.HitCount > 0
                        ? dealt.LastKnownHealth
                        : 0;
                return new DamageRecapLine(
                    player.Name,
                    dealt,
                    taken,
                    remainingHealth);
            })
            .OrderByDescending(line => line.Dealt.TotalDamage + line.Taken.TotalDamage)
            .ThenBy(line => line.EnemyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class MutableDamageEntry
    {
        public int TotalDamage { get; set; }
        public int HitCount { get; set; }
        public int LastKnownHealth { get; set; } = 100;

        public DamageRecapEntry ToSnapshot()
            => new(TotalDamage, HitCount, LastKnownHealth);
    }
}

internal static partial class RoundDamageRecapPolicy
{
    internal const string ChatColorGreen = "\u0004";
    internal const string ChatColorLime = "\u0006";
    internal const string ChatColorDefault = "\u0001";

    public static bool TryParseStyle(string value, out DamageRecapStyle style)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                style = DamageRecapStyle.Auto;
                return true;
            case "classic":
                style = DamageRecapStyle.Classic;
                return true;
            case "pw":
                style = DamageRecapStyle.PerfectWorld;
                return true;
            default:
                style = DamageRecapStyle.Auto;
                return false;
        }
    }

    public static string GetStyleName(DamageRecapStyle style)
        => style switch
        {
            DamageRecapStyle.PerfectWorld => "pw",
            DamageRecapStyle.Classic => "classic",
            _ => "auto",
        };

    public static DamageRecapStyle ResolveStyle(
        DamageRecapStyle configured,
        string? steamLanguage,
        bool perfectWorld)
    {
        if (configured != DamageRecapStyle.Auto)
        {
            return configured;
        }

        return perfectWorld
               || steamLanguage is not null
               && (steamLanguage.Equals("schinese", StringComparison.OrdinalIgnoreCase)
                   || steamLanguage.Equals("tchinese", StringComparison.OrdinalIgnoreCase))
            ? DamageRecapStyle.PerfectWorld
            : DamageRecapStyle.Classic;
    }

    public static string FormatLine(DamageRecapLine line, DamageRecapStyle style)
    {
        if (style == DamageRecapStyle.Classic)
        {
            var health = line.RemainingHealth > 0
                ? $"{line.RemainingHealth} HP left"
                : "DEAD";
            var dealtHitLabel = line.Dealt.HitCount <= 1 ? "hit" : "hits";
            var takenHitLabel = line.Taken.HitCount <= 1 ? "hit" : "hits";
            return $" {ChatColorGreen}{line.EnemyName} [{health}] - "
                   + $"Dealt to: [{line.Dealt.TotalDamage} in {line.Dealt.HitCount} {dealtHitLabel}] - "
                   + $"Taken from: [{line.Taken.TotalDamage} in {line.Taken.HitCount} {takenHitLabel}]"
                   + ChatColorDefault;
        }

        return $" {ChatColorDefault}命中{ChatColorGreen}{line.Dealt.HitCount}{ChatColorDefault}次 "
               + $"{ChatColorGreen}{line.Dealt.TotalDamage}{ChatColorDefault}伤害 "
               + $"被击中{ChatColorGreen}{line.Taken.HitCount}{ChatColorDefault}次 "
               + $"{ChatColorGreen}{line.Taken.TotalDamage}{ChatColorDefault}伤害 "
               + $"剩{ChatColorGreen}{Math.Max(0, line.RemainingHealth)}{ChatColorDefault}HP "
               + $"{ChatColorLime}{line.EnemyName}{ChatColorDefault}";
    }
}
