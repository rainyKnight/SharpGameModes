using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.ZombieInfection;

public sealed partial class ZombieInfectionModule
{
    private static readonly string[] BuyCommands = ["buy", "autobuy", "rebuy", "buymenu"];
    private static readonly string[] DropCommands = ["drop", "+drop", "-drop"];
    private static readonly string[] TeamCommands = ["jointeam", "joinclass"];
    private static readonly string[] PlayerModelCommands =
    [
        "m", "model", "md", "models", "mesh", "mg", "skin", "mat", "materialgroup",
    ];

    private void InstallCommands()
    {
        foreach (var command in BuyCommands)
        {
            _clients.InstallCommandListener(command, OnBuyCommand);
        }

        foreach (var command in DropCommands)
        {
            _clients.InstallCommandListener(command, OnDropCommand);
        }

        foreach (var command in TeamCommands)
        {
            _clients.InstallCommandListener(command, OnJoinTeamCommand);
        }

        foreach (var command in PlayerModelCommands)
        {
            _clients.InstallCommandListener(command, OnPlayerModelCommand);
        }

        foreach (var command in ZombieWeaponCatalog.Aliases
                     .Concat(new[] { "gun", "guns", "wp", "枪", "fdy", "armor", "jia", "甲" })
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _clients.InstallCommandCallback(command, OnWeaponCommand);
            _registeredWeaponCommands.Add(command);
        }
    }

    private void RemoveCommands()
    {
        foreach (var command in BuyCommands)
        {
            _clients.RemoveCommandListener(command, OnBuyCommand);
        }

        foreach (var command in DropCommands)
        {
            _clients.RemoveCommandListener(command, OnDropCommand);
        }

        foreach (var command in TeamCommands)
        {
            _clients.RemoveCommandListener(command, OnJoinTeamCommand);
        }

        foreach (var command in PlayerModelCommands)
        {
            _clients.RemoveCommandListener(command, OnPlayerModelCommand);
        }

        foreach (var command in _registeredWeaponCommands)
        {
            _clients.RemoveCommandCallback(command, OnWeaponCommand);
        }

        _registeredWeaponCommands.Clear();
    }

