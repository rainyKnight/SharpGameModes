namespace SharpGameModes.Domain;

public enum TrackedTeam
{
    Terrorist,
    CounterTerrorist,
}

public sealed record TrackedPlayer(
    ulong SteamId,
    string PlayerName,
    TrackedTeam Team,
    bool IsAlive);

public sealed record CompletedPlayerMatchStatistics(
    ulong SteamId,
    string PlayerName,
    int RoundsPlayed,
    int Kills,
    int Assists,
    int Deaths,
    int Damage,
    int Headshots,
    int EntryKills,
    int EntryDeaths,
    int MultiKillRounds,
    int ClutchesWon,
    int KastRounds,
    int SurvivedRounds)
{
    public CompletedMatchStatistics ToRatingStatistics()
        => new(
            RoundsPlayed,
            Kills,
            Assists,
            Deaths,
            Damage,
            KastRounds,
            MultiKillRounds,
            ClutchesWon,
            EntryKills,
            EntryDeaths);
}

public sealed class RatingMatchTracker
{
    private readonly double _tradeWindowSeconds;
    private readonly Dictionary<ulong, RoundPlayerStatistics> _round = new();
    private readonly Dictionary<ulong, MatchPlayerStatistics> _match = new();
    private readonly Dictionary<TrackedTeam, HashSet<ulong>> _alive = new()
    {
        [TrackedTeam.CounterTerrorist] = new(),
        [TrackedTeam.Terrorist] = new(),
    };
    private readonly Dictionary<TrackedTeam, ClutchCandidate> _clutchCandidates = new();
    private readonly List<RecentDeath> _recentDeaths = new();
    private bool _hasEntryDuel;

