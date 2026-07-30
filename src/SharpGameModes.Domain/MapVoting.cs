using System.Globalization;
using System.Text;

namespace SharpGameModes.Domain;

public static class MapEntryDisplay
{
    public static string Format(MapPoolEntry entry)
        => $"{entry.DisplayName} [{entry.ModeDisplayName}]";
}

public static class MapSearch
{
    public static IReadOnlyList<MapPoolEntry> Find(
        IEnumerable<MapPoolEntry> entries,
        string query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        var candidates = entries.ToArray();
        var exact = candidates
            .Where(entry => Fields(entry).Any(field => field == normalizedQuery))
            .ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        return candidates
            .Where(entry => Fields(entry).Any(field => field.Contains(normalizedQuery, StringComparison.Ordinal)))
            .OrderBy(entry => Normalize(MapEntryDisplay.Format(entry)).IndexOf(normalizedQuery, StringComparison.Ordinal))
            .ThenBy(MapEntryDisplay.Format, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsLetterOrDigit(character) || category == UnicodeCategory.OtherLetter)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Fields(MapPoolEntry entry)
    {
        yield return Normalize(entry.EntryId);
        yield return Normalize(entry.MapName);
        yield return Normalize(entry.DisplayName);
        yield return Normalize(MapEntryDisplay.Format(entry));
        yield return Normalize(entry.Mode.Value);
        yield return Normalize(entry.ModeDisplayName);
        yield return Normalize($"{entry.MapName} {entry.Mode.Value}");
        yield return Normalize($"{entry.MapName} {entry.ModeDisplayName}");
        yield return Normalize($"{entry.DisplayName} {entry.Mode.Value}");
        yield return Normalize($"{entry.DisplayName} {entry.ModeDisplayName}");
        if (entry.WorkshopId is { } workshopId)
        {
            yield return workshopId.ToString(CultureInfo.InvariantCulture);
        }
    }
}

public sealed record NominationPage(
    int PageNumber,
    int PageCount,
    IReadOnlyList<MapPoolEntry> Entries)
{
    public static NominationPage Create(
        IReadOnlyList<MapPoolEntry> entries,
        int requestedPageIndex,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var pageCount = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
        var pageIndex = (requestedPageIndex % pageCount + pageCount) % pageCount;
        return new NominationPage(
            pageIndex + 1,
            pageCount,
            entries.Skip(pageIndex * pageSize).Take(pageSize).ToArray());
    }
}

public static class PagedSelection
{
    public static bool TryResolveVisibleNumber(
        int currentIndex,
        int itemCount,
        int pageSize,
        int visibleNumber,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (itemCount < 1 || pageSize < 1 || visibleNumber < 1 || visibleNumber > pageSize)
        {
            return false;
        }

        var normalizedIndex = Math.Clamp(currentIndex, 0, itemCount - 1);
        var pageStart = normalizedIndex / pageSize * pageSize;
        var candidateIndex = pageStart + visibleNumber - 1;
        if (candidateIndex >= itemCount)
        {
            return false;
        }

        selectedIndex = candidateIndex;
        return true;
    }
}

public static class MapCandidateSelector
{
    public static IReadOnlyList<MapPoolEntry> Select(
        IReadOnlyCollection<MapPoolEntry> eligibleEntries,
        string? currentEntryId,
        IReadOnlyCollection<string> recentEntryIds,
        IEnumerable<string> nominatedEntryIds,
        int maximumCandidates,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(eligibleEntries);
        ArgumentNullException.ThrowIfNull(recentEntryIds);
        ArgumentNullException.ThrowIfNull(nominatedEntryIds);
        ArgumentNullException.ThrowIfNull(random);
        if (maximumCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        var all = eligibleEntries
            .Where(entry => !entry.EntryId.Equals(currentEntryId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var byId = all.ToDictionary(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase);
        var selected = new List<MapPoolEntry>(Math.Min(maximumCandidates, all.Count));
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nominatedId in nominatedEntryIds)
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            if (byId.TryGetValue(nominatedId, out var entry) && selectedIds.Add(entry.EntryId))
            {
                selected.Add(entry);
            }
        }

        var recent = recentEntryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = all
            .Where(entry => !recent.Contains(entry.EntryId) && !selectedIds.Contains(entry.EntryId))
            .ToList();
        Fill(preferred, selected, selectedIds, maximumCandidates, random);

        var relaxed = all
            .Where(entry => !selectedIds.Contains(entry.EntryId))
            .ToList();
        Fill(relaxed, selected, selectedIds, maximumCandidates, random);
        return selected;
    }

    private static void Fill(
        List<MapPoolEntry> pool,
        List<MapPoolEntry> selected,
        HashSet<string> selectedIds,
        int maximumCandidates,
        Random random)
    {
        while (pool.Count > 0 && selected.Count < maximumCandidates)
        {
            var choice = TakeWeighted(pool, random);
            pool.Remove(choice);
            if (selectedIds.Add(choice.EntryId))
            {
                selected.Add(choice);
            }
        }
    }

    private static MapPoolEntry TakeWeighted(IReadOnlyList<MapPoolEntry> pool, Random random)
    {
        var totalWeight = pool.Sum(entry => (long)Math.Max(0, entry.Weight));
        if (totalWeight <= 0)
        {
            return pool[random.Next(pool.Count)];
        }

        var roll = random.NextInt64(totalWeight);
        foreach (var entry in pool)
        {
            roll -= Math.Max(0, entry.Weight);
            if (roll < 0)
            {
                return entry;
            }
        }

        return pool[^1];
    }
}

public enum MapVoteCastResult
{
    Accepted,
    InvalidCandidate,
    AlreadyVotedSame,
    MustRevokeFirst,
}

public sealed class MapVoteSession
{
    private readonly Dictionary<ulong, string> _votes = [];
    private readonly Dictionary<string, MapPoolEntry> _candidatesById;