    private ECommandAction OnBuyCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsEligible(client) || !_config.BlockStandardBuyCommands
            || client.GetPlayerController() is not { } player)
        {
            return ECommandAction.Skipped;
        }

        if (player.Team == CStrikeTeam.CT)
        {
            for (var index = 1; index <= command.ArgCount; index++)
            {
                if (_config.BlockedBuyItems.Contains(NormalizeBuyItem(command.GetArg(index)), StringComparer.OrdinalIgnoreCase))
                {
                    client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前模式禁用烟雾弹和闪光弹。");
                    return ECommandAction.Stopped;
                }
            }

            return ECommandAction.Skipped;
        }

        if (!_config.BlockZombieBuy || player.Team != CStrikeTeam.TE)
        {
            return ECommandAction.Skipped;
        }

        if (command.CommandName.Equals("buymenu", StringComparison.OrdinalIgnoreCase)
            || command.CommandName.Equals("buy", StringComparison.OrdinalIgnoreCase) && command.ArgCount == 0)
        {
            return ECommandAction.Skipped;
        }

        if (command.CommandName.Equals("buy", StringComparison.OrdinalIgnoreCase)
            && command.ArgCount > 0
            && Enumerable.Range(1, command.ArgCount).All(index => IsArmorBuyItem(command.GetArg(index))))
        {
            GiveFullArmor(client);
            return ECommandAction.Stopped;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 僵尸只能买甲。");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnDropCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsEligible(client) || client.GetPlayerController() is not { } player)
        {
            return ECommandAction.Skipped;
        }

        if (_config.BlockHumanWeaponDrop)
        {
            if (_config.ShowDropBlockedMessage)
            {
                client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前模式不能丢枪。");
            }

            return ECommandAction.Stopped;
        }

        if (player.Team == CStrikeTeam.TE)
        {
            StripZombieToKnife(player);
            return ECommandAction.Stopped;
        }

        return ECommandAction.Skipped;
    }

    private ECommandAction OnJoinTeamCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsEligible(client) || client.GetPlayerController() is not { } player)
        {
            return ECommandAction.Skipped;
        }

        if (IsWarmup())
        {
            Schedule(() => EnsureWarmupHuman(player), 0.05);
        }
        else if (_phase == ZombiePhase.Active)
        {
            Schedule(() => JoinActiveRoundAsZombie(player, announce: false), 0.05);
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 本模式不能自行换队，中途加入会作为僵尸复活。");
        }
        else if (_phase is ZombiePhase.Waiting or ZombiePhase.Countdown)
        {
            Schedule(() => RespawnAndApplyHuman(player), 0.05);
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 本模式不能自行换队。");
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction OnPlayerModelCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !_config.BlockPlayerModelCommandsForZombies || !IsEligible(client)
            || client.GetPlayerController() is not { Team: CStrikeTeam.TE })
        {
            return ECommandAction.Skipped;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 僵尸状态下暂时不能切换人物模型。");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWeaponCommand(IGameClient client, StringCommand command)
    {
        if (!IsActive() || !IsEligible(client))
        {
            return ECommandAction.Skipped;
        }

        var alias = command.CommandName;
        if (alias.StartsWith("ms_", StringComparison.OrdinalIgnoreCase))
        {
            alias = alias[3..];
        }
        else if (alias.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
        {
            alias = alias[4..];
        }

        if (IsArmorAlias(alias))
        {
            GiveFullArmor(client);
        }
        else if (IsHelpAlias(alias))
        {
            PrintWeaponHelp(client);
        }
        else if (ZombieWeaponCatalog.TryResolve(alias, out var weapon))
        {
            GiveHumanWeapon(client, weapon);
        }
        else
        {
            PrintWeaponHelp(client);
        }

        return ECommandAction.Handled;
    }

    private void GiveHumanWeapon(IGameClient client, ZombieWeapon weapon)
    {
        var player = client.GetPlayerController();
        if (!IsEligible(player))
        {
            return;
        }

        if (player.Team == CStrikeTeam.TE)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 僵尸不能拿枪，输入 !fdy 买满甲。");
            return;
        }

        if (player.Team != CStrikeTeam.CT || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 只有存活的人类阵营可以拿枪。");
            return;
        }

        var key = PlayerKey(player);
        if (!_humanLoadouts.TryGetValue(key, out var loadout))
        {
            loadout = new HumanLoadout();
            _humanLoadouts[key] = loadout;
        }

        if (weapon.Slot == ZombieWeaponSlot.Primary)
        {
            loadout.Primary = weapon;
        }
        else
        {
            loadout.Secondary = weapon;
        }

        pawn.RemoveAllItems(removeSuit: false);
        if (loadout.Primary is not null)
        {
            pawn.GiveNamedItem(loadout.Primary.EntityName);
        }

        if (loadout.Secondary is not null)
        {
            pawn.GiveNamedItem(loadout.Secondary.EntityName);
        }

        pawn.GiveNamedItem("weapon_knife");
        Schedule(() => RefillHumanReserveAmmo(player), 0.05);
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 已发放 {weapon.DisplayName}。");
    }

    private void GiveFullArmor(IGameClient client)
    {
        var player = client.GetPlayerController();
        if (!IsEligible(player) || player.Team is not (CStrikeTeam.CT or CStrikeTeam.TE)
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 进入人类或僵尸阵营并存活后才能买甲。");
            return;
        }

        pawn.ArmorValue = player.Team == CStrikeTeam.TE ? _config.ZombieArmor : _config.HumanArmor;
        if (pawn.GetItemService() is { } items)
        {
            items.HasHelmet = true;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 已补满护甲和头盔。");
    }

    private void RefillHumanReserveAmmo(IPlayerController player)
    {
        if (!_config.InfiniteHumanAmmo || !IsEligible(player) || player.Team != CStrikeTeam.CT
            || player.GetPlayerPawn() is not { IsAlive: true } pawn
            || pawn.GetWeaponService() is not { } weapons)
        {
            return;
        }

        foreach (var handle in weapons.GetMyWeapons())
        {
            var weapon = _entities.FindEntityByHandle(handle);
            if (weapon is not null && weapon.PrimaryReserveAmmoMax > 0)
            {
                weapon.ReserveAmmo = weapon.PrimaryReserveAmmoMax;
            }
        }
    }

    private void PrintWeaponHelp(IGameClient client)
    {
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 主武器：!ak !m4 !a1 !awp !ssg !aug !sg553 !galil !famas !mp9 !mac10 !p90 !negev");
        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 手枪：!de !r8 !glock !usp !p2000 !p250 !fn57 !tec9 !cz，也可用 !fdy 买满甲。");
    }

    private static bool IsHelpAlias(string alias)
        => alias.Trim().ToLowerInvariant() is "gun" or "guns" or "wp" or "weapon" or "weapons" or "枪";

    private static bool IsArmorAlias(string alias)
        => alias.Trim().ToLowerInvariant() is "fdy" or "armor" or "armour" or "kevlar" or "helmet" or "jia" or "甲";

    private static bool IsArmorBuyItem(string value)
        => NormalizeBuyItem(value) is "vest" or "vesthelm" or "kevlar" or "assaultsuit" or "helmet";

    private static string NormalizeBuyItem(string value)
    {
        var normalized = value.Trim().Trim('"').TrimStart('!', '/', '.').ToLowerInvariant();
        if (normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[7..];
        }

        return normalized.StartsWith("item_", StringComparison.OrdinalIgnoreCase) ? normalized[5..] : normalized;
    }
}
