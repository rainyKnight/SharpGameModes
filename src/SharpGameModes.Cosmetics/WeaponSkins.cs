using System.Globalization;
using Microsoft.Extensions.Logging;
using SharpGameModes.Cosmetics.Storage;
using SharpGameModes.Domain;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.Cosmetics;

public sealed partial class CosmeticsModule
{
    private static readonly Dictionary<string, string> WeaponDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon_deagle"] = "沙漠之鹰",
            ["weapon_elite"] = "双持贝瑞塔",
            ["weapon_fiveseven"] = "Five-SeveN",
            ["weapon_glock"] = "Glock-18",
            ["weapon_ak47"] = "AK-47",
            ["weapon_aug"] = "AUG",
            ["weapon_awp"] = "AWP",
            ["weapon_famas"] = "FAMAS",
            ["weapon_g3sg1"] = "G3SG1",
            ["weapon_galilar"] = "加利尔 AR",
            ["weapon_m249"] = "M249",
            ["weapon_m4a1"] = "M4A4",
            ["weapon_mac10"] = "MAC-10",
            ["weapon_p90"] = "P90",
            ["weapon_mp5sd"] = "MP5-SD",
            ["weapon_ump45"] = "UMP-45",
            ["weapon_xm1014"] = "XM1014",
            ["weapon_bizon"] = "PP-野牛",
            ["weapon_mag7"] = "MAG-7",
            ["weapon_negev"] = "内格夫",
            ["weapon_sawedoff"] = "截短霰弹枪",
            ["weapon_tec9"] = "Tec-9",
            ["weapon_taser"] = "宙斯 x27",
            ["weapon_hkp2000"] = "P2000",
            ["weapon_mp7"] = "MP7",
            ["weapon_mp9"] = "MP9",
            ["weapon_nova"] = "新星",
            ["weapon_p250"] = "P250",
            ["weapon_scar20"] = "SCAR-20",
            ["weapon_sg556"] = "SG 553",
            ["weapon_ssg08"] = "SSG 08",
            ["weapon_m4a1_silencer"] = "M4A1-S",
            ["weapon_usp_silencer"] = "USP-S",
            ["weapon_cz75a"] = "CZ75",
            ["weapon_revolver"] = "R8 左轮手枪",
        };

    private HookReturnValue<IBaseWeapon> OnGiveNamedItemPre(
        IGiveNamedItemHookParams parameters,
        HookReturnValue<IBaseWeapon> current)
    {
        if (!_config.Enabled
            || !_config.KnivesEnabled
            || !IsHuman(parameters.Client)
            || !KnifeCatalog.IsKnifeClassName(parameters.Classname)
            || !IsPlayingTeam(parameters.Controller.Team)
            || !TryGetKnife(parameters.Client.SteamId.AsPrimitive(), parameters.Controller.Team, out var knife)
            || knife.ClassName.Equals(KnifeCatalog.DefaultClassName, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        parameters.SetOverride(knife.ClassName);
        return new HookReturnValue<IBaseWeapon>(EHookAction.ChangeParamReturnDefault);
    }

    private void OnGiveNamedItemPost(
        IGiveNamedItemHookParams parameters,
        HookReturnValue<IBaseWeapon> result)
    {
        if (result.ReturnValue is { } weapon)
        {
            ScheduleWeaponSkin(parameters.Client, parameters.Controller.Team, weapon);
        }
    }

    private void OnPlayerEquipWeapon(IPlayerEquipWeaponForwardParams parameters)
    {
        ScheduleWeaponSkin(parameters.Client, parameters.Controller.Team, parameters.Weapon);
    }

    private void ScheduleWeaponSkin(IGameClient client, CStrikeTeam team, IBaseWeapon weapon)
    {
        _modSharp.InvokeFrameAction(
            () =>
            {
                if (!_stopping && weapon.IsValid())
                {
                    ApplyWeaponSkin(client, team, weapon);
                }
            });
    }

    private void ApplyWeaponSkin(IGameClient client, CStrikeTeam team, IBaseWeapon weapon)
    {
        if (!_config.Enabled
            || !IsHuman(client)
            || !IsPlayingTeam(team)
            || !weapon.IsValid())
        {
            return;
        }

        var steamId = client.SteamId.AsPrimitive();
        var definitionIndex = (int)weapon.ItemDefinitionIndex;
        var key = new WeaponSkinKey(steamId, (int)team, definitionIndex);
        WeaponSkinPreference? preference = null;
        var hasPaint = _config.WeaponSkinsEnabled
            && _skinPreferences.TryGetValue(key, out preference)
            && _skinCatalog.TryGetPaint(definitionIndex, preference.PaintKit, out _);
        var isSelectedKnife = _config.KnivesEnabled
            && weapon.IsKnife
            && TryGetKnife(steamId, team, out var knife)
            && !knife.ClassName.Equals(KnifeCatalog.DefaultClassName, StringComparison.OrdinalIgnoreCase);
        if (!hasPaint && !isSelectedKnife)
        {
            return;
        }

        var paintKit = hasPaint ? preference!.PaintKit : 0;
        var seed = hasPaint
            ? preference!.PaintKit == 38 && preference.Seed == 0 ? _fadeSeed++ : preference.Seed
            : _config.DefaultSeed;
        var wear = hasPaint ? (float)preference!.Wear : (float)_config.DefaultWear;
        var nameTag = hasPaint ? preference!.NameTag : string.Empty;
        var sticker0 = ParseSticker(preference?.Sticker0);
        var sticker1 = ParseSticker(preference?.Sticker1);
        var sticker2 = ParseSticker(preference?.Sticker2);
        var sticker3 = ParseSticker(preference?.Sticker3);
        if (!_entities.UpdateEconItemAttributes(
                weapon,
                client.SteamId.AccountId,
                nameTag,
                paintKit,
                seed,
                wear,
                sticker0.Id,
                sticker0.Wear,
                sticker1.Id,
                sticker1.Wear,
                sticker2.Id,
                sticker2.Wear,
                sticker3.Id,
                sticker3.Wear))
        {
            _logger.LogWarning(
                "ModSharp rejected econ attributes for entity {EntityIndex} and paint kit {PaintKit}.",
                weapon.Index.AsPrimitive(),
                paintKit);
            return;
        }

        weapon.SetModelScale(1f);
        weapon.NetworkStateChanged("m_AttributeManager");
    }

    private static (int Id, float Wear) ParseSticker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        var fields = value.Split(';');
        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return default;
        }

        var wear = fields.Length > 4
            && float.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWear)
                ? parsedWear
                : 0f;
        return (id, wear);
    }

    private void RefreshWeaponSkins(IGameClient client, IPlayerPawn pawn)
    {
        if (!_config.WeaponSkinsEnabled || !IsHuman(client)
            || client.GetPlayerController() is not { } controller)
        {
            return;
        }

        var weaponService = pawn.GetWeaponService();
        if (weaponService is null)
        {
            return;
        }

        foreach (var handle in weaponService.GetMyWeapons())
        {
            if (handle.IsValid() && _entities.FindEntityByHandle<IBaseWeapon>(handle) is { } weapon)
            {
                ScheduleWeaponSkin(client, controller.Team, weapon);
            }
        }
    }

    private bool TryGetKnife(ulong steamId, CStrikeTeam team, out KnifePreference knife)
        => _knifePreferences.TryGetValue(new KnifeKey(steamId, (int)team), out knife!);

    private void ShowWeaponSkinMenu(IGameClient client)
    {
        if (!_config.WeaponSkinsEnabled)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 武器皮肤已关闭。");
            return;
        }

        if (!TryGetPlayingTeam(client, out var team) || !TryGetMenu(client, out var menuManager))
        {
            return;
        }

        var root = new Menu();
        root.SetShowIndex(false);
        root.SetTitle("武器皮肤");
        if (_config.KnivesEnabled)
        {
            root.AddSubMenu("刀型", _ => BuildKnifeMenu(team));
            if (GetSelectedKnifeOption(client.SteamId.AsPrimitive(), team) is { DefinitionIndex: > 0 } selectedKnife
                && _skinCatalog.TryGetWeapon(selectedKnife.DefinitionIndex, out var knifePaints))
            {
                root.AddSubMenu(
                    $"{selectedKnife.DisplayName}皮肤",
                    _ => BuildPaintMenu(team, knifePaints));
            }
        }

        foreach (var group in _skinCatalog.Weapons.Where(
                     group => !KnifeCatalog.Options.Any(knife => knife.DefinitionIndex == group.WeaponDefinitionIndex)))
        {
            root.AddSubMenu(DisplayWeaponName(group.WeaponName), _ => BuildPaintMenu(team, group));
        }

        root.AddExitItem("退出");
        menuManager.DisplayMenu(client, root);
        PrintMenuControls(client);
    }

    private void ShowKnifeMenu(IGameClient client)
    {
        if (!_config.KnivesEnabled)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 刀型选择已关闭。");
            return;
        }

        if (!TryGetPlayingTeam(client, out var team) || !TryGetMenu(client, out var menuManager))
        {
            return;
        }

        menuManager.DisplayMenu(client, BuildKnifeMenu(team));
        PrintMenuControls(client);
    }

    private Menu BuildKnifeMenu(CStrikeTeam team)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle(team == CStrikeTeam.CT ? "选择 CT 刀型" : "选择 T 刀型");
        foreach (var knife in KnifeCatalog.Options)
        {
            menu.AddItem(
                client =>
                {
                    var selected = GetSelectedKnifeOption(client.SteamId.AsPrimitive(), team);
                    return selected.ClassName.Equals(knife.ClassName, StringComparison.OrdinalIgnoreCase)
                        ? $"[已选] {knife.DisplayName}"
                        : knife.DisplayName;
                },
                controller =>
                {
                    SaveKnife(controller.Client, team, knife);
                    controller.Refresh();
                });
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private Menu BuildPaintMenu(CStrikeTeam team, WeaponPaintGroup group)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle(DisplayWeaponName(group.WeaponName));
        foreach (var paint in group.Paints)
        {
            menu.AddItem(
                client =>
                {
                    var key = new WeaponSkinKey(
                        client.SteamId.AsPrimitive(),
                        (int)team,
                        group.WeaponDefinitionIndex);
                    var selected = _skinPreferences.TryGetValue(key, out var preference)
                        ? preference.PaintKit
                        : 0;
                    return selected == paint.PaintKit
                        ? $"[已选] {paint.PaintName}"
                        : paint.PaintName;
                },
                controller =>
                {
                    SaveWeaponPaint(controller.Client, team, paint);
                    controller.Refresh();
                });
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private void SaveWeaponPaint(IGameClient client, CStrikeTeam team, WeaponPaintOption paint)
    {
        if (!IsHuman(client) || !IsPlayingTeam(team))
        {
            return;
        }

        var key = new WeaponSkinKey(client.SteamId.AsPrimitive(), (int)team, paint.WeaponDefinitionIndex);
        if (paint.PaintKit == 0)
        {
            _repository.DeleteWeaponSkin(key);
            _skinPreferences.Remove(key);
        }
        else
        {
            var existing = _skinPreferences.GetValueOrDefault(key);
            var preference = new WeaponSkinPreference(
                key.SteamId,
                key.Team,
                key.WeaponDefinitionIndex,
                paint.PaintKit,
                existing?.Wear ?? _config.DefaultWear,
                existing?.Seed ?? _config.DefaultSeed,
                existing?.NameTag ?? string.Empty,
                existing?.StatTrak ?? false,
                existing?.StatTrakCount ?? 0,
                existing?.Sticker0 ?? string.Empty,
                existing?.Sticker1 ?? string.Empty,
                existing?.Sticker2 ?? string.Empty,
                existing?.Sticker3 ?? string.Empty,
                existing?.Sticker4 ?? string.Empty,
                existing?.Keychain ?? string.Empty);
            _repository.UpsertWeaponSkin(preference);
            _skinPreferences[key] = preference;
        }

        if (client.GetPlayerController()?.GetPlayerPawn() is { IsAlive: true } pawn)
        {
            RefreshWeaponSkins(client, pawn);
            client.ForceFullUpdate();
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 武器皮肤已保存。");
    }

    private void SaveKnife(IGameClient client, CStrikeTeam team, KnifeOption knife)
    {
        if (!IsHuman(client) || !IsPlayingTeam(team))
        {
            return;
        }

        var key = new KnifeKey(client.SteamId.AsPrimitive(), (int)team);
        if (knife.ClassName.Equals(KnifeCatalog.DefaultClassName, StringComparison.OrdinalIgnoreCase))
        {
            _repository.DeleteKnife(key);
            _knifePreferences.Remove(key);
        }
        else
        {
            var preference = new KnifePreference(key.SteamId, key.Team, knife.ClassName);
            _repository.UpsertKnife(preference);
            _knifePreferences[key] = preference;
        }

        if (client.GetPlayerController() is { } player
            && player.Team == team
            && player.GetPlayerPawn() is { IsAlive: true } pawn)
        {
            ReplaceKnife(pawn);
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 刀型已保存。");
    }

    private void ReplaceKnife(IPlayerPawn pawn)
    {
        var weaponService = pawn.GetWeaponService();
        if (weaponService is null)
        {
            return;
        }

        var knives = new List<IBaseWeapon>();
        foreach (var handle in weaponService.GetMyWeapons())
        {
            if (handle.IsValid()
                && _entities.FindEntityByHandle<IBaseWeapon>(handle) is { IsKnife: true } weapon)
            {
                knives.Add(weapon);
            }
        }

        foreach (var knife in knives)
        {
            knife.Kill();
        }

        _modSharp.InvokeFrameAction(
            () =>
            {
                if (!_stopping && pawn.IsValid() && pawn.IsAlive)
                {
                    pawn.GiveNamedItem(KnifeCatalog.DefaultClassName);
                }
            });
    }

    private KnifeOption GetSelectedKnifeOption(ulong steamId, CStrikeTeam team)
        => TryGetKnife(steamId, team, out var preference)
            && KnifeCatalog.TryGet(preference.ClassName, out var knife)
                ? knife
                : KnifeCatalog.Options[0];

    private bool TryGetPlayingTeam(IGameClient client, out CStrikeTeam team)
    {
        team = client.GetPlayerController()?.Team ?? CStrikeTeam.UnAssigned;
        if (IsPlayingTeam(team))
        {
            return true;
        }

        client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 加入 T 或 CT 后再设置外观。");
        return false;
    }

    private static string DisplayWeaponName(string weaponName)
        => WeaponDisplayNames.GetValueOrDefault(weaponName, weaponName.Replace("weapon_", string.Empty));
}
