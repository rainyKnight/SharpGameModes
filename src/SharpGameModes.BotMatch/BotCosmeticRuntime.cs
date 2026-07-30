using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp port of CS2-Bot-Improver's BotRandomizer. A loadout is
/// stable for a bot slot/user-id pair and is rerolled only on team change or
/// an explicit queued reroll.
/// </summary>
internal sealed class BotCosmeticRuntime : IDisposable
{
    private const string AttributeWriterLinux =
        "55 48 89 E5 41 57 41 56 49 89 FE 41 55 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85";
    private const string AttributeWriterWindows =
        "40 53 55 41 56 48 81 EC ? ? ? ? 0F 29 74 24";

    private readonly ISharedSystem _shared;
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IHookManager _hooks;
    private readonly ISchemaManager _schema;
    private readonly ILogger _logger;
    private readonly string _catalogPath;
    private readonly string _charmPlacementPath;
    private readonly CosmeticStateStore _states = new();
    private readonly HashSet<int> _pendingRerolls = [];
    private readonly Dictionary<nint, AppliedWeaponCosmetic> _appliedWeapons = [];
    private readonly Dictionary<int, AppliedKnifeCosmetic> _appliedKnives = [];
    private readonly Dictionary<int, AppliedGloveCosmetic> _appliedGloves = [];
    private readonly Dictionary<string, nint> _attributeNames =
        new(StringComparer.Ordinal);
    private CosmeticCatalog? _catalog;
    private CosmeticRoller? _roller;
    private nint _attributeWriter;
    private int _networkedAttributesOffset;
    private int _runtimeGeneration;
    private bool _active;
    private bool _giveNamedItemHooked;
    private bool _giveNamedItemErrorLogged;
    private bool _musicErrorLogged;
    private long _weaponApplications;
    private long _knifeApplications;
    private long _gloveApplications;
    private long _agentApplications;
    private long _musicApplications;
    private long _stickerApplications;
    private long _keychainApplications;
    private long _errors;

