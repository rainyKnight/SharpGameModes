using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace SharpGameModes.BotMatch;

internal sealed class RoundDamageRecapRuntime : IDisposable
{
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly ILogger _logger;
    private readonly string _difficultyTier;
    private readonly RoundDamageRecapTracker _tracker = new();
    private readonly string?[] _steamLanguages = new string?[64];
    private DamageRecapStyle _style;
    private bool _active;
    private bool _announcedDifficultyThisMap;
    private int _lifecycleGeneration;
    private long _hurtEvents;
    private long _recapLines;
    private long _queryErrors;

    public RoundDamageRecapRuntime(
        ISharedSystem shared,
        IClientManager clients,
        ILogger logger,
        string difficultyTier,
        string initialStyle)
    {
        _modSharp = shared.GetModSharp();
        _clients = clients;
        _logger = logger;
        _difficultyTier = difficultyTier;
        _style = RoundDamageRecapPolicy.TryParseStyle(initialStyle, out var parsed)
            ? parsed
            : DamageRecapStyle.Auto;
    }

    public DamageRecapStyle CurrentStyle => _style;

    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _lifecycleGeneration++;
        ResetMap();
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            QueryLanguage(client, 0);
        }

        _logger.LogInformation(
            "Pure ModSharp RoundDamageRecap enabled with style {Style}.",
            RoundDamageRecapPolicy.GetStyleName(_style));
    }

    public void Deactivate()
    {
        if (!_active)
        {
            ClearTransientState();
            return;
        }

        _active = false;
        _lifecycleGeneration++;
        ClearTransientState();
        _logger.LogInformation(
            "Pure ModSharp RoundDamageRecap disabled. Hurt events {HurtEvents}, recap lines {RecapLines}, language query errors {QueryErrors}.",
            Interlocked.Read(ref _hurtEvents),
            Interlocked.Read(ref _recapLines),
            Interlocked.Read(ref _queryErrors));
    }

    public void ResetMap()
    {
        _tracker.ResetRound();
        _announcedDifficultyThisMap = false;
    }

    public void HandleGameEvent(IGameEvent gameEvent)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            switch (gameEvent.Name)
            {
                case "round_start":
                    _tracker.ResetRound();
                    AnnounceDifficultyOncePerMap();
                    break;
                case "player_hurt" when gameEvent is IEventPlayerHurt hurt:
                    HandlePlayerHurt(hurt);
                    break;
                case "round_end":
                    PrintRoundRecaps();
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "RoundDamageRecap event handler failed for {EventName}.",
                gameEvent.Name);
        }
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (_active)
        {
            QueryLanguage(client, 1);
        }
    }

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        _steamLanguages[slot] = null;
        _tracker.RemovePlayer(slot);
    }

    public bool TrySetStyle(string value)
    {
        if (!RoundDamageRecapPolicy.TryParseStyle(value, out var style))
        {
            return false;
        }

        _style = style;
        return true;
    }

    public string DescribeStyle(IGameClient? client = null)
    {
        var configured = RoundDamageRecapPolicy.GetStyleName(_style);
        if (_style != DamageRecapStyle.Auto || client is null)
        {
            return configured;
        }

        var slot = client.Slot.AsPrimitive();
        var language = slot is >= 0 and < 64 ? _steamLanguages[slot] : null;
        var effective = RoundDamageRecapPolicy.ResolveStyle(
            _style,
            language,
            client.PerfectWorld);
        return $"auto, effective = {RoundDamageRecapPolicy.GetStyleName(effective)}, "
               + $"language = {language ?? "unknown"}";
    }

    public string GetStatus()
        => $"RoundDamageRecap active={_active}, style={RoundDamageRecapPolicy.GetStyleName(_style)}, "
           + $"hurt_events={Interlocked.Read(ref _hurtEvents)}, recap_lines={Interlocked.Read(ref _recapLines)}, "
           + $"language_query_errors={Interlocked.Read(ref _queryErrors)}.";

    public void Dispose() => Deactivate();

    private void HandlePlayerHurt(IEventPlayerHurt gameEvent)
    {
        if (!TryGetTrackablePlayer(gameEvent.KillerController, out var attacker)
            || !TryGetTrackablePlayer(gameEvent.VictimController, out var victim)
            || attacker.Key == victim.Key)
        {
            return;
        }

        _tracker.RegisterDamage(
            attacker.Key,
            victim.Key,
            gameEvent.Damage,
            gameEvent.Health);
        Interlocked.Increment(ref _hurtEvents);
    }

    private void PrintRoundRecaps()
    {
        var participants = GetParticipants();
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!TryGetEligibleRecipient(client, out var controller))
            {
                continue;
            }

            var slot = client.Slot.AsPrimitive();
            var language = slot is >= 0 and < 64 ? _steamLanguages[slot] : null;
            var style = RoundDamageRecapPolicy.ResolveStyle(
                _style,
                language,
                client.PerfectWorld);
            foreach (var line in _tracker.BuildLines(slot, (int)controller.Team, participants))
            {
                client.Print(
                    HudPrintChannel.Chat,
                    RoundDamageRecapPolicy.FormatLine(line, style));
                Interlocked.Increment(ref _recapLines);
            }
        }
    }

    private IReadOnlyList<DamageRecapParticipant> GetParticipants()
    {
        var participants = new List<DamageRecapParticipant>();
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!client.IsValid
                || client.IsHltv
                || client.GetPlayerController() is not
                    {
                        IsValidEntity: true,
                        Team: CStrikeTeam.CT or CStrikeTeam.TE,
                    } controller)
            {
                continue;
            }

            var pawn = controller.GetPlayerPawn();
            participants.Add(
                new DamageRecapParticipant(
                    client.Slot.AsPrimitive(),
                    controller.PlayerName,
                    (int)controller.Team,
                    pawn is { IsAlive: true },
                    pawn?.Health ?? 0));
        }

        return participants;
    }

    private bool TryGetTrackablePlayer(
        IPlayerController? controller,
        out (int Key, CStrikeTeam Team) player)
    {
        player = default;
        if (controller is not
            {
                IsValidEntity: true,
                Team: CStrikeTeam.CT or CStrikeTeam.TE,
            })
        {
            return false;
        }

        var key = controller.PlayerSlot.AsPrimitive();
        if (key is < 0 or >= 64
            || _clients.GetGameClient(controller.PlayerSlot) is not
                {
                    IsValid: true,
                    IsHltv: false,
                })
        {
            return false;
        }

        player = (key, controller.Team);
        return true;
    }

    private static bool TryGetEligibleRecipient(
        IGameClient client,
        out IPlayerController controller)
    {
        controller = null!;
        var slot = client.Slot.AsPrimitive();
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(client.IsFakeClient, slot))
        {
            return false;
        }

        if (client.GetPlayerController() is not
            {
                IsValidEntity: true,
                Team: CStrikeTeam.CT or CStrikeTeam.TE,
            } found)
        {
            return false;
        }

        controller = found;
        return true;
    }

    private void QueryLanguage(IGameClient client, double delaySeconds)
    {
        if (!client.IsValid
            || client.IsHltv
            || BotIdentityRegistry.IsBot(
                client.IsFakeClient,
                client.Slot.AsPrimitive()))
        {
            return;
        }

        var generation = _lifecycleGeneration;
        _modSharp.PushTimer(
            () =>
            {
                if (!_active
                    || generation != _lifecycleGeneration
                    || !client.IsValid
                    || !client.IsInGame)
                {
                    return;
                }

                _clients.QueryConVar(client, "cl_language", OnLanguageQueryResult);
            },
            delaySeconds,
            GameTimerFlags.StopOnMapEnd);
    }

    private void OnLanguageQueryResult(
        IGameClient client,
        QueryConVarValueStatus status,
        string name,
        string value)
    {
        if (!_active || status != QueryConVarValueStatus.ValueIntact)
        {
            if (_active)
            {
                Interlocked.Increment(ref _queryErrors);
            }

            return;
        }

        var slot = client.Slot.AsPrimitive();
        if (slot is >= 0 and < 64)
        {
            _steamLanguages[slot] = value;
        }
    }

    private void AnnounceDifficultyOncePerMap()
    {
        if (_announcedDifficultyThisMap)
        {
            return;
        }

        var recipients = _clients.GetGameClients(inGame: true)
            .Where(client => TryGetEligibleRecipient(client, out _))
            .ToArray();
        if (recipients.Length == 0)
        {
            return;
        }

        var message =
            $" {RoundDamageRecapPolicy.ChatColorGreen}{BuildDifficultyMessage()}"
            + RoundDamageRecapPolicy.ChatColorDefault;
        foreach (var client in recipients)
        {
            client.Print(HudPrintChannel.Chat, message);
        }

        _announcedDifficultyThisMap = true;
    }

    private string BuildDifficultyMessage()
        => RoundDamageRecapPolicy.FormatDifficultyAnnouncement(
            _difficultyTier);

    private void ClearTransientState()
    {
        _tracker.ResetRound();
        Array.Clear(_steamLanguages);
        _announcedDifficultyThisMap = false;
    }
}
