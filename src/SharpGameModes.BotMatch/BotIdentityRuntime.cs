using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.CStrike;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

[assembly: DisableRuntimeMarshalling]

namespace SharpGameModes.BotMatch;

internal sealed class BotIdentityRuntime : IDisposable, IBotHider
{
    private const string GameDataPath = "sharp-gamemodes.botmatch.games";
    private const int MaxUserInfoBytes = 4096;

    private readonly object _gate = new();
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly ISchemaManager _schema;
    private readonly ILogger _logger;
    private readonly BotMatchConfig _config;
    private readonly BotIdentityCatalog _catalog;
    private readonly Random _random = new();
    private readonly ManagedBotSlot?[] _slots = new ManagedBotSlot?[64];
    private readonly PendingBotSlot?[] _pendingSlots = new PendingBotSlot?[64];
    private readonly long[] _nextUserInfoAttemptAt = new long[64];
    private INetworkingStringTable? _avatarTable;
    private nint _avatarTablePointer;
    private int _pingOffset;
    private int _userInfoChangedVFuncIndex;
    private bool _gameDataRegistered;
    private bool _active;
    private bool _disguiseEnabled = true;
    private bool _userInfoApiDisabled;
    private string _userInfoApiFailure = string.Empty;
    private bool _useBotInfoNames;
    private long _crosshairWrites;
    private long _flairWrites;
    private long _avatarWrites;
    private long _avatarClears;
    private long _avatarErrors;
    private long _engineClientRefreshes;
    private long _userInfoRefreshErrors;
    private long _userInfoRewriteAttempts;
    private long _userInfoRewriteMatches;
    private long _userInfoRewriteWrites;
    private long _userInfoRewriteMisses;
    private long _userInfoRewriteErrors;
    private long _userInfoInvalidSnapshots;
    private long _userInfoNullStructures;
    private long _userInfoNullData;
    private long _userInfoInvalidSizes;
    private long _userInfoInvalidPayloads;
    private long _userInfoTargetMismatches;
    private long _presentationErrors;

    public BotIdentityRuntime(
        ISharedSystem shared,
        IClientManager clients,
        ILogger logger,
        BotMatchConfig config,
        string identityCatalogPath)
    {
        _modSharp = shared.GetModSharp();
        _clients = clients;
        _schema = shared.GetSchemaManager();
        _logger = logger;
        _config = config;
        _useBotInfoNames = config.UseBotInfoNames;
        _catalog = BotIdentityCatalog.Load(identityCatalogPath);
    }

    public bool IsEnabled => _config.HideBotIdentity;
    public bool IsActive => _active;

    public bool NormalizeInactiveBotName(IGameClient client)
    {
        if (!_config.NormalizeInactiveBotNames
            || _active
            || !client.IsValid
            || client.IsHltv
            || !client.IsFakeClient)
        {
            return false;
        }

        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return false;
        }