    public BotCosmeticRuntime(
        ISharedSystem shared,
        IClientManager clients,
        ILogger logger,
        string dataDirectory)
    {
        _shared = shared;
        _modSharp = shared.GetModSharp();
        _clients = clients;
        _entities = shared.GetEntityManager();
        _hooks = shared.GetHookManager();
        _schema = shared.GetSchemaManager();
        _logger = logger;
        _catalogPath = Path.Combine(dataDirectory, "cosmetic_catalog.json");
        _charmPlacementPath = Path.Combine(
            dataDirectory,
            "charm_placements.json");
    }

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        try
        {
            _catalog = CosmeticCatalog.Load(_catalogPath);
            var placements = CharmPlacementCatalog.Load(
                _charmPlacementPath,
                _catalog);
            _roller = new CosmeticRoller(_catalog, placements);
            _attributeWriter = _shared.GetLibraryModuleManager()
                .Server
                .FindPatternExactly(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? AttributeWriterWindows
                        : AttributeWriterLinux);
            if (_attributeWriter == nint.Zero)
            {
                throw new InvalidDataException(
                    "SetOrAddAttributeValueByName signature resolved to zero.");
            }

            _networkedAttributesOffset = _schema.GetNetVarOffset(
                "CEconItemView",
                "m_NetworkedDynamicAttributes");
            if (_networkedAttributesOffset <= 0)
            {
                throw new InvalidDataException(
                    $"CEconItemView::m_NetworkedDynamicAttributes resolved to invalid offset {_networkedAttributesOffset}.");
            }

            _hooks.GiveNamedItem.InstallHookPost(
                OnGiveNamedItemPost,
                priority: 20);
            _giveNamedItemHooked = true;
            _active = true;
            _runtimeGeneration++;
            ResetMap();
            RestoreAllBots();
            _logger.LogInformation(
                "Pure ModSharp BotRandomizer 1.3.0 enabled: {Weapons} weapons, {Paints} paints, {Stickers} stickers, {Charms} charms, {CharmPositions} charm positions across {CharmWeapons} weapons; writer=0x{Writer:X}, attributes=0x{Offset:X}.",
                _catalog.WeaponCount,
                _catalog.WeaponPaintCount,
                _catalog.StickerKits.Count,
                _catalog.KeychainDefinitions.Count,
                placements.PlacementCount,
                placements.WeaponCount,
                _attributeWriter,
                _networkedAttributesOffset);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or OverflowException)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogError(
                exception,
                "Failed to enable pure ModSharp BotRandomizer.");
            Deactivate();
            return false;
        }
    }

    public void Deactivate()
    {
        if (!_active
            && !_giveNamedItemHooked
            && _catalog is null
            && _attributeWriter == nint.Zero)
        {
            return;
        }

        _active = false;
        _runtimeGeneration++;
        if (_giveNamedItemHooked)
        {
            _hooks.GiveNamedItem.RemoveHookPost(OnGiveNamedItemPost);
            _giveNamedItemHooked = false;
        }

        _states.Reset();
        _pendingRerolls.Clear();
        _appliedWeapons.Clear();
        _appliedKnives.Clear();
        _appliedGloves.Clear();
        _roller?.ResetMap();
        _roller = null;
        _catalog = null;
        _attributeWriter = nint.Zero;
        _networkedAttributesOffset = 0;
        _giveNamedItemErrorLogged = false;
        _musicErrorLogged = false;
        _logger.LogInformation(
            "Pure ModSharp BotRandomizer disabled. Applied weapons {Weapons}, knives {Knives}, gloves {Gloves}, agents {Agents}, music {Music}, stickers {Stickers}, charms {Charms}; errors {Errors}.",
            Interlocked.Read(ref _weaponApplications),
            Interlocked.Read(ref _knifeApplications),
            Interlocked.Read(ref _gloveApplications),
            Interlocked.Read(ref _agentApplications),
            Interlocked.Read(ref _musicApplications),
            Interlocked.Read(ref _stickerApplications),
            Interlocked.Read(ref _keychainApplications),
            Interlocked.Read(ref _errors));
    }

    public void PrecacheModels()
    {
        foreach (var model in RandomizerAssets.CounterTerroristModels)
        {
            _modSharp.PrecacheResource(model);
        }

        foreach (var model in RandomizerAssets.TerroristModels)
        {
            _modSharp.PrecacheResource(model);
        }
    }

    public void ResetMap()
    {
        _runtimeGeneration++;
        _states.Reset();
        _pendingRerolls.Clear();
        _appliedWeapons.Clear();
        _appliedKnives.Clear();
        _appliedGloves.Clear();
        _roller?.ResetMap();
    }

    public void Release(IGameClient client)
    {
        var slot = client.Slot.AsPrimitive();
        _states.Remove(slot);
        _pendingRerolls.Remove(slot);
        _appliedKnives.Remove(slot);
        _appliedGloves.Remove(slot);
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
                case "round_prestart":
                    ConsumeAllPendingRerolls();
                    break;
                case "player_spawn":
                    HandlePlayerSpawn(gameEvent);
                    break;
                case "player_team" when gameEvent is IEventPlayerTeam team:
                    HandlePlayerTeam(team);
                    break;
                case "round_mvp":
                    HandleRoundMvp(gameEvent);
                    break;
                case "item_pickup":
                    HandleItemPickup(gameEvent);
                    break;
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(
                exception,
                "BotRandomizer failed while handling {Event}.",
                gameEvent.Name);
        }
    }

    public bool QueueReroll(string? target, out string result)
    {
        if (!_active || _roller is null)
        {
            result = "BotRandomizer is available only while BotMatch is active.";
            return false;
        }

        int? targetSlot = null;
        var normalized = string.IsNullOrWhiteSpace(target) ? "all" : target;
        if (!normalized.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(normalized, out var parsed)
                || parsed is < 0 or > 63)
            {
                result = "Usage: br_reroll [all|bot slot].";
                return false;
            }

            targetSlot = parsed;
        }

        var bots = _clients.GetGameClients(inGame: true)
            .Where(
                client => IsManagedBot(client)
                    && client.GetPlayerController() is
                    {
                        Team: CStrikeTeam.TE or CStrikeTeam.CT,
                    }
                    && (targetSlot is null
                        || client.Slot.AsPrimitive() == targetSlot.Value))
            .ToArray();
        foreach (var bot in bots)
        {
            _pendingRerolls.Add(bot.Slot.AsPrimitive());
        }

        result = bots.Length == 0
            ? "No matching bot slots."
            : $"Queued {bots.Length} bot loadout(s) for the next safe spawn.";
        return bots.Length > 0;
    }

    public string GetStatus()
        => _active && _catalog is not null
            ? $"BotRandomizer active: states {_states.States.Count}, pending {_pendingRerolls.Count}, weapons {Interlocked.Read(ref _weaponApplications)}, knives {Interlocked.Read(ref _knifeApplications)}, gloves {Interlocked.Read(ref _gloveApplications)}, stickers {Interlocked.Read(ref _stickerApplications)}, charms {Interlocked.Read(ref _keychainApplications)}, errors {Interlocked.Read(ref _errors)}."
            : "BotRandomizer inactive.";

    public void Dispose()
    {
        Deactivate();
        foreach (var pointer in _attributeNames.Values)
        {
            Marshal.FreeHGlobal(pointer);
        }

        _attributeNames.Clear();
    }

    private void OnGiveNamedItemPost(
        IGiveNamedItemHookParams parameters,
        HookReturnValue<IBaseWeapon> result)
    {
        if (!_active
            || _roller is null
            || result.ReturnValue is not { } weapon
            || !IsManagedBot(parameters.Client))
        {
            return;
        }

        var slot = parameters.Client.Slot.AsPrimitive();
        var userId = parameters.Client.UserId.AsPrimitive();
        var controller = parameters.Controller;
        var state = GetOrCreateState(parameters.Client, controller);
        if (state is null)
        {
            return;
        }

        var stateGeneration = state.Generation;
        var runtimeGeneration = _runtimeGeneration;
        _modSharp.InvokeFrameAction(
            () =>
            {
                if (!_active
                    || runtimeGeneration != _runtimeGeneration
                    || !_states.IsCurrent(slot, userId, stateGeneration)
                    || !weapon.IsValidEntity)
                {
                    return;
                }

                try
                {
                    ApplyWeapon(
                        parameters.Client,
                        weapon,
                        state.Loadout);
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _errors);
                    if (!_giveNamedItemErrorLogged)
                    {
                        _giveNamedItemErrorLogged = true;
                        _logger.LogError(
                            exception,
                            "BotRandomizer GiveNamedItem post-hook failed.");
                    }
                }
            });
    }

    private void HandlePlayerSpawn(IGameEvent gameEvent)
    {
        var controller = gameEvent is IEventPlayerSpawn spawn
            ? spawn.Controller
            : gameEvent.GetPlayerController("userid");
        if (controller?.GetGameClient() is not { } client)
        {
            return;
        }

        ConsumePendingReroll(client);
        var state = GetOrCreateState(client, controller);
        if (state is null)
        {
            return;
        }

        ScheduleRestore(
            state,
            delay: 0,
            applyMusic: true,
            refreshWeapons: false);
        ScheduleWearableRestore(state, 0.10);
        ScheduleWearableRestore(state, 0.25);
    }

    private void HandlePlayerTeam(IEventPlayerTeam gameEvent)
    {
        if (gameEvent.Disconnect
            || gameEvent.Controller?.GetGameClient() is not { } client
            || !IsManagedBot(client))
        {
            return;
        }

        var team = gameEvent.NewTeam;
        if (_roller is null
            || team is not (CStrikeTeam.TE or CStrikeTeam.CT))
        {
            _states.BumpGeneration(client.Slot.AsPrimitive());
            return;
        }

        var slot = client.Slot.AsPrimitive();
        var userId = client.UserId.AsPrimitive();
        var state = _states.Reroll(
            slot,
            userId,
            (byte)team,
            preserveMusic: true,
            music => _roller.RollLoadout((byte)team, music));
        if (state is not null)
        {
            ScheduleRestore(
                state,
                delay: 0.10,
                applyMusic: false,
                refreshWeapons: false);
        }
    }

    private void HandleRoundMvp(IGameEvent gameEvent)
    {
        var controller = gameEvent.GetPlayerController("userid");
        if (controller?.GetGameClient() is not { } client
            || GetOrCreateState(client, controller) is not { } state)
        {
            return;
        }

        ApplyMusicKit(controller, state.Loadout.MusicKit);
        if (gameEvent.Editable)
        {
            gameEvent.SetInt("musickitid", state.Loadout.MusicKit);
            gameEvent.SetInt("musickitmvps", 0);
            gameEvent.SetInt("nomusic", 0);
        }
    }

    private void HandleItemPickup(IGameEvent gameEvent)
    {
        var item = gameEvent.GetString("item");
        if (string.IsNullOrWhiteSpace(item)
            || (!item.Contains("knife", StringComparison.OrdinalIgnoreCase)
                && !item.Contains(
                    "bayonet",
                    StringComparison.OrdinalIgnoreCase))
            || gameEvent.GetPlayerController("userid")?.GetGameClient()
                is not { } client
            || !IsManagedBot(client))
        {
            return;
        }

        var slot = client.Slot.AsPrimitive();
        var userId = client.UserId.AsPrimitive();
        if (!_states.TryGet(slot, out var state))
        {
            return;
        }

        Schedule(
            () =>
            {
                if (TryResolveCurrentBot(
                        slot,
                        userId,
                        state.Generation,
                        out _,
                        out _,
                        out var pawn,
                        out _))
                {
                    SyncPickedUpKnife(pawn);
                }
            },
            0);
        ScheduleKnifeSync(slot, userId, state.Generation, 0.10);
        ScheduleKnifeSync(slot, userId, state.Generation, 0.25);
    }

    private void ScheduleKnifeSync(
        int slot,
        int userId,
        long stateGeneration,
        double delay)
        => Schedule(
            () =>
            {
                if (TryResolveCurrentBot(
                        slot,
                        userId,
                        stateGeneration,
                        out _,
                        out _,
                        out var pawn,
                        out _))
                {
                    SyncPickedUpKnife(pawn);
                }
            },
            delay);

    private void ScheduleWearableRestore(
        SlotCosmeticState state,
        double delay)
    {
        var slot = state.Slot;
        var userId = state.UserId;
        var stateGeneration = state.Generation;
        Schedule(
            () =>
            {
                if (TryResolveCurrentBot(
                        slot,
                        userId,
                        stateGeneration,
                        out var client,
                        out _,
                        out var pawn,
                        out var current))
                {
                    ApplyWearables(client, pawn, current.Loadout);
                }
            },
            delay);
    }

    private void ScheduleRestore(
        SlotCosmeticState state,
        double delay,
        bool applyMusic,
        bool refreshWeapons)
    {
        var slot = state.Slot;
        var userId = state.UserId;
        var stateGeneration = state.Generation;
        Schedule(
            () =>
            {
                if (TryResolveCurrentBot(
                        slot,
                        userId,
                        stateGeneration,
                        out var client,
                        out var controller,
                        out var pawn,
                        out var current))
                {
                    ApplyIdentity(
                        controller,
                        pawn,
                        current.Loadout,
                        applyMusic);
                    ApplyWearables(client, pawn, current.Loadout);
                    if (refreshWeapons)
                    {
                        RefreshWeapons(client, pawn, current.Loadout);
                    }
                }
            },
            delay);
    }

    private void RestoreAllBots()
    {
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            if (!IsManagedBot(client)
                || client.GetPlayerController() is not { } controller
                || GetOrCreateState(client, controller) is not { } state)
            {
                continue;
            }

            ScheduleRestore(
                state,
                delay: 0,
                applyMusic: true,
                refreshWeapons: true);
        }
    }

    private void ApplyIdentity(
        IPlayerController controller,
        IPlayerPawn pawn,
        BotCosmeticLoadout loadout,
        bool applyMusic)
    {
        pawn.SetModel(loadout.AgentModel);
        pawn.NetworkStateChanged("m_CBodyComponent");
        Interlocked.Increment(ref _agentApplications);
        if (applyMusic)
        {
            ApplyMusicKit(controller, loadout.MusicKit);
        }
    }

    private void ApplyWearables(
        IGameClient client,
        IPlayerPawn pawn,
        BotCosmeticLoadout loadout)
    {
        ApplyKnife(client, pawn, loadout.Knife);
        ApplyGloves(client, pawn, loadout.Glove);
    }

    private void RefreshWeapons(
        IGameClient client,
        IPlayerPawn pawn,
        BotCosmeticLoadout loadout)
    {
        var weapons = pawn.GetWeaponService();
        if (weapons is null)
        {
            return;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (handle.IsValid()
                && _entities.FindEntityByHandle<IBaseWeapon>(handle)
                    is { IsValidEntity: true } weapon
                && !weapon.IsKnife)
            {
                ApplyWeapon(client, weapon, loadout);
            }
        }
    }

    private void ApplyWeapon(
        IGameClient client,
        IBaseWeapon weapon,
        BotCosmeticLoadout loadout)
    {
        if (_roller is null
            || !_catalog!.TryGetWeapon(weapon.ItemDefinitionIndex, out _)
            || _roller.GetOrCreateWeapon(
                loadout,
                weapon.ItemDefinitionIndex) is not { } selection)
        {
            return;
        }

        var item = weapon.AttributeContainer.Item;
        if (_appliedWeapons.TryGetValue(
                weapon.GetAbsPtr(),
                out var applied)
            && ReferenceEquals(applied.Selection, selection)
            && item.ItemId == applied.ItemId)
        {
            return;
        }

        ApplyEconSelection(
            weapon,
            client.SteamId.AccountId,
            selection);
        _appliedWeapons[weapon.GetAbsPtr()] = new AppliedWeaponCosmetic(
            selection,
            item.ItemId);
        Interlocked.Increment(ref _weaponApplications);
    }

    private void ApplyKnife(
        IGameClient client,
        IPlayerPawn pawn,
        KnifeSelection selection)
    {
        var weapons = pawn.GetWeaponService();
        if (weapons is null)
        {
            return;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (!handle.IsValid()
                || _entities.FindEntityByHandle<IBaseWeapon>(handle)
                    is not { IsValidEntity: true, IsKnife: true } knife)
            {
                continue;
            }

            var item = knife.AttributeContainer.Item;
            var fingerprint = KnifeCosmeticFingerprint.From(selection);
            var slot = client.Slot.AsPrimitive();
            if (_appliedKnives.TryGetValue(slot, out var applied)
                && applied.PawnPointer == pawn.GetAbsPtr()
                && applied.WeaponPointer == knife.GetAbsPtr()
                && applied.Fingerprint == fingerprint
                && item.ItemDefinitionIndex == selection.DefIndex)
            {
                return;
            }

            knife.AcceptInput(
                "ChangeSubclass",
                value: selection.DefIndex.ToString());
            item.SetItemDefinitionIndexLocal(selection.DefIndex);
            item.SetQualityLocal(3);
            item.SetInitializedLocal(true);
            var cosmetic = new WeaponCosmeticSelection(
                selection.PaintKit,
                0,
                selection.Wear,
                Legacy: false,
                Stickers: [],
                Keychain: null);
            ApplyEconSelection(
                knife,
                client.SteamId.AccountId,
                cosmetic,
                quality: 3);
            knife.NetworkStateChanged("m_AttributeManager");
            _appliedKnives[slot] = new AppliedKnifeCosmetic(
                pawn.GetAbsPtr(),
                knife.GetAbsPtr(),
                fingerprint);
            Interlocked.Increment(ref _knifeApplications);
            return;
        }
    }

    private void ApplyGloves(
        IGameClient client,
        IPlayerPawn pawn,
        GloveSelection selection)
    {
        var item = pawn.GetEconGloves();
        var fingerprint = GloveCosmeticFingerprint.From(selection);
        var slot = client.Slot.AsPrimitive();
        if (_appliedGloves.TryGetValue(slot, out var applied)
            && applied.PawnPointer == pawn.GetAbsPtr()
            && applied.Fingerprint == fingerprint
            && item.Initialized
            && item.ItemDefinitionIndex == selection.DefIndex
            && item.AccountId == client.SteamId.AccountId)
        {
            return;
        }

        pawn.GiveGloves(
            selection.DefIndex,
            selection.PaintKit,
            selection.Wear,
            seed: 0);
        item.SetAccountIdLocal(client.SteamId.AccountId);
        item.SetQualityLocal(4);
        item.SetInitializedLocal(true);
        AssignItemId(item);
        pawn.NetworkStateChanged("m_EconGloves");
        _appliedGloves[slot] = new AppliedGloveCosmetic(
            pawn.GetAbsPtr(),
            fingerprint);
        Interlocked.Increment(ref _gloveApplications);
    }

    private void ApplyEconSelection(
        IBaseWeapon weapon,
        uint accountId,
        WeaponCosmeticSelection selection,
        int quality = 4)
    {
        var stickers = selection.Stickers
            .OrderBy(sticker => sticker.Slot)
            .Take(4)
            .ToArray();
        (int Id, float Wear) GetSticker(int slot)
            => stickers.FirstOrDefault(sticker => sticker.Slot == slot)
                is { } sticker
                ? (unchecked((int)sticker.DefIndex), sticker.Wear)
                : default;

        var first = GetSticker(0);
        var second = GetSticker(1);
        var third = GetSticker(2);
        var fourth = GetSticker(3);
        if (!_entities.UpdateEconItemAttributes(
                weapon,
                accountId,
                string.Empty,
                selection.PaintKit,
                selection.Seed,
                selection.Wear,
                first.Id,
                first.Wear,
                second.Id,
                second.Wear,
                third.Id,
                third.Wear,
                fourth.Id,
                fourth.Wear))
        {
            throw new InvalidOperationException(
                $"ModSharp rejected bot econ attributes for entity {weapon.Index.AsPrimitive()}.");
        }

        var item = weapon.AttributeContainer.Item;
        item.SetAccountIdLocal(accountId);
        item.SetQualityLocal(quality);
        item.SetInitializedLocal(true);
        var attributes = item.GetAbsPtr() + _networkedAttributesOffset;
        SetAttribute(
            attributes,
            "set item texture prefab",
            selection.PaintKit);
        SetAttribute(
            attributes,
            "set item texture seed",
            selection.Seed);
        SetAttribute(
            attributes,
            "set item texture wear",
            selection.Wear);
        foreach (var sticker in selection.Stickers)
        {
            SetStickerAttributes(attributes, sticker);
            Interlocked.Increment(ref _stickerApplications);
        }

        if (selection.Keychain is { } keychain)
        {
            SetKeychainAttributes(attributes, keychain);
            Interlocked.Increment(ref _keychainApplications);
        }

        weapon.SetModelScale(1f);
        weapon.NetworkStateChanged("m_AttributeManager");
    }

    private void SetStickerAttributes(
        nint attributes,
        StickerSelection sticker)
    {
        var slot = $"sticker slot {sticker.Slot}";
        SetAttribute(
            attributes,
            $"{slot} id",
            AttributeEncoding.UInt32BitsToSingle(sticker.DefIndex));
        SetAttribute(
            attributes,
            $"{slot} schema",
            AttributeEncoding.UInt32BitsToSingle(sticker.Schema));
        SetAttribute(attributes, $"{slot} wear", sticker.Wear);
        if (sticker.Rotation is float rotation)
        {
            SetAttribute(attributes, $"{slot} rotation", rotation);
        }

        if (sticker.X is float x)
        {
            SetAttribute(attributes, $"{slot} offset x", x);
        }

        if (sticker.Y is float y)
        {
            SetAttribute(attributes, $"{slot} offset y", y);
        }
    }

    private void SetKeychainAttributes(
        nint attributes,
        KeychainSelection keychain)
    {
        var slot = $"keychain slot {keychain.Slot}";
        SetAttribute(
            attributes,
            $"{slot} id",
            AttributeEncoding.UInt32BitsToSingle(keychain.DefIndex));
        SetAttribute(
            attributes,
            $"{slot} seed",
            AttributeEncoding.Int32BitsToSingle(keychain.Seed));
        if (keychain.Sticker is uint sticker)
        {
            SetAttribute(
                attributes,
                $"{slot} sticker",
                AttributeEncoding.UInt32BitsToSingle(sticker));
        }

        if (keychain.X is float x)
        {
            SetAttribute(attributes, $"{slot} offset x", x);
        }

        if (keychain.Y is float y)
        {
            SetAttribute(attributes, $"{slot} offset y", y);
        }

        if (keychain.Z is float z)
        {
            SetAttribute(attributes, $"{slot} offset z", z);
        }
    }

    private unsafe void SetAttribute(
        nint attributes,
        string name,
        float value)
    {
        if (attributes == nint.Zero || _attributeWriter == nint.Zero)
        {
            throw new InvalidOperationException(
                "BotRandomizer attribute writer is unavailable.");
        }

        if (!_attributeNames.TryGetValue(name, out var namePointer))
        {
            namePointer = Marshal.StringToHGlobalAnsi(name);
            _attributeNames.Add(name, namePointer);
        }

        ((delegate* unmanaged<nint, nint, float, int>)_attributeWriter)(
            attributes,
            namePointer,
            value);
    }

    private void ApplyMusicKit(
        IPlayerController controller,
        int kitId)
    {
        try
        {
            if (controller.GetInventoryService() is { } inventory)
            {
                inventory.MusicId = checked((ushort)kitId);
            }

            controller.SetNetVar("m_iMusicKitID", kitId);
            controller.SetNetVar("m_iMusicKitMVPs", 0);
            controller.SetNetVar("m_bMvpNoMusic", false);
            Interlocked.Increment(ref _musicApplications);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            if (!_musicErrorLogged)
            {
                _musicErrorLogged = true;
                _logger.LogWarning(
                    exception,
                    "BotRandomizer could not apply a music kit.");
            }
        }
    }

    private void SyncPickedUpKnife(IPlayerPawn pawn)
    {
        var weapons = pawn.GetWeaponService();
        if (weapons is null)
        {
            return;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (!handle.IsValid()
                || _entities.FindEntityByHandle<IBaseWeapon>(handle)
                    is not { IsValidEntity: true, IsKnife: true } knife
                || !RandomizerAssets.KnifeDefIndexByName.TryGetValue(
                    knife.GetWeaponClassname(),
                    out var definitionIndex))
            {
                continue;
            }

            knife.AcceptInput(
                "ChangeSubclass",
                value: definitionIndex.ToString());
            knife.AttributeContainer.Item.SetItemDefinitionIndexLocal(
                definitionIndex);
            knife.NetworkStateChanged("m_AttributeManager");
        }
    }

    private void ConsumeAllPendingRerolls()
    {
        foreach (var client in _clients.GetGameClients(inGame: true))
        {
            ConsumePendingReroll(client);
        }
    }

    private void ConsumePendingReroll(IGameClient client)
    {
        if (_roller is null
            || !IsManagedBot(client)
            || client.GetPlayerController() is not
            {
                Team: CStrikeTeam.TE or CStrikeTeam.CT,
            } controller
            || !_pendingRerolls.Remove(client.Slot.AsPrimitive()))
        {
            return;
        }

        _states.Reroll(
            client.Slot.AsPrimitive(),
            client.UserId.AsPrimitive(),
            (byte)controller.Team,
            preserveMusic: false,
            music => _roller.RollLoadout((byte)controller.Team, music));
    }

    private SlotCosmeticState? GetOrCreateState(
        IGameClient client,
        IPlayerController controller)
    {
        if (_roller is null
            || !IsManagedBot(client)
            || controller.Team is not (CStrikeTeam.TE or CStrikeTeam.CT))
        {
            return null;
        }

        var team = (byte)controller.Team;
        return _states.GetOrCreate(
            client.Slot.AsPrimitive(),
            client.UserId.AsPrimitive(),
            team,
            music => _roller.RollLoadout(team, music));
    }

    private bool TryResolveCurrentBot(
        int slot,
        int userId,
        long stateGeneration,
        out IGameClient client,
        out IPlayerController controller,
        out IPlayerPawn pawn,
        out SlotCosmeticState state)
    {
        client = null!;
        controller = null!;
        pawn = null!;
        state = null!;
        if (!_active
            || !_states.IsCurrent(slot, userId, stateGeneration)
            || _clients.GetGameClient(new PlayerSlot((byte)slot))
                is not { IsValid: true, IsInGame: true } resolvedClient
            || resolvedClient.UserId.AsPrimitive() != userId
            || !IsManagedBot(resolvedClient)
            || resolvedClient.GetPlayerController()
                is not { IsValidEntity: true } resolvedController
            || resolvedController.GetPlayerPawn()
                is not { IsValidEntity: true, IsAlive: true } resolvedPawn
            || !_states.TryGet(slot, out var resolvedState))
        {
            return false;
        }

        client = resolvedClient;
        controller = resolvedController;
        pawn = resolvedPawn;
        state = resolvedState;
        return true;
    }

    private void Schedule(Action action, double delay)
    {
        var generation = _runtimeGeneration;
        if (delay <= 0)
        {
            _modSharp.InvokeFrameAction(
                () =>
                {
                    if (_active && generation == _runtimeGeneration)
                    {
                        action();
                    }
                });
            return;
        }

        _modSharp.PushTimer(
            () =>
            {
                if (_active && generation == _runtimeGeneration)
                {
                    action();
                }
            },
            delay,
            GameTimerFlags.StopOnMapEnd);
    }

    private static void AssignItemId(IEconItemView item)
    {
        var itemId = EconItemIdAllocator.Next();
        item.SetItemIdLowLocal((uint)(itemId & uint.MaxValue));
        item.SetItemIdHighLocal((uint)(itemId >> 32));
    }

    private static bool IsManagedBot(IGameClient? client)
        => client is
            {
                IsValid: true,
                IsInGame: true,
                IsHltv: false,
            }
            && BotIdentityRegistry.IsBot(
                client.IsFakeClient,
                client.Slot.AsPrimitive());

    private readonly record struct KnifeCosmeticFingerprint(
        ushort DefinitionIndex,
        int PaintKit,
        int WearBits)
    {
        public static KnifeCosmeticFingerprint From(
            KnifeSelection selection)
            => new(
                selection.DefIndex,
                selection.PaintKit,
                BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct GloveCosmeticFingerprint(
        ushort DefinitionIndex,
        int PaintKit,
        int WearBits)
    {
        public static GloveCosmeticFingerprint From(
            GloveSelection selection)
            => new(
                selection.DefIndex,
                selection.PaintKit,
                BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct AppliedKnifeCosmetic(
        nint PawnPointer,
        nint WeaponPointer,
        KnifeCosmeticFingerprint Fingerprint);

    private readonly record struct AppliedWeaponCosmetic(
        WeaponCosmeticSelection Selection,
        ulong ItemId);

    private readonly record struct AppliedGloveCosmetic(
        nint PawnPointer,
        GloveCosmeticFingerprint Fingerprint);
}
