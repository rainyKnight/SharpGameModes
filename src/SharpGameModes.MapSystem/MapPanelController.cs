using System.Net;
using System.Text;
using SharpGameModes.Domain;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.MapSystem;

internal enum MapPanelMode
{
    Vote,
    Nomination,
    Information,
    Notice,
}

internal sealed class MapPanelController
{
    private const int PageSize = 5;
    private static readonly IReadOnlyDictionary<string, int> EmptyVoteCounts
        = new Dictionary<string, int>();
    private readonly Dictionary<int, PanelState> _states = [];
    private readonly Action<IGameClient, MapPoolEntry, MapPanelMode> _onSelection;
    private readonly Func<IReadOnlyDictionary<string, int>> _getVoteCounts;
    private readonly Func<ulong, string?> _getPlayerVote;

    public MapPanelController(
        Action<IGameClient, MapPoolEntry, MapPanelMode> onSelection,
        Func<IReadOnlyDictionary<string, int>> getVoteCounts,
        Func<ulong, string?> getPlayerVote)
    {
        _onSelection = onSelection;
        _getVoteCounts = getVoteCounts;
        _getPlayerVote = getPlayerVote;
    }

    public void OpenMaps(
        IGameClient client,
        string title,
        IReadOnlyList<MapPoolEntry> entries,
        MapPanelMode mode,
        DateTimeOffset? voteEndsAt = null)
    {
        if (entries.Count == 0)
        {
            ShowMessage(client, "没有可显示的地图。");
            return;
        }

        var slot = client.Slot.AsPrimitive();
        var oldIndex = _states.TryGetValue(slot, out var old)
            && old.SteamId == client.SteamId.AsPrimitive()
            && old.Mode == mode
                ? old.Index
                : 0;
        _states[slot] = new PanelState(
            client.SteamId.AsPrimitive(),
            title,
            entries.ToArray(),
            mode,
            Math.Clamp(oldIndex, 0, entries.Count - 1),
            voteEndsAt);

        if (mode is MapPanelMode.Vote or MapPanelMode.Nomination)
        {
            client.Print(HudPrintChannel.Chat, "可在聊天框输入当前页的 1-5 选择地图。");
        }
    }