    public MapVoteSession(IReadOnlyList<MapPoolEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("A map vote requires at least one candidate.", nameof(candidates));
        }

        Candidates = candidates.ToArray();
        _candidatesById = Candidates.ToDictionary(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase);
        if (_candidatesById.Count != Candidates.Count)
        {
            throw new ArgumentException("Map vote candidates must have unique entry ids.", nameof(candidates));
        }
    }

    public IReadOnlyList<MapPoolEntry> Candidates { get; }
    public int VoteCount => _votes.Count;

    public MapVoteCastResult Cast(ulong voterId, string entryId)
    {
        if (!_candidatesById.ContainsKey(entryId))
        {
            return MapVoteCastResult.InvalidCandidate;
        }

        if (_votes.TryGetValue(voterId, out var existing))
        {
            return existing.Equals(entryId, StringComparison.OrdinalIgnoreCase)
                ? MapVoteCastResult.AlreadyVotedSame
                : MapVoteCastResult.MustRevokeFirst;
        }

        _votes[voterId] = entryId;
        return MapVoteCastResult.Accepted;
    }

    public bool Revoke(ulong voterId)
        => _votes.Remove(voterId);

    public string? GetVote(ulong voterId)
        => _votes.GetValueOrDefault(voterId);

    public IReadOnlyDictionary<string, int> GetCounts()
        => _votes.Values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public MapPoolEntry SelectWinner(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var counts = GetCounts();
        if (counts.Count == 0)
        {
            return Candidates[random.Next(Candidates.Count)];
        }

        var maximum = counts.Values.Max();
        var tied = counts
            .Where(pair => pair.Value == maximum)
            .Select(pair => _candidatesById[pair.Key])
            .ToArray();
        return tied[random.Next(tied.Length)];
    }
}

public sealed record RtvProgress(
    bool Accepted,
    int CurrentVotes,
    int RequiredVotes,
    bool Passed);

public sealed class RtvTracker
{
    private readonly HashSet<ulong> _voters = [];

    public int Count => _voters.Count;

    public RtvProgress Register(
        ulong voterId,
        IReadOnlyCollection<ulong> eligibleVoterIds,
        double requiredRatio)
    {
        ArgumentNullException.ThrowIfNull(eligibleVoterIds);
        if (!double.IsFinite(requiredRatio) || requiredRatio is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredRatio));
        }

        var eligible = eligibleVoterIds.ToHashSet();
        _voters.RemoveWhere(voter => !eligible.Contains(voter));
        var required = Math.Max(1, (int)Math.Ceiling(eligible.Count * requiredRatio));
        var accepted = eligible.Contains(voterId) && _voters.Add(voterId);
        return new RtvProgress(accepted, _voters.Count, required, _voters.Count >= required);
    }

    public bool Remove(ulong voterId)
        => _voters.Remove(voterId);

    public void Clear()
        => _voters.Clear();
}