    public RatingMatchTracker(double tradeWindowSeconds = 5.0)
    {
        if (!double.IsFinite(tradeWindowSeconds) || tradeWindowSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tradeWindowSeconds));
        }

        _tradeWindowSeconds = tradeWindowSeconds;
    }

    public bool IsRoundLive { get; private set; }
    public int MatchPlayerCount => _match.Count;

    public void StartRound(IEnumerable<TrackedPlayer> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        ResetRound();
        IsRoundLive = true;

        foreach (var player in players)
        {
            MarkParticipant(player);
        }
    }

    public void RegisterDamage(TrackedPlayer attacker, TrackedPlayer victim, int damage)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (!IsRoundLive || !AreEnemies(attacker, victim))
        {
            return;
        }

        MarkParticipant(attacker).Damage += Math.Max(0, damage);
    }

    public void RegisterDeath(
        TrackedPlayer victim,
        TrackedPlayer? attacker,
        TrackedPlayer? assister,
        bool headshot,
        double timestamp)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (!IsRoundLive || victim.SteamId == 0)
        {
            return;
        }

        var victimStats = MarkParticipant(victim);
        victimStats.Deaths++;
        victimStats.Team = victim.Team;

        var hasEnemyAttacker = attacker is not null && AreEnemies(attacker, victim);
        if (hasEnemyAttacker)
        {
            var attackerStats = MarkParticipant(attacker!);
            attackerStats.Kills++;
            attackerStats.Team = attacker!.Team;
            if (headshot)
            {
                attackerStats.Headshots++;
            }

            if (!_hasEntryDuel)
            {
                attackerStats.EntryKills++;
                victimStats.EntryDeaths++;
                _hasEntryDuel = true;
            }

            RegisterTrade(attacker, victim, timestamp);
            _recentDeaths.Add(new RecentDeath(
                victim.SteamId,
                attacker.SteamId,
                victim.Team,
                attacker.Team,
                timestamp));
        }

        if (assister is not null
            && assister.SteamId != victim.SteamId
            && (!hasEnemyAttacker || assister.SteamId != attacker!.SteamId)
            && assister.Team != victim.Team)
        {
            var assisterStats = MarkParticipant(assister);
            assisterStats.Assists++;
            assisterStats.Team = assister.Team;
        }

        _alive[victim.Team].Remove(victim.SteamId);
        PruneRecentDeaths(timestamp);
        UpdateClutchCandidates();
    }

    public void EndRound(TrackedTeam? winningTeam, IEnumerable<TrackedPlayer> activePlayers)
    {
        ArgumentNullException.ThrowIfNull(activePlayers);
        if (!IsRoundLive)
        {
            return;
        }

        var activeAtRoundEnd = new HashSet<ulong>();
        foreach (var player in activePlayers)
        {
            if (player.SteamId == 0)
            {
                continue;
            }

            activeAtRoundEnd.Add(player.SteamId);
            MarkParticipant(player);
        }

        if (winningTeam is { } winner
            && _clutchCandidates.TryGetValue(winner, out var clutch)
            && _alive[winner].Contains(clutch.SteamId)
            && _round.TryGetValue(clutch.SteamId, out var clutchStats))
        {
            clutchStats.ClutchesWon++;
        }

        foreach (var roundStats in _round.Values)
        {
            if (!roundStats.Participated)
            {
                continue;
            }

            var survived = roundStats.Deaths == 0 && activeAtRoundEnd.Contains(roundStats.SteamId);
            var kast = roundStats.Kills > 0
                || roundStats.Assists > 0
                || survived
                || roundStats.TradedDeath;
            if (!_match.TryGetValue(roundStats.SteamId, out var matchStats))
            {
                matchStats = new MatchPlayerStatistics(roundStats.SteamId, roundStats.PlayerName);
                _match.Add(roundStats.SteamId, matchStats);
            }

            matchStats.AddRound(roundStats, kast, survived);
        }

        ResetRound();
    }

    public IReadOnlyList<CompletedPlayerMatchStatistics> CompleteMatch()
    {
        var completed = _match.Values
            .Where(player => player.RoundsPlayed > 0)
            .Select(player => player.Snapshot())
            .ToArray();
        DiscardMatch();
        return completed;
    }

    public void ResetRound()
    {
        IsRoundLive = false;
        _hasEntryDuel = false;
        _round.Clear();
        _alive[TrackedTeam.CounterTerrorist].Clear();
        _alive[TrackedTeam.Terrorist].Clear();
        _recentDeaths.Clear();
        _clutchCandidates.Clear();
    }

    public void DiscardMatch()
    {
        ResetRound();
        _match.Clear();
    }

    private RoundPlayerStatistics MarkParticipant(TrackedPlayer player)
    {
        if (!_round.TryGetValue(player.SteamId, out var stats))
        {
            stats = new RoundPlayerStatistics(player.SteamId, player.PlayerName, player.Team);
            _round.Add(player.SteamId, stats);
        }

        stats.PlayerName = player.PlayerName;
        stats.Team = player.Team;
        stats.Participated = true;
        if (player.IsAlive)
        {
            _alive[player.Team].Add(player.SteamId);
        }

        return stats;
    }

    private void RegisterTrade(TrackedPlayer attacker, TrackedPlayer victim, double timestamp)
    {
        foreach (var recentDeath in _recentDeaths)
        {
            var victimWasTraded = recentDeath.KillerSteamId == victim.SteamId
                && recentDeath.VictimTeam == attacker.Team
                && recentDeath.KillerTeam == victim.Team
                && timestamp - recentDeath.Timestamp <= _tradeWindowSeconds;
            if (victimWasTraded && _round.TryGetValue(recentDeath.VictimSteamId, out var tradedStats))
            {
                tradedStats.TradedDeath = true;
            }
        }
    }

    private void PruneRecentDeaths(double timestamp)
        => _recentDeaths.RemoveAll(death => death.Timestamp < timestamp - _tradeWindowSeconds);

    private void UpdateClutchCandidates()
    {
        UpdateClutchCandidate(TrackedTeam.CounterTerrorist, TrackedTeam.Terrorist);
        UpdateClutchCandidate(TrackedTeam.Terrorist, TrackedTeam.CounterTerrorist);
    }

    private void UpdateClutchCandidate(TrackedTeam team, TrackedTeam opponent)
    {
        if (_clutchCandidates.ContainsKey(team)
            || _alive[team].Count != 1
            || _alive[opponent].Count < 2)
        {
            return;
        }

        _clutchCandidates[team] = new ClutchCandidate(_alive[team].First());
    }

    private static bool AreEnemies(TrackedPlayer left, TrackedPlayer right)
        => left.SteamId != 0
            && right.SteamId != 0
            && left.SteamId != right.SteamId
            && left.Team != right.Team;

    private sealed class RoundPlayerStatistics(ulong steamId, string playerName, TrackedTeam team)
    {
        public ulong SteamId { get; } = steamId;
        public string PlayerName { get; set; } = playerName;
        public TrackedTeam Team { get; set; } = team;
        public bool Participated { get; set; }
        public int Kills { get; set; }
        public int Assists { get; set; }
        public int Deaths { get; set; }
        public int Damage { get; set; }
        public int Headshots { get; set; }
        public int EntryKills { get; set; }
        public int EntryDeaths { get; set; }
        public int ClutchesWon { get; set; }
        public bool TradedDeath { get; set; }
    }

    private sealed class MatchPlayerStatistics(ulong steamId, string playerName)
    {
        public ulong SteamId { get; } = steamId;
        public string PlayerName { get; private set; } = playerName;
        public int RoundsPlayed { get; private set; }
        public int Kills { get; private set; }
        public int Assists { get; private set; }
        public int Deaths { get; private set; }
        public int Damage { get; private set; }
        public int Headshots { get; private set; }
        public int EntryKills { get; private set; }
        public int EntryDeaths { get; private set; }
        public int MultiKillRounds { get; private set; }
        public int ClutchesWon { get; private set; }
        public int KastRounds { get; private set; }
        public int SurvivedRounds { get; private set; }

        public void AddRound(RoundPlayerStatistics stats, bool kast, bool survived)
        {
            PlayerName = stats.PlayerName;
            RoundsPlayed++;
            Kills += stats.Kills;
            Assists += stats.Assists;
            Deaths += stats.Deaths;
            Damage += stats.Damage;
            Headshots += stats.Headshots;
            EntryKills += stats.EntryKills;
            EntryDeaths += stats.EntryDeaths;
            MultiKillRounds += stats.Kills >= 2 ? 1 : 0;
            ClutchesWon += stats.ClutchesWon;
            KastRounds += kast ? 1 : 0;
            SurvivedRounds += survived ? 1 : 0;
        }

        public CompletedPlayerMatchStatistics Snapshot()
            => new(
                SteamId,
                PlayerName,
                RoundsPlayed,
                Kills,
                Assists,
                Deaths,
                Damage,
                Headshots,
                EntryKills,
                EntryDeaths,
                MultiKillRounds,
                ClutchesWon,
                KastRounds,
                SurvivedRounds);
    }

    private sealed record RecentDeath(
        ulong VictimSteamId,
        ulong KillerSteamId,
        TrackedTeam VictimTeam,
        TrackedTeam KillerTeam,
        double Timestamp);

    private sealed record ClutchCandidate(ulong SteamId);
}