    public void ShowMessage(IGameClient client, string message, double seconds = 5)
    {
        var slot = client.Slot.AsPrimitive();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, seconds));
        if (_states.TryGetValue(slot, out var state)
            && state.SteamId == client.SteamId.AsPrimitive()
            && state.Mode != MapPanelMode.Notice)
        {
            state.StatusMessage = message;
            state.StatusExpiresAt = expiresAt;
            return;
        }

        _states[slot] = new PanelState(
            client.SteamId.AsPrimitive(),
            "换图",
            [],
            MapPanelMode.Notice,
            0,
            null)
        {
            NoticeMessage = message,
            NoticeExpiresAt = expiresAt,
        };
    }

    public void Close(IGameClient client)
    {
        if (_states.Remove(client.Slot.AsPrimitive()))
        {
            client.PrintCenterHtml(" ", 1);
        }
    }

    public void Forget(IGameClient client)
        => _states.Remove(client.Slot.AsPrimitive());

    public void CloseAll(MapPanelMode? mode = null)
    {
        foreach (var slot in _states
                     .Where(pair => mode is null || pair.Value.Mode == mode)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _states.Remove(slot);
        }
    }

    public bool HandleChatNumber(IGameClient client, int visibleNumber)
    {
        if (!_states.TryGetValue(client.Slot.AsPrimitive(), out var state)
            || state.SteamId != client.SteamId.AsPrimitive()
            || state.Mode is not (MapPanelMode.Vote or MapPanelMode.Nomination))
        {
            return false;
        }

        if (state.SelectionLocked)
        {
            state.StatusMessage = "选择已锁定；如需重选，请输入 revote。";
            state.StatusExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3);
            return true;
        }

        if (!PagedSelection.TryResolveVisibleNumber(
                state.Index,
                state.Entries.Count,
                PageSize,
                visibleNumber,
                out var selectedIndex))
        {
            state.StatusMessage = "本页没有这个选项。";
            state.StatusExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3);
            return true;
        }

        state.Index = selectedIndex;
        _onSelection(client, state.Entries[selectedIndex], state.Mode);
        LockAcceptedVote(state, client.SteamId.AsPrimitive());
        return true;
    }

    public HookReturnValue<EmptyHookReturn> OnPlayerRunCommand(
        IPlayerRunCommandHookParams param,
        HookReturnValue<EmptyHookReturn> result)
    {
        var client = param.Controller.GetGameClient();
        if (client is null
            || !_states.TryGetValue(client.Slot.AsPrimitive(), out var state)
            || state.SteamId != client.SteamId.AsPrimitive())
        {
            return result;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.Mode == MapPanelMode.Notice
            && state.NoticeExpiresAt is { } noticeExpiresAt
            && now >= noticeExpiresAt)
        {
            Close(client);
            return result;
        }

        if (state.StatusExpiresAt is { } statusExpiresAt && now >= statusExpiresAt)
        {
            state.StatusMessage = null;
            state.StatusExpiresAt = null;
        }

        var buttons = param.KeyButtons;
        var pressed = buttons & ~state.LastButtons;
        state.LastButtons = buttons;

        if (state.Entries.Count > 0 && !state.SelectionLocked)
        {
            if ((pressed & UserCommandButtons.Forward) != 0)
            {
                state.Index = (state.Index - 1 + state.Entries.Count) % state.Entries.Count;
            }
            else if ((pressed & UserCommandButtons.Back) != 0)
            {
                state.Index = (state.Index + 1) % state.Entries.Count;
            }
            else if ((pressed & UserCommandButtons.MoveLeft) != 0)
            {
                ChangePage(state, -1);
            }
            else if ((pressed & UserCommandButtons.MoveRight) != 0)
            {
                ChangePage(state, 1);
            }
            else if ((pressed & UserCommandButtons.Use) != 0)
            {
                _onSelection(client, state.Entries[state.Index], state.Mode);
                LockAcceptedVote(state, client.SteamId.AsPrimitive());
            }
        }

        if (_states.TryGetValue(client.Slot.AsPrimitive(), out var current)
            && current.SteamId == client.SteamId.AsPrimitive())
        {
            client.PrintCenterHtml(Render(current, client.SteamId.AsPrimitive()), 1);
        }

        return result;
    }

    private string Render(PanelState state, ulong steamId)
    {
        var html = new StringBuilder("<div>");
        html.Append("<b><font color='#ff6666'>")
            .Append(WebUtility.HtmlEncode(state.Title))
            .Append("</font></b>");

        if (state.Mode == MapPanelMode.Vote && state.VoteEndsAt is { } voteEndsAt)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling((voteEndsAt - DateTimeOffset.UtcNow).TotalSeconds));
            html.Append(" <font color='#ffd966'>(").Append(seconds).Append("s)</font>");
        }
        else if (state.Entries.Count > 0)
        {
            html.Append(" <font color='#ffd966'>(")
                .Append(state.Index / PageSize + 1)
                .Append('/')
                .Append(PageCount(state))
                .Append(")</font>");
        }

        html.Append("<br>");
        if (state.Mode == MapPanelMode.Notice)
        {
            html.Append("<font color='#ffffff'>")
                .Append(WebUtility.HtmlEncode(state.NoticeMessage))
                .Append("</font><br>");
        }
        else
        {
            AppendEntries(html, state, steamId);
        }

        if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            html.Append("<font color='#ff9999'>")
                .Append(WebUtility.HtmlEncode(state.StatusMessage))
                .Append("</font><br>");
        }

        html.Append(state.Mode == MapPanelMode.Notice
            ? string.Empty
            : state.SelectionLocked
                ? "<font color='#ffd966'>选择已锁定</font>"
                : "<font color='#ff9999'>W/S</font> 选择 | <font color='#ff9999'>A/D</font> 翻页 | <font color='#ff9999'>E</font> 确认");
        html.Append("</div>");
        return html.ToString();
    }

    private void AppendEntries(StringBuilder html, PanelState state, ulong steamId)
    {
        var counts = state.Mode == MapPanelMode.Vote
            ? _getVoteCounts()
            : EmptyVoteCounts;
        var playerVote = state.Mode == MapPanelMode.Vote ? _getPlayerVote(steamId) : null;
        var start = PageStart(state);
        var end = Math.Min(start + PageSize, state.Entries.Count);
        for (var index = start; index < end; index++)
        {
            var entry = state.Entries[index];
            var selected = index == state.Index;
            var voted = entry.EntryId.Equals(playerVote, StringComparison.OrdinalIgnoreCase);
            var color = selected ? "#9acd32" : "#ffffff";
            var cursor = selected ? "► " : "　";
            var votes = state.Mode == MapPanelMode.Vote
                ? $" <font color='#66ddff'>[{counts.GetValueOrDefault(entry.EntryId)}]</font>"
                : string.Empty;
            var mark = voted ? " <font color='#ffd966'>✓</font>" : string.Empty;
            html.Append("<font color='")
                .Append(color)
                .Append("'>")
                .Append(cursor)
                .Append(index - start + 1)
                .Append(". ")
                .Append(WebUtility.HtmlEncode(MapEntryDisplay.Format(entry)))
                .Append("</font>")
                .Append(votes)
                .Append(mark)
                .Append("<br>");
        }
    }

    private static void ChangePage(PanelState state, int delta)
    {
        var pageCount = PageCount(state);
        var currentPage = state.Index / PageSize;
        var nextPage = (currentPage + delta % pageCount + pageCount) % pageCount;
        var offset = state.Index % PageSize;
        state.Index = Math.Min((nextPage * PageSize) + offset, state.Entries.Count - 1);
    }

    private void LockAcceptedVote(PanelState state, ulong steamId)
    {
        if (state.Mode != MapPanelMode.Vote || _getPlayerVote(steamId) is not { } entryId)
        {
            return;
        }

        var selectedIndex = Array.FindIndex(
            state.Entries.ToArray(),
            entry => entry.EntryId.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0)
        {
            return;
        }

        state.Index = selectedIndex;
        state.SelectionLocked = true;
        state.StatusMessage = null;
        state.StatusExpiresAt = null;
    }

    private static int PageStart(PanelState state)
        => state.Index / PageSize * PageSize;

    private static int PageCount(PanelState state)
        => Math.Max(1, (state.Entries.Count + PageSize - 1) / PageSize);

    private sealed class PanelState(
        ulong steamId,
        string title,
        IReadOnlyList<MapPoolEntry> entries,
        MapPanelMode mode,
        int index,
        DateTimeOffset? voteEndsAt)
    {
        public ulong SteamId { get; } = steamId;
        public string Title { get; } = title;
        public IReadOnlyList<MapPoolEntry> Entries { get; } = entries;
        public MapPanelMode Mode { get; } = mode;
        public DateTimeOffset? VoteEndsAt { get; } = voteEndsAt;
        public int Index { get; set; } = index;
        public UserCommandButtons LastButtons { get; set; }
        public string? NoticeMessage { get; init; }
        public DateTimeOffset? NoticeExpiresAt { get; init; }
        public string? StatusMessage { get; set; }
        public DateTimeOffset? StatusExpiresAt { get; set; }
        public bool SelectionLocked { get; set; }
    }
}
