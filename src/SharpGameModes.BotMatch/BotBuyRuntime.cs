using System.Globalization;
using Microsoft.Extensions.Logging;
using SharpGameModes.Contracts;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Pure ModSharp port of CS2-Bot-Improver's BotBuy 1.0.12 economy logic.
/// Delayed work is generation-guarded and exists only while BotMatch is active.
/// </summary>
internal sealed class BotBuyRuntime : IDisposable
{
    private readonly IModSharp _modSharp;
    private readonly IClientManager _clients;
    private readonly IEntityManager _entities;
    private readonly IConVarManager _conVars;
    private readonly ILogger _logger;
    private readonly Dictionary<int, InventorySnapshot> _previous = [];
    private readonly Dictionary<BotBuyTeam, HashSet<int>> _poorUserIds = [];
    private bool _active;
    private int _generation;
    private long _purchases;
    private long _refunds;
    private long _swaps;
    private long _weaponGifts;
    private long _armorGifts;
    private long _errors;

    public BotBuyRuntime(
        ISharedSystem shared,
        IClientManager clients,
        ILogger logger)
    {
        _modSharp = shared.GetModSharp();
        _clients = clients;
        _entities = shared.GetEntityManager();
        _conVars = shared.GetConVarManager();
        _logger = logger;
    }

    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _generation++;
        ResetMap();
        _logger.LogInformation(
            "Pure ModSharp BotBuy 1.0.12 enabled with full economy, refund, gifting and special-round logic.");
    }

    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _generation++;
        _previous.Clear();
        _poorUserIds.Clear();
        _logger.LogInformation(
            "Pure ModSharp BotBuy disabled. Purchases {Purchases}, refunds {Refunds}, swaps {Swaps}, weapon gifts {WeaponGifts}, armor gifts {ArmorGifts}; errors {Errors}.",
            Interlocked.Read(ref _purchases),
            Interlocked.Read(ref _refunds),
            Interlocked.Read(ref _swaps),
            Interlocked.Read(ref _weaponGifts),
            Interlocked.Read(ref _armorGifts),
            Interlocked.Read(ref _errors));
    }

    public void ResetMap()
    {
        _generation++;
        _previous.Clear();
        _poorUserIds.Clear();
    }

    public void Release(IGameClient client)
    {
        _previous.Remove(client.UserId.AsPrimitive());
        foreach (var poor in _poorUserIds.Values)
        {
            poor.Remove(client.UserId.AsPrimitive());
        }
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
                case "player_death":
                    ClearPreviousInventory(
                        gameEvent.GetPlayerController("userid"));
                    break;
                case "round_end":
                    SavePreviousInventory();
                    break;
                case "round_start":
                    HandleRoundStart();
                    break;
                case "round_freeze_end":
                    HandleRoundFreezeEnd();
                    break;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or OverflowException)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(
                exception,
                "BotBuy event handler failed for {EventName}.",
                gameEvent.Name);
        }
    }

    public string GetStatus()
        => $"BotBuy active: histories {_previous.Count}, purchases {Interlocked.Read(ref _purchases)}, refunds {Interlocked.Read(ref _refunds)}, swaps {Interlocked.Read(ref _swaps)}, weapon gifts {Interlocked.Read(ref _weaponGifts)}, armor gifts {Interlocked.Read(ref _armorGifts)}, errors {Interlocked.Read(ref _errors)}.";

    public void Dispose() => Deactivate();

    private void HandleRoundStart()
    {
        if (string.Equals(
                _modSharp.GetMapName(),
                "aim_rush",
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(GetConVarString("bot_loadout")))
        {
            return;
        }

        var players = SnapshotPlayers(requireAlive: true);
        var ctBots = players
            .Where(player => player.IsBot
                && player.Team == BotBuyTeam.CounterTerrorist)
            .ToArray();
        var tBots = players
            .Where(player => player.IsBot
                && player.Team == BotBuyTeam.Terrorist)
            .ToArray();
        SnapshotPoorPlayers(players);

        foreach (var bot in players.Where(player => player.IsBot))
        {
            if (Random.Shared.NextSingle() < 0.8f)
            {
                Swap(bot, "weapon_hkp2000", "weapon_usp_silencer");
            }
        }

        var allCtInRange = ctBots.Length > 0
            && ctBots.All(
                bot => GetMoney(bot) is > 1000 and < 2800);
        var allTInRange = tBots.Length > 0
            && tBots.All(
                bot => GetMoney(bot) is > 1000 and < 2800);
        Schedule(() => RunForceBuy(allCtInRange, allTInRange), 0.4);

        foreach (var bot in players.Where(player => player.IsBot))
        {
            var initialGun = GetActiveWeaponName(bot.Pawn);
            if (initialGun is not ("weapon_scar20" or "weapon_g3sg1"))
            {
                continue;
            }

            var slot = bot.Slot;
            var userId = bot.UserId;
            Schedule(
                () =>
                {
                    if (!TryResolveLiveBot(slot, userId, out var current))
                    {
                        return;
                    }

                    var currentGun = GetActiveWeaponName(current.Pawn);
                    if (currentGun is not null
                        && currentGun is not ("weapon_scar20" or "weapon_g3sg1"))
                    {
                        Refund(current, currentGun);
                    }
                },
                0.5);
        }

        foreach (var bot in players.Where(player => player.IsBot))
        {
            var roll = Random.Shared.NextSingle();
            if (roll < 0.06f)
            {
                continue;
            }

            var replacement = roll < 0.53f
                ? "weapon_m4a1"
                : "weapon_m4a1_silencer";
            ScheduleBot(
                bot,
                current => Swap(current, "weapon_aug", replacement),
                0.4);
        }

        Schedule(ReplaceP90s, 0.4);
        Schedule(ReplaceXm1014s, 0.4);
        Schedule(ReplaceSsg08s, 0.4);
        Schedule(RunBigAdvantage, 0.6);
        Schedule(BuyDefusers, 3);
        Schedule(RestorePartiallyUsedArmor, 1);
        Schedule(RunSpecialRoundPurchases, 0.4);
        Schedule(GiftWeapons, 2);
        Schedule(GiftArmor, 2.5);
    }

    private void RunForceBuy(bool allCtInRange, bool allTInRange)
    {
        var players = SnapshotPlayers(requireAlive: true);
        if (allCtInRange)
        {
            var roll = Random.Shared.NextSingle();
            foreach (var bot in players.Where(
                         player => player.IsBot
                             && player.Team == BotBuyTeam.CounterTerrorist))
            {
                if (roll < 0.10f)
                {
                    Swap(bot, "weapon_usp_silencer", "weapon_fiveseven");
                    Swap(bot, "weapon_hkp2000", "weapon_fiveseven");
                }
                else if (roll < 0.20f)
                {
                    Buy(bot, "weapon_mp9");
                }
            }
        }

        if (!allTInRange)
        {
            return;
        }

        var terroristRoll = Random.Shared.NextSingle();
        foreach (var bot in players.Where(
                     player => player.IsBot
                         && player.Team == BotBuyTeam.Terrorist))
        {
            if (terroristRoll < 0.10f)
            {
                Swap(bot, "weapon_glock", "weapon_tec9");
            }
            else if (terroristRoll < 0.20f)
            {
                Buy(bot, "weapon_mac10");
            }
        }
    }

    private void ReplaceP90s()
    {
        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot
                         && GetActiveWeaponName(player.Pawn) == "weapon_p90"))
        {
            var roll = Random.Shared.NextSingle();
            if (roll < 0.3f)
            {
                Swap(bot, "weapon_p90", "weapon_bizon");
            }
            else if (roll < 0.4f)
            {
                Swap(bot, "weapon_p90", "weapon_mp7");
            }
            else if (roll < 0.5f)
            {
                Swap(bot, "weapon_p90", "weapon_mp5sd");
            }
            else if (roll < 0.6f)
            {
                Swap(bot, "weapon_p90", "weapon_ump45");
            }
        }
    }

    private void ReplaceXm1014s()
    {
        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot
                         && GetActiveWeaponName(player.Pawn) == "weapon_xm1014"))
        {
            var roll = Random.Shared.NextSingle();
            if (roll < 0.5f)
            {
                Swap(bot, "weapon_xm1014", "weapon_negev");
            }
            else if (bot.Team == BotBuyTeam.CounterTerrorist
                && roll < 0.6f)
            {
                Swap(bot, "weapon_xm1014", "weapon_mag7");
            }
            else if (bot.Team == BotBuyTeam.Terrorist
                && roll < 0.65f)
            {
                Swap(bot, "weapon_xm1014", "weapon_sawedoff");
            }
        }
    }

    private void ReplaceSsg08s()
    {
        if (MathF.Abs(GetConVarFloat("sv_gravity", 800f) - 230f) < 0.01f)
        {
            return;
        }

        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot
                         && GetActiveWeaponName(player.Pawn) == "weapon_ssg08"))
        {
            var roll = Random.Shared.NextSingle();
            if (roll < 0.05f)
            {
                if (bot.Team == BotBuyTeam.CounterTerrorist)
                {
                    Refund(bot, "weapon_usp_silencer");
                    Refund(bot, "weapon_hkp2000");
                }
                else
                {
                    Refund(bot, "weapon_glock");
                }

                Swap(bot, "weapon_ssg08", "weapon_deagle");
            }
            else if (roll < 0.45f)
            {
                Swap(
                    bot,
                    "weapon_ssg08",
                    bot.Team == BotBuyTeam.Terrorist
                        ? "weapon_mac10"
                        : "weapon_mp9");
            }
        }
    }

    private void RunBigAdvantage()
    {
        if (IsFirstRoundOfHalf())
        {
            return;
        }

        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot && GetMoney(player) >= 5200))
        {
            var currentWeapon = GetActiveWeaponName(bot.Pawn);
            if (currentWeapon is null)
            {
                continue;
            }

            var roll = Random.Shared.NextSingle();
            if (roll < 0.10f)
            {
                Swap(
                    bot,
                    currentWeapon,
                    bot.Team == BotBuyTeam.CounterTerrorist
                        ? "weapon_scar20"
                        : "weapon_g3sg1");
            }
            else if (roll < 0.14f)
            {
                Swap(bot, currentWeapon, "weapon_m249");
            }
        }
    }

    private void BuyDefusers()
    {
        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot
                         && player.Team == BotBuyTeam.CounterTerrorist))
        {
            var money = GetMoney(bot);
            var isPoor = IsPoor(bot);
            if (isPoor && !(IsFirstRoundOfHalf() && money == 500)
                || money < 400
                || bot.Pawn.GetItemService()?.HasDefuser == true)
            {
                continue;
            }

            Buy(bot, "item_defuser");
        }
    }

    private void RestorePartiallyUsedArmor()
    {
        if (IsFirstRoundOfHalf())
        {
            return;
        }

        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot))
        {
            var previous = PreviousInventory(bot);
            if (previous.Armor is <= 40 or > 99
                || bot.Pawn.ArmorValue <= 99
                || bot.Pawn.GetItemService()?.HasHelmet != true
                || !Refund(bot, "item_assaultsuit"))
            {
                continue;
            }

            bot.Pawn.GiveNamedItem("item_assaultsuit");
            bot.Pawn.ArmorValue = previous.Armor;
            bot.Pawn.NetworkStateChanged("m_ArmorValue");
        }
    }

    private void RunSpecialRoundPurchases()
    {
        if (!IsFirstRoundOfHalf())
        {
            return;
        }

        foreach (var bot in SnapshotPlayers(requireAlive: true)
                     .Where(player => player.IsBot))
        {
            var money = GetMoney(bot);
            var roll = Random.Shared.NextSingle();
            switch (money)
            {
                case 800:
                    RunCompetitivePistolPurchase(bot, roll);
                    break;
                case 1000:
                    RunCasualPistolPurchase(bot, roll);
                    break;
                case 10000:
                    RunOvertimePurchase(bot, roll);
                    break;
            }
        }
    }

    private void RunCompetitivePistolPurchase(
        PlayerSnapshot bot,
        float roll)
    {
        if (bot.Team == BotBuyTeam.CounterTerrorist)
        {
            if (roll < 0.50f)
            {
                Buy(bot, "item_kevlar");
            }
            else if (roll < 0.65f)
            {
                SwapCtPistol(bot, "weapon_elite");
            }
            else if (roll < 0.75f)
            {
                SwapCtPistol(bot, "weapon_p250");
            }
            else if (roll < 0.83f)
            {
                SwapCtPistol(bot, "weapon_deagle");
            }
            else if (roll < 0.91f)
            {
                SwapCtPistol(bot, "weapon_cz75a");
            }
            else if (roll < 0.98f)
            {
                SwapCtPistol(bot, "weapon_fiveseven");
            }
            else
            {
                SwapCtPistol(bot, "weapon_revolver");
            }

            return;
        }

        if (roll < 0.50f)
        {
            Buy(bot, "item_kevlar");
        }
        else if (roll < 0.65f)
        {
            Swap(bot, "weapon_glock", "weapon_elite");
        }
        else if (roll < 0.77f)
        {
            Swap(bot, "weapon_glock", "weapon_p250");
        }
        else if (roll < 0.85f)
        {
            Swap(bot, "weapon_glock", "weapon_deagle");
        }
        else if (roll < 0.87f)
        {
            Swap(bot, "weapon_glock", "weapon_revolver");
        }
        else
        {
            Swap(bot, "weapon_glock", "weapon_tec9");
        }
    }

    private void RunCasualPistolPurchase(
        PlayerSnapshot bot,
        float roll)
    {
        if (bot.Team == BotBuyTeam.CounterTerrorist)
        {
            if (roll < 0.20f)
            {
                SwapCtPistol(bot, "weapon_elite");
            }
            else if (roll < 0.50f)
            {
                SwapCtPistol(bot, "weapon_deagle");
            }
            else if (roll < 0.65f)
            {
                SwapCtPistol(bot, "weapon_cz75a");
            }
            else if (roll < 0.95f)
            {
                SwapCtPistol(bot, "weapon_fiveseven");
            }
            else
            {
                SwapCtPistol(bot, "weapon_revolver");
            }

            return;
        }

        if (roll < 0.20f)
        {
            Swap(bot, "weapon_glock", "weapon_elite");
        }
        else if (roll < 0.30f)
        {
            Swap(bot, "weapon_glock", "weapon_p250");
        }
        else if (roll < 0.55f)
        {
            Swap(bot, "weapon_glock", "weapon_deagle");
        }
        else if (roll < 0.60f)
        {
            Swap(bot, "weapon_glock", "weapon_revolver");
        }
        else
        {
            Swap(bot, "weapon_glock", "weapon_tec9");
        }
    }

    private void RunOvertimePurchase(PlayerSnapshot bot, float roll)
    {
        Buy(bot, "item_assaultsuit");
        if (bot.Team == BotBuyTeam.CounterTerrorist)
        {
            if (roll < 0.35f)
            {
                Buy(bot, "weapon_m4a1");
            }
            else if (roll < 0.70f)
            {
                Buy(bot, "weapon_m4a1_silencer");
            }
            else if (roll < 0.90f)
            {
                Buy(bot, "weapon_awp");
            }
            else
            {
                Buy(bot, "weapon_scar20");
            }
        }
        else if (roll < 0.70f)
        {
            Buy(bot, "weapon_ak47");
        }
        else if (roll < 0.90f)
        {
            Buy(bot, "weapon_awp");
        }
        else
        {
            Buy(bot, "weapon_g3sg1");
        }
    }

    private void GiftWeapons()
    {
        if (IsFirstRoundOfHalf())
        {
            return;
        }

        foreach (var team in new[]
                 {
                     BotBuyTeam.CounterTerrorist,
                     BotBuyTeam.Terrorist,
                 })
        {
            var players = SnapshotPlayers(requireAlive: true)
                .Where(player => player.Team == team)
                .ToArray();
            var poor = players
                .Where(player => IsPoor(player) && !HasPrimaryWeapon(player))
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
            var richBots = players
                .Where(player => player.IsBot && GetMoney(player) >= 2900)
                .ToArray();
            if (poor.Count == 0 || richBots.Length == 0)
            {
                continue;
            }

            var gifted = new HashSet<int>();
            var poorIndex = 0;
            foreach (var rich in richBots)
            {
                if (poorIndex >= poor.Count)
                {
                    break;
                }

                var price = team == BotBuyTeam.CounterTerrorist
                    ? 2900
                    : 2700;
                var maxGive = Math.Min(3, GetMoney(rich) / price);
                var given = 0;
                while (given < maxGive && poorIndex < poor.Count)
                {
                    var target = poor[poorIndex++];
                    if (!gifted.Add(target.UserId))
                    {
                        continue;
                    }

                    var gun = team == BotBuyTeam.CounterTerrorist
                        ? Random.Shared.Next(2) == 0
                            ? "weapon_m4a1_silencer"
                            : "weapon_m4a1"
                        : "weapon_ak47";
                    target.Pawn.GiveNamedItem(gun);
                    Charge(rich, price);
                    Interlocked.Increment(ref _weaponGifts);
                    foreach (var teammate in players)
                    {
                        teammate.Client.Print(
                            HudPrintChannel.Chat,
                            BotBuyChatPolicy.FormatWeaponGift(
                                rich.Name,
                                target.Name));
                    }

                    given++;
                }
            }
        }
    }

    private void GiftArmor()
    {
        foreach (var team in new[]
                 {
                     BotBuyTeam.CounterTerrorist,
                     BotBuyTeam.Terrorist,
                 })
        {
            var completed = new HashSet<int>();
            while (true)
            {
                var players = SnapshotPlayers(requireAlive: true)
                    .Where(player => player.Team == team)
                    .ToArray();
                var needArmor = players
                    .Where(player => player.IsBot
                        && !completed.Contains(player.UserId)
                        && HasPrimaryWeapon(player)
                        && player.Pawn.ArmorValue == 0)
                    .ToArray();
                if (needArmor.Length == 0)
                {
                    break;
                }

                var buyer = players
                    .Where(player => player.IsBot
                        && !IsPoor(player)
                        && GetMoney(player) >= 650)
                    .OrderByDescending(GetMoney)
                    .FirstOrDefault();
                if (buyer.Controller is null)
                {
                    break;
                }

                var target = needArmor[Random.Shared.Next(needArmor.Length)];
                var buyerMoney = GetMoney(buyer);
                if (team == BotBuyTeam.Terrorist && buyerMoney < 1000)
                {
                    break;
                }

                var item = buyerMoney >= 1000
                    ? "item_assaultsuit"
                    : "item_kevlar";
                var price = buyerMoney >= 1000 ? 1000 : 650;
                target.Pawn.GiveNamedItem(item);
                Charge(buyer, price);
                completed.Add(target.UserId);
                Interlocked.Increment(ref _armorGifts);
            }
        }
    }

    private void HandleRoundFreezeEnd()
    {
        if (!string.IsNullOrEmpty(GetConVarString("bot_loadout")))
        {
            return;
        }

        _modSharp.ServerCommand(
            IsSecondToLastRoundOfHalf()
                ? "bot_eco_limit 0"
                : "bot_eco_limit 2800");
    }

    private bool Buy(PlayerSnapshot player, string itemName)
    {
        if (!player.IsBot
            || player.Controller.GetInGameMoneyService() is not { } money
            || !BotBuyPolicy.TryGetPurchasePrice(
                itemName,
                player.Team,
                player.Pawn.ArmorValue,
                out var price)
            || money.Account < price)
        {
            return false;
        }

        player.Pawn.GiveNamedItem(itemName);
        money.Account -= price;
        MarkMoneyChanged(player.Controller);
        Interlocked.Increment(ref _purchases);
        return true;
    }

    private bool Refund(PlayerSnapshot player, string itemName)
    {
        if (!player.IsBot
            || player.Controller.GetInGameMoneyService() is not { } money
            || !CanRefund(player, itemName)
            || !BotBuyPolicy.TryGetRefundPrice(
                itemName,
                player.Team,
                out var price))
        {
            return false;
        }

        if (itemName.StartsWith("weapon_", StringComparison.Ordinal))
        {
            if (!RemoveWeapon(player.Pawn, itemName))
            {
                return false;
            }
        }
        else if (itemName is "item_assaultsuit" or "item_kevlar")
        {
            if (player.Pawn.ArmorValue <= 0)
            {
                return false;
            }

            player.Pawn.ArmorValue = 0;
            player.Pawn.NetworkStateChanged("m_ArmorValue");
        }
        else
        {
            return false;
        }

        money.Account = Math.Min(
            GetConVarInt("mp_maxmoney", 16000),
            money.Account + price);
        MarkMoneyChanged(player.Controller);
        Interlocked.Increment(ref _refunds);
        return true;
    }

    private bool Swap(
        PlayerSnapshot player,
        string oldItem,
        string newItem)
    {
        if (!Refund(player, oldItem))
        {
            return false;
        }

        if (!Buy(player, newItem))
        {
            Buy(player, oldItem);
            return false;
        }

        Interlocked.Increment(ref _swaps);
        return true;
    }

    private bool CanRefund(PlayerSnapshot player, string itemName)
        => IsFirstRoundOfHalf()
            || !PreviousInventory(player).Weapons.Contains(itemName);

    private bool RemoveWeapon(IPlayerPawn pawn, string itemName)
    {
        if (pawn.GetWeaponService() is not { } weapons)
        {
            return false;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (!handle.IsValid()
                || _entities.FindEntityByHandle<IBaseWeapon>(handle)
                    is not { IsValidEntity: true } weapon
                || !BotBuyPolicy.TryGetWeaponName(
                    weapon.ItemDefinitionIndex,
                    out var weaponName)
                || weaponName != itemName)
            {
                continue;
            }

            weapon.Kill();
            return true;
        }

        return false;
    }

    private void SwapCtPistol(PlayerSnapshot player, string newItem)
    {
        Swap(player, "weapon_usp_silencer", newItem);
        Swap(player, "weapon_hkp2000", newItem);
    }

    private bool HasPrimaryWeapon(PlayerSnapshot player)
        => BotBuyPolicy.IsPrimaryWeapon(
            GetActiveWeaponName(player.Pawn));

    private static string? GetActiveWeaponName(IPlayerPawn pawn)
        => pawn.GetActiveWeapon() is { IsValidEntity: true } weapon
            && BotBuyPolicy.TryGetWeaponName(
                weapon.ItemDefinitionIndex,
                out var weaponName)
                ? weaponName
                : null;

    private void SnapshotPoorPlayers(IEnumerable<PlayerSnapshot> players)
    {
        _poorUserIds.Clear();
        _poorUserIds[BotBuyTeam.CounterTerrorist] = players
            .Where(player => player.Team == BotBuyTeam.CounterTerrorist
                && GetMoney(player) < 2800)
            .Select(player => player.UserId)
            .ToHashSet();
        _poorUserIds[BotBuyTeam.Terrorist] = players
            .Where(player => player.Team == BotBuyTeam.Terrorist
                && GetMoney(player) < 2800)
            .Select(player => player.UserId)
            .ToHashSet();
    }

    private bool IsPoor(PlayerSnapshot player)
        => _poorUserIds.TryGetValue(player.Team, out var poor)
            && poor.Contains(player.UserId);

    private void SavePreviousInventory()
    {
        if (IsFirstRoundOfHalf())
        {
            _previous.Clear();
            return;
        }

        foreach (var bot in SnapshotPlayers(requireAlive: false)
                     .Where(player => player.IsBot))
        {
            _previous[bot.UserId] = CurrentInventory(bot);
        }
    }

    private void ClearPreviousInventory(IPlayerController? controller)
    {
        if (controller?.GetGameClient() is not { } client
            || !BotIdentityRegistry.IsBot(
                client.IsFakeClient,
                client.Slot.AsPrimitive())
            || !_previous.TryGetValue(
                client.UserId.AsPrimitive(),
                out var previous))
        {
            return;
        }

        previous.Weapons.Clear();
        previous.Armor = 0;
    }

    private InventorySnapshot CurrentInventory(PlayerSnapshot player)
    {
        var inventory = new InventorySnapshot
        {
            Money = GetMoney(player),
            Armor = player.Pawn.ArmorValue,
        };
        if (player.Pawn.GetWeaponService() is not { } weapons)
        {
            return inventory;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            if (handle.IsValid()
                && _entities.FindEntityByHandle<IBaseWeapon>(handle)
                    is { IsValidEntity: true } weapon
                && BotBuyPolicy.TryGetWeaponName(
                    weapon.ItemDefinitionIndex,
                    out var weaponName))
            {
                inventory.Weapons.Add(weaponName);
            }
        }

        return inventory;
    }

    private InventorySnapshot PreviousInventory(PlayerSnapshot player)
        => !IsFirstRoundOfHalf()
            && _previous.TryGetValue(player.UserId, out var previous)
                ? previous
                : InventorySnapshot.Empty;

    private PlayerSnapshot[] SnapshotPlayers(bool requireAlive)
        => _clients.GetGameClients(inGame: true)
            .Where(client => client is { IsValid: true, IsHltv: false })
            .Select(
                client =>
                {
                    var controller = client.GetPlayerController();
                    var pawn = controller?.GetPawn()?.AsPlayerPawn();
                    var slot = client.Slot.AsPrimitive();
                    return controller is { IsValidEntity: true }
                        && pawn is { IsValidEntity: true }
                        && (!requireAlive
                            || pawn is { IsAlive: true } && pawn.Health > 0)
                            ? new PlayerSnapshot(
                                slot,
                                client.UserId.AsPrimitive(),
                                client,
                                controller,
                                pawn,
                                ToBotBuyTeam(controller.Team),
                                BotIdentityRegistry.IsBot(
                                    client.IsFakeClient,
                                    slot),
                                client.Name)
                            : default;
                })
            .Where(player => player.Controller is not null
                && player.Team is BotBuyTeam.CounterTerrorist
                    or BotBuyTeam.Terrorist)
            .ToArray();

    private bool TryResolveLiveBot(
        int slot,
        int userId,
        out PlayerSnapshot player)
    {
        player = default;
        if (!_active
            || _clients.GetGameClient(new PlayerSlot((byte)slot))
                is not { IsValid: true, IsInGame: true } client
            || client.UserId.AsPrimitive() != userId
            || !BotIdentityRegistry.IsBot(client.IsFakeClient, slot)
            || client.GetPlayerController()
                is not { IsValidEntity: true } controller
            || controller.GetPawn()?.AsPlayerPawn()
                is not { IsValidEntity: true, IsAlive: true } pawn
            || pawn.Health <= 0)
        {
            return false;
        }

        player = new PlayerSnapshot(
            slot,
            userId,
            client,
            controller,
            pawn,
            ToBotBuyTeam(controller.Team),
            IsBot: true,
            client.Name);
        return player.Team is BotBuyTeam.CounterTerrorist
            or BotBuyTeam.Terrorist;
    }

    private void ScheduleBot(
        PlayerSnapshot bot,
        Action<PlayerSnapshot> action,
        double delay)
    {
        var slot = bot.Slot;
        var userId = bot.UserId;
        Schedule(
            () =>
            {
                if (TryResolveLiveBot(slot, userId, out var current))
                {
                    action(current);
                }
            },
            delay);
    }

    private void Schedule(Action action, double delay)
    {
        var generation = _generation;
        _modSharp.PushTimer(
            () =>
            {
                if (_active && generation == _generation)
                {
                    action();
                }
            },
            delay,
            GameTimerFlags.StopOnMapEnd);
    }

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            return BotBuyPolicy.IsFirstRoundOfHalf(
                _modSharp.GetGameRules().TotalRoundsPlayed,
                GetConVarInt("mp_maxrounds", 24),
                GetConVarInt("mp_overtime_maxrounds", 6));
        }
        catch
        {
            return false;
        }
    }

    private bool IsSecondToLastRoundOfHalf()
    {
        try
        {
            return BotBuyPolicy.IsSecondToLastRoundOfHalf(
                _modSharp.GetGameRules().TotalRoundsPlayed,
                GetConVarInt("mp_maxrounds", 24));
        }
        catch
        {
            return false;
        }
    }

    private string GetConVarString(string name)
    {
        try
        {
            return (_conVars.FindConVar(name)
                    ?? _conVars.FindConVar(name, useIterator: true))
                ?.GetString()
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private int GetConVarInt(string name, int fallback)
    {
        try
        {
            return (_conVars.FindConVar(name)
                    ?? _conVars.FindConVar(name, useIterator: true))
                ?.GetInt32()
                ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private float GetConVarFloat(string name, float fallback)
        => float.TryParse(
            GetConVarString(name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;

    private static BotBuyTeam ToBotBuyTeam(CStrikeTeam team)
        => team switch
        {
            CStrikeTeam.TE => BotBuyTeam.Terrorist,
            CStrikeTeam.CT => BotBuyTeam.CounterTerrorist,
            _ => BotBuyTeam.None,
        };

    private static int GetMoney(PlayerSnapshot player)
        => player.Controller.GetInGameMoneyService()?.Account ?? 0;

    private static void MarkMoneyChanged(IPlayerController controller)
        => controller.NetworkStateChanged("m_pInGameMoneyServices");

    private static void Charge(PlayerSnapshot player, int price)
    {
        if (player.Controller.GetInGameMoneyService() is not { } money)
        {
            return;
        }

        money.Account = Math.Max(0, money.Account - price);
        MarkMoneyChanged(player.Controller);
    }

    private sealed class InventorySnapshot
    {
        public static InventorySnapshot Empty { get; } = new();

        public HashSet<string> Weapons { get; } =
            new(StringComparer.Ordinal);
        public int Money { get; init; }
        public int Armor { get; set; }
    }

    private readonly record struct PlayerSnapshot(
        int Slot,
        int UserId,
        IGameClient Client,
        IPlayerController Controller,
        IPlayerPawn Pawn,
        BotBuyTeam Team,
        bool IsBot,
        string Name);
}