        lock (_gate)
        {
            if (_active || !client.IsValid || !client.IsFakeClient)
            {
                return false;
            }

            try
            {
                var name = StockBotNamePolicy.ForSlot(slot);
                if (!string.Equals(client.Name, name, StringComparison.Ordinal))
                {
                    client.SetName(name);
                }

                if (client.GetPlayerController() is { IsValidEntity: true } controller
                    && !string.Equals(controller.PlayerName, name, StringComparison.Ordinal))
                {
                    controller.PlayerName = name;
                    controller.NetworkStateChanged("m_iszPlayerName");
                }

                return true;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _presentationErrors);
                _logger.LogDebug(
                    exception,
                    "Failed to normalize inactive Bot name for slot {Slot}.",
                    slot);
                return false;
            }
        }
    }

    public bool Activate()
    {
        if (!IsEnabled)
        {
            return true;
        }

        lock (_gate)
        {
            if (_active)
            {
                return true;
            }

            try
            {
                _disguiseEnabled = true;
                _userInfoApiDisabled = false;
                _userInfoApiFailure = string.Empty;
                _useBotInfoNames = _config.UseBotInfoNames;
                RegisterGameData();
                ResolveOffsets();

                _active = true;
                _logger.LogInformation(
                    "Pure ModSharp BotHider enabled with userinfo Fakeplayer rewriting and {Profiles} profiles ({Crosshairs} crosshairs, {Flairs} scoreboard flairs); engine Bot state, PackEntities and ModSharp core remain unchanged.",
                    _catalog.Count,
                    _catalog.CrosshairCount,
                    _catalog.FlairCount);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to enable pure ModSharp BotHider.");
                return false;
            }
        }
    }

    public void Deactivate()
    {
        if (!IsEnabled)
        {
            BotIdentityRegistry.Clear();
            return;
        }

        lock (_gate)
        {
            if (!_active)
            {
                BotIdentityRegistry.Clear();
                return;
            }

            _active = false;
            ClearAllAvatarOverridesUnsafe();
            RestoreAllUnsafe();
            BotIdentityRegistry.Clear();
        }

        _logger.LogInformation(
            "Pure ModSharp BotHider disabled; restored userinfo Fakeplayer and presentation. Crosshair writes {CrosshairWrites}, flair writes {FlairWrites}, avatar writes {AvatarWrites}, avatar clears {AvatarClears}, userinfo writes {UserInfoWrites}, refreshes {EngineClientRefreshes}, errors {Errors}.",
            Interlocked.Read(ref _crosshairWrites),
            Interlocked.Read(ref _flairWrites),
            Interlocked.Read(ref _avatarWrites),
            Interlocked.Read(ref _avatarClears),
            Interlocked.Read(ref _userInfoRewriteWrites),
            Interlocked.Read(ref _engineClientRefreshes),
            Interlocked.Read(ref _presentationErrors)
                + Interlocked.Read(ref _avatarErrors)
                + Interlocked.Read(ref _userInfoRefreshErrors)
                + Interlocked.Read(ref _userInfoRewriteErrors));
    }

    public unsafe void OnClientConnected(IGameClient client)
    {
        if (!_active
            || !client.IsValid
            || client.IsHltv
            || !client.IsFakeClient)
        {
            return;
        }

        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            if (_slots[slot] is { } existing)
            {
                ReleaseUnsafe(existing, restore: false);
                _slots[slot] = null;
            }

            if (_pendingSlots[slot] is { } oldPending)
            {
                ReleasePendingUnsafe(oldPending);
                _pendingSlots[slot] = null;
            }

            var pointer = client.GetAbsPtr();
            var originalName = client.Name;
            var unavailableSteamIds = CollectUnavailableSteamIdsUnsafe();
            var profile = _catalog.ChooseAvailable(
                originalName,
                unavailableSteamIds,
                _random);
            var syntheticSteamId = profile?.SteamId64
                ?? _config.SyntheticSteamIdBase + (ulong)slot + 1UL;
            var personaName = profile is not null && _useBotInfoNames
                ? profile.Name
                : string.IsNullOrWhiteSpace(originalName)
                    ? profile?.Name ?? PersonaName(slot)
                    : originalName;
            var pending = new PendingBotSlot(
                slot,
                client.UserId.AsPrimitive(),
                client,
                pointer,
                originalName,
                syntheticSteamId,
                StablePing(slot),
                personaName,
                profile);

            _pendingSlots[slot] = pending;
            _nextUserInfoAttemptAt[slot] = 0;
            BotIdentityRegistry.MarkManaged(slot);

            _logger.LogInformation(
                "BotHider prepared slot {Slot} during OnClientConnected as '{Name}' with profile {Profile} and presentation SteamID {SteamId}; engine Bot identity is unchanged.",
                slot,
                personaName,
                profile?.Name ?? "fallback",
                syntheticSteamId);
        }
    }

    public void Reconcile()
    {
        if (!_active)
        {
            return;
        }

        var current = _clients.GetGameClients(inGame: false)
            .Where(client => client.IsValid && !client.IsHltv)
            .ToArray();
        var seen = new bool[64];

        foreach (var client in current)
        {
            var slot = client.Slot.AsPrimitive();
            if (slot is < 0 or >= 64)
            {
                continue;
            }

            seen[slot] = true;
            TryAdoptOrRefresh(client);
        }

        lock (_gate)
        {
            for (var slot = 0; slot < _slots.Length; slot++)
            {
                if (!seen[slot] && _slots[slot] is { } managed)
                {
                    ReleaseUnsafe(managed, restore: false);
                    _slots[slot] = null;
                }

                if (!seen[slot] && _pendingSlots[slot] is { } pending)
                {
                    ReleasePendingUnsafe(pending);
                    _pendingSlots[slot] = null;
                }
            }
        }
    }

    public void TryAdoptOrRefresh(IGameClient client)
    {
        if (!_active || !client.IsValid || client.IsHltv)
        {
            return;
        }

        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            var userId = client.UserId.AsPrimitive();
            if (_slots[slot] is { } existing)
            {
                if (existing.UserId == userId && existing.ClientPointer == client.GetAbsPtr())
                {
                    existing.Client = client;
                    if (client.GetPlayerController() is { IsValidEntity: true } currentController)
                    {
                        existing.Controller = currentController;
                        existing.ControllerPointer = currentController.GetAbsPtr();
                    }

                    RefreshManagedSlotUnsafe(existing);
                    return;
                }

                ReleaseUnsafe(existing, restore: false);
                _slots[slot] = null;
            }

            if (_pendingSlots[slot] is { } pending)
            {
                if (pending.UserId != userId
                    || pending.ClientPointer != client.GetAbsPtr())
                {
                    ReleasePendingUnsafe(pending);
                    _pendingSlots[slot] = null;
                    BotIdentityRegistry.Release(slot);
                    return;
                }

                pending.Client = client;
                if (!client.IsInGame
                    || client.GetPlayerController() is not { IsValidEntity: true } pendingController)
                {
                    return;
                }

                PromotePendingUnsafe(pending, pendingController);
                _pendingSlots[slot] = null;
                return;
            }

            // Existing bots can be adopted without rebuilding because this
            // route changes only the replicated userinfo Fakeplayer field.
            if (client.IsFakeClient
                && client.IsInGame
                && client.GetPlayerController() is { IsValidEntity: true })
            {
                OnClientConnected(client);
            }
        }
    }

    public void OnClientSettingChanged(IGameClient client)
    {
        if (!_active || !_disguiseEnabled || !client.IsValid || client.IsHltv)
        {
            return;
        }

        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        lock (_gate)
        {
            var userId = client.UserId.AsPrimitive();
            if (_slots[slot] is { } managed
                && managed.UserId == userId
                && managed.ClientPointer == client.GetAbsPtr())
            {
                TrySetUserInfoFakePlayerUnsafe(slot, userId, fakePlayer: false);
            }
        }
    }

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
        {
            return;
        }

        lock (_gate)
        {
            if (_slots[slot] is not { } managed)
            {
                if (_pendingSlots[slot] is { } pending)
                {
                    ReleasePendingUnsafe(pending);
                    _pendingSlots[slot] = null;
                }

                BotIdentityRegistry.Release(slot);
                return;
            }

            var sameIncarnation = client.IsValid
                && managed.ClientPointer == client.GetAbsPtr()
                && managed.UserId == client.UserId.AsPrimitive();
            ReleaseUnsafe(
                managed,
                restore: sameIncarnation);
            _slots[slot] = null;
        }
    }

    public void RestoreAllForQuotaRebuild()
    {
        lock (_gate)
        {
            RestoreAllUnsafe();
        }
    }

    public void RunWithEngineBotIdentity(IGameClient client, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public void Dispose()
    {
        Deactivate();
        if (_gameDataRegistered)
        {
            _modSharp.GetGameData().Unregister(GameDataPath);
            _gameDataRegistered = false;
        }
    }

    public string GetStatus()
    {
        lock (_gate)
        {
            var managed = _slots.Count(slot => slot is not null);
            var pending = _pendingSlots.Count(slot => slot is not null);
            var identities = string.Join(
                ", ",
                _slots
                    .Where(slot => slot is not null)
                    .Select(slot =>
                        $"slot={slot!.Slot} sid={slot.SyntheticSteamId} " +
                        $"name='{slot.PersonaName}' ping={slot.Ping} " +
                        $"crosshair='{slot.CrosshairCode}' " +
                        $"flair={slot.ScoreboardFlair} " +
                        $"avatar={slot.AvatarApplied}/{slot.AvatarBytes?.Length ?? 0}B"));
            return
                $"BotHider {(_active ? "enabled" : "disabled")}. " +
                $"Disguise {(_disguiseEnabled ? "on" : "off")}, " +
                $"name source {(_useBotInfoNames ? "bot_info" : "botprofile")}; " +
                $"UserInfoChanged=#{_userInfoChangedVFuncIndex}; " +
                $"engine identity and PackEntities unchanged; userinfo API {(_userInfoApiDisabled ? "disabled" : "probing/available")}" +
                (_userInfoApiDisabled ? $" ({_userInfoApiFailure})" : string.Empty) +
                "; Fakeplayer-only rewrite requested; " +
                $"Profiles {_catalog.Count}, crosshairs {_catalog.CrosshairCount}, " +
                $"flairs {_catalog.FlairCount}; managed {managed}, pending {pending}" +
                (managed > 0 ? $" [{identities}]" : string.Empty) +
                $"; crosshair writes {Interlocked.Read(ref _crosshairWrites)}, " +
                $"flair writes {Interlocked.Read(ref _flairWrites)}, " +
                $"avatar writes {Interlocked.Read(ref _avatarWrites)}, " +
                $"avatar clears {Interlocked.Read(ref _avatarClears)}, " +
                $"refreshes {Interlocked.Read(ref _engineClientRefreshes)}, " +
                $"refresh errors {Interlocked.Read(ref _userInfoRefreshErrors)}, " +
                $"userinfo attempts {Interlocked.Read(ref _userInfoRewriteAttempts)}, " +
                $"matches {Interlocked.Read(ref _userInfoRewriteMatches)}, " +
                $"writes {Interlocked.Read(ref _userInfoRewriteWrites)}, " +
                $"misses {Interlocked.Read(ref _userInfoRewriteMisses)}, " +
                $"invalid snapshots {Interlocked.Read(ref _userInfoInvalidSnapshots)}, " +
                $"null structures {Interlocked.Read(ref _userInfoNullStructures)}, " +
                $"null data {Interlocked.Read(ref _userInfoNullData)}, " +
                $"invalid sizes {Interlocked.Read(ref _userInfoInvalidSizes)}, " +
                $"invalid payloads {Interlocked.Read(ref _userInfoInvalidPayloads)}, " +
                $"target mismatches {Interlocked.Read(ref _userInfoTargetMismatches)}, " +
                $"userinfo errors {Interlocked.Read(ref _userInfoRewriteErrors)}, " +
                $"errors {Interlocked.Read(ref _presentationErrors) + Interlocked.Read(ref _avatarErrors) + Interlocked.Read(ref _userInfoRefreshErrors) + Interlocked.Read(ref _userInfoRewriteErrors)}.";
        }
    }

    public bool IsManagedBot(int slot)
    {
        lock (_gate)
        {
            return _active
                && slot is >= 0 and < 64
                && (_slots[slot] is not null || _pendingSlots[slot] is not null);
        }
    }

    public ulong GetBotSteamId(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.SyntheticSteamId
                    ?? _pendingSlots[slot]?.SyntheticSteamId
                    ?? 0UL
                : 0UL;
        }
    }

    public int[] GetManagedSlots()
    {
        lock (_gate)
        {
            return _slots
                .Select((slot, index) => (slot, index))
                .Where(item =>
                    item.slot is not null || _pendingSlots[item.index] is not null)
                .Select(item => item.index)
                .ToArray();
        }
    }

    public string GetPersonaName(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.PersonaName
                    ?? _pendingSlots[slot]?.PersonaName
                    ?? string.Empty
                : string.Empty;
        }
    }

    public int GetPing(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.Ping
                    ?? _pendingSlots[slot]?.Ping
                    ?? 0
                : 0;
        }
    }

    public string GetCrosshairCode(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.CrosshairCode
                    ?? _pendingSlots[slot]?.Profile?.CrosshairCode
                    ?? string.Empty
                : string.Empty;
        }
    }

    public bool HasBotAvatar(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                && _slots[slot] is
                {
                    AvatarApplied: true,
                } managed
                && managed.AvatarAppliedSteamId == managed.SyntheticSteamId;
        }
    }

    public int GetConfiguredAvatarSize(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.AvatarBytes?.Length ?? 0
                : 0;
        }
    }

    public uint GetScoreboardFlair(int slot)
    {
        lock (_gate)
        {
            return slot is >= 0 and < 64
                ? _slots[slot]?.ScoreboardFlair
                    ?? _pendingSlots[slot]?.Profile?.ScoreboardFlair
                    ?? 0U
                : 0U;
        }
    }

    public (string Name, ulong Address)[] GetSignatures() => [];

    public bool SetBotSteamId(int slot, ulong steamId64)
    {
        if (steamId64 < BotIdentityCatalog.SteamId64IndividualBase
            || steamId64
                > BotIdentityCatalog.SteamId64IndividualBase + uint.MaxValue)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_active
                || slot is < 0 or >= 64
                || _slots[slot] is not { } managed)
            {
                return false;
            }

            var unavailable = CollectUnavailableSteamIdsUnsafe();
            unavailable.Remove(managed.SyntheticSteamId);
            if (unavailable.Contains(steamId64))
            {
                return false;
            }

            ClearAvatarOverrideUnsafe(managed);
            managed.SyntheticSteamId = steamId64;
            ApplyPresentationUnsafe(managed, managed.PersonaName);
            ApplyAvatarOverrideUnsafe(managed, out _);
            if (_disguiseEnabled)
            {
                TrySetUserInfoFakePlayerUnsafe(
                    managed.Slot,
                    managed.UserId,
                    fakePlayer: false);
            }
            return true;
        }
    }

    public bool SetCrosshairCode(int slot, string code)
    {
        if (!BotIdentityPresentationPolicy.TryNormalizeCrosshair(
                code,
                out var crosshair))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_active
                || slot is < 0 or >= 64
                || _slots[slot] is not { } managed)
            {
                return false;
            }

            managed.CrosshairCode = crosshair;
            ApplyProfilePresentationUnsafe(managed);
            return true;
        }
    }

    public bool SetBotAvatar(int slot, string pngPath)
        => TrySetBotAvatar(slot, pngPath, out _);

    public bool TrySetBotAvatar(int slot, string pngPath, out string error)
    {
        error = string.Empty;
        ManagedBotSlot expected;
        lock (_gate)
        {
            if (!_active
                || slot is < 0 or >= 64
                || _slots[slot] is not { } managed)
            {
                error = "slot is not a BotHider-managed bot";
                return false;
            }

            expected = managed;
        }

        if (pngPath == "0")
        {
            lock (_gate)
            {
                if (!_active || !ReferenceEquals(_slots[slot], expected))
                {
                    error = "slot is not a BotHider-managed bot";
                    return false;
                }

                expected.AvatarBytes = null;
                ClearAvatarOverrideUnsafe(expected);
                return true;
            }
        }

        byte[] bytes;
        try
        {
            if (string.IsNullOrWhiteSpace(pngPath))
            {
                error = "avatar path is empty";
                return false;
            }

            var fullPath = Path.GetFullPath(pngPath);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                error = "avatar PNG does not exist";
                return false;
            }
            if (file.Length > BotIdentityPresentationPolicy.MaxAvatarBytes)
            {
                error = "avatar PNG must be 16 KiB or smaller";
                return false;
            }

            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception)
        {
            error = $"failed to read avatar PNG: {exception.Message}";
            return false;
        }

        if (!BotIdentityPresentationPolicy.TryValidateAvatarBytes(bytes, out error))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_active || !ReferenceEquals(_slots[slot], expected))
            {
                error = "slot is not a BotHider-managed bot";
                return false;
            }

            expected.AvatarBytes = bytes;
            return ApplyAvatarOverrideUnsafe(expected, out error);
        }
    }

    public bool SetPersonaName(int slot, string name)
    {
        var normalized = BotIdentityPresentationPolicy.NormalizePersonaName(name);
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_active
                || slot is < 0 or >= 64
                || _slots[slot] is not { } managed)
            {
                return false;
            }

            managed.PersonaName = normalized;
            ApplyPresentationUnsafe(managed, normalized);
            if (_disguiseEnabled)
            {
                TryRefreshClientUserInfoUnsafe(slot);
                TrySetUserInfoFakePlayerUnsafe(
                    managed.Slot,
                    managed.UserId,
                    fakePlayer: false);
            }
            return true;
        }
    }

    public bool SetScoreboardFlair(int slot, uint itemDefIndex)
    {
        if (itemDefIndex > ushort.MaxValue)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_active
                || slot is < 0 or >= 64
                || _slots[slot] is not { } managed)
            {
                return false;
            }

            managed.ScoreboardFlair = itemDefIndex;
            ApplyProfilePresentationUnsafe(managed);
            return true;
        }
    }

    public bool SetDisguise(bool enabled)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return false;
            }

            _disguiseEnabled = enabled;
            foreach (var managed in _slots)
            {
                if (managed is null)
                {
                    continue;
                }

                if (enabled)
                {
                    ApplyPresentationUnsafe(managed, managed.PersonaName);
                    ApplyAvatarOverrideUnsafe(managed, out _);
                }
                else
                {
                    ClearAvatarOverrideUnsafe(managed);
                }

                if (IsCurrentClientUnsafe(managed))
                {
                    managed.Client.SetName(managed.PersonaName);
                }

                TryRefreshClientUserInfoUnsafe(managed.Slot);
                if (enabled)
                {
                    TrySetUserInfoFakePlayerUnsafe(
                        managed.Slot,
                        managed.UserId,
                        fakePlayer: false);
                }
            }

            return true;
        }
    }

    public bool SetNameSource(bool useBotInfo)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return false;
            }

            _useBotInfoNames = useBotInfo;
            return true;
        }
    }

    private void RegisterGameData()
    {
        if (_gameDataRegistered)
        {
            return;
        }

        _modSharp.GetGameData().Register(GameDataPath);
        _gameDataRegistered = true;
    }

    private void ResolveOffsets()
    {
        var gameData = _modSharp.GetGameData();
        _pingOffset = _schema.GetNetVarOffset("CCSPlayerController", "m_iPing");
        _userInfoChangedVFuncIndex =
            gameData.GetVFuncIndex("CNetworkGameServer::UserInfoChanged");

        if (_pingOffset <= 0
            || _userInfoChangedVFuncIndex <= 0)
        {
            throw new InvalidDataException("One or more BotHider offsets resolved to an invalid value.");
        }
    }

    private void PromotePendingUnsafe(
        PendingBotSlot pending,
        IPlayerController controller)
    {
        var controllerPointer = controller.GetAbsPtr();
        var originalCrosshair = TryReadCrosshair(controller);
        var originalScoreboardRanks = TryReadScoreboardRanks(controller.GetInventoryService());
        var managed = new ManagedBotSlot(
            pending.Slot,
            pending.UserId,
            pending.Client,
            pending.ClientPointer,
            controllerPointer,
            controller,
            controller.GetPlayerPawn()?.GetAbsPtr() ?? 0,
            controller.GetPlayerPawn(),
            pending.OriginalName,
            pending.SyntheticSteamId,
            pending.Ping,
            pending.PersonaName,
            pending.Profile,
            originalCrosshair,
            originalScoreboardRanks);

        _slots[pending.Slot] = managed;
        BotIdentityRegistry.MarkManaged(pending.Slot);

        ApplyPresentationUnsafe(managed, pending.PersonaName);
        ApplyProfilePresentationUnsafe(managed);
        if (_disguiseEnabled)
        {
            ApplyAvatarOverrideUnsafe(managed, out _);
            TryRefreshClientUserInfoUnsafe(managed.Slot);
            TrySetUserInfoFakePlayerUnsafe(
                managed.Slot,
                managed.UserId,
                fakePlayer: false);
        }

        _logger.LogInformation(
            "BotHider adopted slot {Slot} as '{Name}' with profile {Profile} and presentation SteamID {SteamId}; only userinfo Fakeplayer is rewritten.",
            pending.Slot,
            pending.PersonaName,
            pending.Profile?.Name ?? "fallback",
            pending.SyntheticSteamId);
    }

    private void RefreshManagedSlotUnsafe(ManagedBotSlot managed)
    {
        if (!managed.Client.IsValid)
        {
            return;
        }

        if (managed.Controller.IsValidEntity)
        {
            managed.ControllerPointer = managed.Controller.GetAbsPtr();
            var pawn = managed.Controller.GetPlayerPawn();
            managed.Pawn = pawn;
            managed.PawnPointer = pawn?.GetAbsPtr() ?? 0;
        }

        ApplyPresentationUnsafe(managed, managed.PersonaName);
        ApplyProfilePresentationUnsafe(managed);
        if (_disguiseEnabled)
        {
            ApplyAvatarOverrideUnsafe(managed, out _);
            TrySetUserInfoFakePlayerUnsafe(
                managed.Slot,
                managed.UserId,
                fakePlayer: false);
        }
    }

    private unsafe void ApplyPresentationUnsafe(ManagedBotSlot managed, string personaName)
    {
        if (managed.Controller.IsValidEntity)
        {
            managed.Controller.PlayerName = personaName;
            managed.Controller.NetworkStateChanged("m_iszPlayerName");
            *(int*)(managed.ControllerPointer + _pingOffset) = managed.Ping;
        }

        if (!string.Equals(
                managed.Client.Name,
                personaName,
                StringComparison.Ordinal))
        {
            managed.Client.SetName(personaName);
        }
    }

    private void ApplyProfilePresentationUnsafe(ManagedBotSlot managed)
    {
        if (!managed.Controller.IsValidEntity)
        {
            return;
        }

        try
        {
            var crosshair = managed.CrosshairCode;
            if (!string.Equals(
                    ReadCrosshair(managed.Controller),
                    crosshair,
                    StringComparison.Ordinal))
            {
                managed.Controller.SetNetVar(
                    "m_szCrosshairCodes",
                    crosshair,
                    BotIdentityProfile.MaxCrosshairUtf8Bytes + 1);
                Interlocked.Increment(ref _crosshairWrites);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _presentationErrors);
            _logger.LogDebug(
                exception,
                "BotHider failed to apply crosshair for slot {Slot}.",
                managed.Slot);
        }

        try
        {
            var inventory = managed.Controller.GetInventoryService();
            if (inventory is null)
            {
                return;
            }

            var ranks = inventory.GetSchemaFixedArray<uint>("m_rank");
            var flair = managed.ScoreboardFlair;
            var changed = false;
            for (var index = 0; index < ranks.Size; index++)
            {
                if (ranks[index] == flair)
                {
                    continue;
                }

                ranks[index] = flair;
                inventory.NetworkStateChanged(
                    "m_rank",
                    extraOffset: checked((ushort)(index * sizeof(uint))));
                changed = true;
            }

            if (changed)
            {
                managed.Controller.NetworkStateChanged("m_pInventoryServices");
                Interlocked.Increment(ref _flairWrites);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _presentationErrors);
            _logger.LogDebug(
                exception,
                "BotHider failed to apply scoreboard flair for slot {Slot}.",
                managed.Slot);
        }
    }

    private void ReleaseUnsafe(
        ManagedBotSlot managed,
        bool restore)
    {
        BotIdentityRegistry.Release(managed.Slot);
        ClearAvatarOverrideUnsafe(managed);
        if (!restore
            || !managed.Client.IsValid
            || managed.Client.GetAbsPtr() != managed.ClientPointer
            || managed.Client.UserId.AsPrimitive() != managed.UserId)
        {
            return;
        }

        managed.Client.SetName(managed.OriginalName);

        if (managed.Controller.IsValidEntity)
        {
            managed.Controller.PlayerName = managed.OriginalName;
            managed.Controller.NetworkStateChanged("m_iszPlayerName");
            RestoreProfilePresentationUnsafe(managed);
        }

        TryRefreshClientUserInfoUnsafe(managed.Slot);
    }

    private void RestoreProfilePresentationUnsafe(ManagedBotSlot managed)
    {
        try
        {
            managed.Controller.SetNetVar(
                "m_szCrosshairCodes",
                managed.OriginalCrosshair,
                BotIdentityProfile.MaxCrosshairUtf8Bytes + 1);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _presentationErrors);
            _logger.LogDebug(
                exception,
                "BotHider failed to restore crosshair for slot {Slot}.",
                managed.Slot);
        }

        try
        {
            var inventory = managed.Controller.GetInventoryService();
            if (inventory is null)
            {
                return;
            }

            var ranks = inventory.GetSchemaFixedArray<uint>("m_rank");
            var count = Math.Min(ranks.Size, managed.OriginalScoreboardRanks.Length);
            for (var index = 0; index < count; index++)
            {
                ranks[index] = managed.OriginalScoreboardRanks[index];
                inventory.NetworkStateChanged(
                    "m_rank",
                    extraOffset: checked((ushort)(index * sizeof(uint))));
            }

            managed.Controller.NetworkStateChanged("m_pInventoryServices");
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _presentationErrors);
            _logger.LogDebug(
                exception,
                "BotHider failed to restore scoreboard flair for slot {Slot}.",
                managed.Slot);
        }
    }

    private bool ApplyAvatarOverrideUnsafe(
        ManagedBotSlot managed,
        out string error)
    {
        error = string.Empty;
        if (managed.AvatarBytes is null)
        {
            ClearAvatarOverrideUnsafe(managed);
            return true;
        }

        if (!TryResolveAvatarTableUnsafe(
                reserveSentinel: true,
                out var table,
                out error))
        {
            Interlocked.Increment(ref _avatarErrors);
            return false;
        }
        if (managed.AvatarApplied
            && managed.AvatarAppliedSteamId == managed.SyntheticSteamId)
        {
            return true;
        }

        ClearAvatarOverrideUnsafe(managed);
        var key = managed.SyntheticSteamId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var index = table.FindStringIndex(key);
            if (index == 0)
            {
                error = "refusing to use reserved string-table index 0";
                Interlocked.Increment(ref _avatarErrors);
                return false;
            }
            if (index < 0)
            {
                index = table.AddString(true, key, managed.AvatarBytes);
                if (index <= 0)
                {
                    error = "failed to allocate a string-table entry";
                    Interlocked.Increment(ref _avatarErrors);
                    return false;
                }
            }
            else
            {
                table.SetStringUserData(index, managed.AvatarBytes);
            }

            managed.AvatarApplied = true;
            managed.AvatarAppliedSteamId = managed.SyntheticSteamId;
            Interlocked.Increment(ref _avatarWrites);
            _logger.LogInformation(
                "BotHider applied custom avatar to slot {Slot}, SteamID {SteamId}, {Bytes} bytes at string-table index {Index}.",
                managed.Slot,
                managed.SyntheticSteamId,
                managed.AvatarBytes.Length,
                index);
            return true;
        }
        catch (Exception exception)
        {
            error = $"failed to write ServerAvatarOverrides: {exception.Message}";
            Interlocked.Increment(ref _avatarErrors);
            return false;
        }
    }

    private void ClearAvatarOverrideUnsafe(ManagedBotSlot managed)
    {
        if (!managed.AvatarApplied || managed.AvatarAppliedSteamId == 0)
        {
            managed.AvatarApplied = false;
            managed.AvatarAppliedSteamId = 0;
            return;
        }

        var appliedSteamId = managed.AvatarAppliedSteamId;
        managed.AvatarApplied = false;
        managed.AvatarAppliedSteamId = 0;
        if (!TryResolveAvatarTableUnsafe(
                reserveSentinel: false,
                out var table,
                out _))
        {
            return;
        }

        try
        {
            var key = appliedSteamId.ToString(CultureInfo.InvariantCulture);
            var index = table.FindStringIndex(key);
            if (index > 0)
            {
                table.SetStringUserData(index, null);
                Interlocked.Increment(ref _avatarClears);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _avatarErrors);
            _logger.LogDebug(
                exception,
                "BotHider failed to clear custom avatar for SteamID {SteamId}.",
                appliedSteamId);
        }
    }

    private void ClearAllAvatarOverridesUnsafe()
    {
        foreach (var managed in _slots)
        {
            if (managed is not null)
            {
                ClearAvatarOverrideUnsafe(managed);
                managed.AvatarBytes = null;
            }
        }

        _avatarTable = null;
        _avatarTablePointer = 0;
    }

    private unsafe bool TryResolveAvatarTableUnsafe(
        bool reserveSentinel,
        out INetworkingStringTable table,
        out string error)
    {
        error = string.Empty;
        table = null!;
        try
        {
            var current = _modSharp.FindStringTable("ServerAvatarOverrides");
            if (current is null)
            {
                error = "ServerAvatarOverrides is unavailable";
                return false;
            }

            var pointer = current.GetAbsPtr();
            if (_avatarTablePointer != pointer)
            {
                _avatarTable = current;
                _avatarTablePointer = pointer;
                foreach (var managed in _slots)
                {
                    if (managed is null)
                    {
                        continue;
                    }

                    managed.AvatarApplied = false;
                    managed.AvatarAppliedSteamId = 0;
                }
            }

            table = _avatarTable ?? current;
            if (!reserveSentinel)
            {
                return true;
            }

            if (table.GetStringCount() == 0)
            {
                var sentinel = table.AddString(
                    true,
                    "__bothider_no_avatar__",
                    null);
                if (sentinel != 0)
                {
                    error = "failed to reserve string-table index 0";
                    return false;
                }
            }

            var fallback = table.GetStringUserData(0);
            if (fallback is not null && fallback->Size != 0)
            {
                error = "string-table index 0 contains avatar data";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = $"failed to access ServerAvatarOverrides: {exception.Message}";
            return false;
        }
    }

    private void RestoreAllUnsafe()
    {
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            if (_slots[slot] is not { } managed)
            {
                continue;
            }

            ReleaseUnsafe(managed, restore: true);
            _slots[slot] = null;
        }

        for (var slot = 0; slot < _pendingSlots.Length; slot++)
        {
            if (_pendingSlots[slot] is not { } pending)
            {
                continue;
            }

            ReleasePendingUnsafe(pending);
            _pendingSlots[slot] = null;
        }
    }

    private string PersonaName(int slot)
    {
        var names = _config.PersonaNames;
        var name = names[slot % names.Length];
        var cycle = slot / names.Length;
        return cycle == 0 ? name : $"{name}{cycle + 1}";
    }

    private int StablePing(int slot)
    {
        var span = _config.FakePingMax - _config.FakePingMin + 1;
        var mixed = unchecked((uint)(slot * 1103515245 + 12345));
        return _config.FakePingMin + (int)(mixed % (uint)span);
    }

    private HashSet<ulong> CollectUnavailableSteamIdsUnsafe()
    {
        var unavailable = new HashSet<ulong>();
        foreach (var managed in _slots)
        {
            if (managed is not null)
            {
                unavailable.Add(managed.SyntheticSteamId);
            }
        }

        foreach (var pending in _pendingSlots)
        {
            if (pending is not null)
            {
                unavailable.Add(pending.SyntheticSteamId);
            }
        }

        foreach (var client in _clients.GetGameClients(inGame: false))
        {
            if (!client.IsValid
                || client.IsHltv
                || BotIdentityRegistry.IsBot(
                    client.IsFakeClient,
                    client.Slot.AsPrimitive()))
            {
                continue;
            }

            var steamId = (ulong)client.SteamId;
            if (steamId != 0)
            {
                unavailable.Add(steamId);
            }
        }

        return unavailable;
    }

    private static string ReadCrosshair(IPlayerController controller)
        => controller.GetNetVar<string>("m_szCrosshairCodes");

    private static string TryReadCrosshair(IPlayerController controller)
    {
        try
        {
            return ReadCrosshair(controller);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static uint[] TryReadScoreboardRanks(IInventoryService? inventory)
    {
        if (inventory is null)
        {
            return [];
        }

        try
        {
            var ranks = inventory.GetSchemaFixedArray<uint>("m_rank");
            var values = new uint[ranks.Size];
            for (var index = 0; index < ranks.Size; index++)
            {
                values[index] = ranks[index];
            }

            return values;
        }
        catch
        {
            return [];
        }
    }

    private bool IsCurrentClientUnsafe(ManagedBotSlot managed)
        => managed.Client.IsValid
            && managed.Client.GetAbsPtr() == managed.ClientPointer
            && managed.Client.UserId.AsPrimitive() == managed.UserId;

    private void ReleasePendingUnsafe(PendingBotSlot pending)
    {
        BotIdentityRegistry.Release(pending.Slot);
    }

    private unsafe bool TrySetUserInfoFakePlayerUnsafe(
        int slot,
        int userId,
        bool fakePlayer)
    {
        var now = Environment.TickCount64;
        if (_userInfoApiDisabled
            || slot is < 0 or >= 64
            || now < _nextUserInfoAttemptAt[slot])
        {
            return false;
        }

        _nextUserInfoAttemptAt[slot] = now + 1000;
        Interlocked.Increment(ref _userInfoRewriteAttempts);
        try
        {
            var table = _modSharp.FindStringTable("userinfo");
            if (table is null)
            {
                Interlocked.Increment(ref _userInfoRewriteMisses);
                return false;
            }

            var count = table.GetStringCount();
            if (slot < 0 || slot >= count)
            {
                Interlocked.Increment(ref _userInfoRewriteMisses);
                return false;
            }

            var matched = TryRewriteUserInfoEntryUnsafe(
                table,
                slot,
                userId,
                fakePlayer);
            if (!matched)
            {
                Interlocked.Increment(ref _userInfoRewriteMisses);
            }

            return matched;
        }
        catch (Exception exception)
        {
            var errors = Interlocked.Increment(ref _userInfoRewriteErrors);
            _userInfoApiDisabled = true;
            _userInfoApiFailure = exception.GetType().Name;
            if (errors <= 3)
            {
                _logger.LogWarning(
                    exception,
                    "BotHider failed to rewrite userinfo Fakeplayer for slot {Slot}, user ID {UserId}.",
                    slot,
                    userId);
            }

            return false;
        }
    }

    private unsafe bool TryRewriteUserInfoEntryUnsafe(
        INetworkingStringTable table,
        int index,
        int userId,
        bool fakePlayer)
    {
        var userData = table.GetStringUserData(index);
        if (userData is null)
        {
            RecordInvalidUserInfoSnapshot(
                index,
                hasStructure: false,
                hasData: false,
                dataSize: 0);
            return false;
        }

        // The native table owns this structure. Snapshot both fields exactly
        // once so a concurrent engine update cannot make validation and copy
        // observe two different versions.
        var dataPointer = userData->Data;
        var dataSize = userData->Size;
        if (dataPointer is null)
        {
            RecordInvalidUserInfoSnapshot(
                index,
                hasStructure: true,
                hasData: false,
                dataSize);
            return false;
        }

        if (dataSize <= 0 || dataSize > MaxUserInfoBytes)
        {
            RecordInvalidUserInfoSnapshot(
                index,
                hasStructure: true,
                hasData: true,
                dataSize);
            return false;
        }

        var payload = new byte[dataSize];
        Marshal.Copy((nint)dataPointer, payload, 0, dataSize);
        var result = BotUserInfoPolicy.RewriteFakePlayer(
            payload,
            userId,
            fakePlayer,
            out var rewritten);
        switch (result)
        {
            case BotUserInfoRewriteResult.AlreadyDesired:
                Interlocked.Increment(ref _userInfoRewriteMatches);
                return true;
            case BotUserInfoRewriteResult.Rewritten:
                table.SetStringUserData(index, rewritten);
                Interlocked.Increment(ref _userInfoRewriteMatches);
                var writes = Interlocked.Increment(ref _userInfoRewriteWrites);
                if (writes == 1)
                {
                    _logger.LogInformation(
                        "BotHider completed its first Fakeplayer-only userinfo rewrite at index {Index}, user ID {UserId}, {Bytes} bytes.",
                        index,
                        userId,
                        rewritten.Length);
                }
                return true;
            case BotUserInfoRewriteResult.Invalid:
                Interlocked.Increment(ref _userInfoInvalidPayloads);
                return false;
            case BotUserInfoRewriteResult.NotTarget:
                Interlocked.Increment(ref _userInfoTargetMismatches);
                return false;
            default:
                return false;
        }
    }

    private void RecordInvalidUserInfoSnapshot(
        int index,
        bool hasStructure,
        bool hasData,
        int dataSize)
    {
        var invalid = Interlocked.Increment(ref _userInfoInvalidSnapshots);
        if (!hasStructure)
        {
            Interlocked.Increment(ref _userInfoNullStructures);
        }
        else if (!hasData)
        {
            Interlocked.Increment(ref _userInfoNullData);
        }
        else
        {
            Interlocked.Increment(ref _userInfoInvalidSizes);
            _userInfoApiDisabled = true;
            _userInfoApiFailure =
                $"native user-data size {dataSize} is outside 1..{MaxUserInfoBytes}";
            _logger.LogError(
                "BotHider disabled userinfo rewriting for this activation: {Reason}. This indicates that the current CS2 string-table ABI is incompatible with ModSharp's public INetworkingStringTable user-data methods.",
                _userInfoApiFailure);
        }

        if (invalid == 1 && !_userInfoApiDisabled)
        {
            _logger.LogInformation(
                "BotHider first unavailable userinfo snapshot at index {Index}: structure={HasStructure}, data={HasData}, size={Size}; retries are limited to once per second per slot.",
                index,
                hasStructure,
                hasData,
                dataSize);
        }
    }

    private void TryRefreshClientUserInfoUnsafe(int slot)
    {
        try
        {
            RefreshClientUserInfoUnsafe(slot);
        }
        catch (Exception exception)
        {
            var errors = Interlocked.Increment(ref _userInfoRefreshErrors);
            if (errors <= 3)
            {
                _logger.LogWarning(
                    exception,
                    "BotHider failed to request a userinfo refresh for slot {Slot}.",
                    slot);
            }
        }
    }

    private unsafe void RefreshClientUserInfoUnsafe(int slot)
    {
        var serverPointer = _modSharp.GetIServer().GetAbsPtr();
        if (serverPointer == 0)
        {
            throw new InvalidOperationException(
                "CNetworkGameServer pointer is unavailable.");
        }

        var vtable = *(nint**)serverPointer;
        if (vtable is null)
        {
            throw new InvalidOperationException(
                "CNetworkGameServer vtable is unavailable.");
        }

        var target = vtable[_userInfoChangedVFuncIndex];
        if (target == 0)
        {
            throw new InvalidOperationException(
                "CNetworkGameServer::UserInfoChanged is unavailable.");
        }

        ((delegate* unmanaged<nint, int, void>)target)(
            serverPointer,
            slot);
        Interlocked.Increment(ref _engineClientRefreshes);
    }

    private sealed class ManagedBotSlot
    {
        public ManagedBotSlot(
            int slot,
            int userId,
            IGameClient client,
            nint clientPointer,
            nint controllerPointer,
            IPlayerController controller,
            nint pawnPointer,
            IPlayerPawn? pawn,
            string originalName,
            ulong syntheticSteamId,
            int ping,
            string personaName,
            BotIdentityProfile? profile,
            string originalCrosshair,
            uint[] originalScoreboardRanks)
        {
            Slot = slot;
            UserId = userId;
            Client = client;
            ClientPointer = clientPointer;
            ControllerPointer = controllerPointer;
            Controller = controller;
            PawnPointer = pawnPointer;
            Pawn = pawn;
            OriginalName = originalName;
            SyntheticSteamId = syntheticSteamId;
            Ping = ping;
            PersonaName = personaName;
            Profile = profile;
            CrosshairCode = profile?.CrosshairCode ?? string.Empty;
            ScoreboardFlair = profile?.ScoreboardFlair ?? 0U;
            OriginalCrosshair = originalCrosshair;
            OriginalScoreboardRanks = originalScoreboardRanks;
        }

        public int Slot { get; }
        public int UserId { get; }
        public IGameClient Client { get; set; }
        public nint ClientPointer { get; }
        public nint ControllerPointer { get; set; }
        public IPlayerController Controller { get; set; }
        public nint PawnPointer { get; set; }
        public IPlayerPawn? Pawn { get; set; }
        public string OriginalName { get; }
        public ulong SyntheticSteamId { get; set; }
        public int Ping { get; }
        public string PersonaName { get; set; }
        public BotIdentityProfile? Profile { get; }
        public string CrosshairCode { get; set; }
        public uint ScoreboardFlair { get; set; }
        public byte[]? AvatarBytes { get; set; }
        public bool AvatarApplied { get; set; }
        public ulong AvatarAppliedSteamId { get; set; }
        public string OriginalCrosshair { get; }
        public uint[] OriginalScoreboardRanks { get; }
    }

    private sealed class PendingBotSlot
    {
        public PendingBotSlot(
            int slot,
            int userId,
            IGameClient client,
            nint clientPointer,
            string originalName,
            ulong syntheticSteamId,
            int ping,
            string personaName,
            BotIdentityProfile? profile)
        {
            Slot = slot;
            UserId = userId;
            Client = client;
            ClientPointer = clientPointer;
            OriginalName = originalName;
            SyntheticSteamId = syntheticSteamId;
            Ping = ping;
            PersonaName = personaName;
            Profile = profile;
        }

        public int Slot { get; }
        public int UserId { get; }
        public IGameClient Client { get; set; }
        public nint ClientPointer { get; }
        public string OriginalName { get; }
        public ulong SyntheticSteamId { get; }
        public int Ping { get; }
        public string PersonaName { get; }
        public BotIdentityProfile? Profile { get; }
    }

}
