using System.Text.Json;
using SharpGameModes.Domain;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.PlayerModels;

public sealed partial class PlayerModelsModule
{
    private void InstallCommands()
    {
        foreach (var command in ModelCommands
                     .Concat(MenuCommands)
                     .Concat(MeshCommands)
                     .Concat(SkinCommands)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _clients.InstallCommandCallback(command, OnPlayerModelCommand);
            _registeredCommands.Add(command);
        }
    }

    private void RemoveCommands()
    {
        foreach (var command in _registeredCommands)
        {
            _clients.RemoveCommandCallback(command, OnPlayerModelCommand);
        }

        _registeredCommands.Clear();
    }

    private ECommandAction OnPlayerModelCommand(IGameClient client, StringCommand command)
    {
        if (!IsHuman(client))
        {
            return ECommandAction.Skipped;
        }

        if (!IsAvailableForClient(client))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前阵营的人物模型由模式模块接管。");
            return ECommandAction.Handled;
        }

        if (_config.DisablePlayerSelection)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 玩家模型选择已关闭。");
            return ECommandAction.Handled;
        }

        if (!HasBasicPermission(client))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 你没有使用人物模型的权限。");
            return ECommandAction.Handled;
        }

        if (!PreferencesReady(client, out _))
        {
            return ECommandAction.Handled;
        }

        var alias = NormalizeCommand(command.CommandName);
        if (MeshCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            ShowMeshMenu(client);
        }
        else if (SkinCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            ShowSkinMenu(client);
        }
        else if (MenuCommands.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            ShowRequestedModelMenu(client, command);
        }
        else
        {
            HandleModelCommand(client, command);
        }

        return ECommandAction.Handled;
    }

    private void HandleModelCommand(IGameClient client, StringCommand command)
    {
        if (command.ArgCount == 0)
        {
            ShowSideMenu(client);
            return;
        }

        var requested = command.GetArg(1).Trim();
        requested = requested.ToLowerInvariant() switch
        {
            "none" or "unset" or "off" => string.Empty,
            "random" => "@random",
            "default" => "@default",
            _ => requested,
        };
        if (requested is not ("" or "@random" or "@default")
            && !_config.Models.ContainsKey(requested))
        {
            var byName = _config.Models.Values.FirstOrDefault(
                model => model.DisplayName.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (byName is null)
            {
                client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 找不到模型“{requested}”。");
                return;
            }

            requested = byName.Index;
        }

        var side = PlayerModelSide.All;
        if (command.ArgCount >= 2 && !TryParseSide(command.GetArg(2), out side))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 阵营只能是 all、t 或 ct。");
            return;
        }

        SaveSelection(client, side, requested, showMessage: true);
    }

    private void ShowRequestedModelMenu(IGameClient client, StringCommand command)
    {
        if (command.ArgCount == 0)
        {
            ShowSideMenu(client);
            return;
        }

        if (!TryParseSide(command.GetArg(1), out var side))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 阵营只能是 all、t 或 ct。");
            return;
        }

        if (IsForced(client, side))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 该阵营使用服务器强制模型。");
            return;
        }

        if (TryGetMenu(client, out var menus))
        {
            menus.DisplayMenu(client, BuildModelMenu(side));
            PrintControls(client);
        }
    }

    private void ShowSideMenu(IGameClient client)
    {
        if (!TryGetMenu(client, out var menus))
        {
            return;
        }

        var tForced = GetDefaultRule(client, PlayerModelSide.T)?.Force == true;
        var ctForced = GetDefaultRule(client, PlayerModelSide.CT)?.Force == true;
        if (tForced && ctForced)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前模型由服务器强制设置。");
            return;
        }

        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle("人物模型");
        if (!tForced && !ctForced)
        {
            menu.AddSubMenu(
                player => $"全部阵营: {SelectionLabel(player, PlayerModelSide.T)} / {SelectionLabel(player, PlayerModelSide.CT)}",
                _ => BuildModelMenu(PlayerModelSide.All));
        }

        if (!tForced)
        {
            menu.AddSubMenu(
                player => $"T 阵营: {SelectionLabel(player, PlayerModelSide.T)}",
                _ => BuildModelMenu(PlayerModelSide.T));
        }

        if (!ctForced)
        {
            menu.AddSubMenu(
                player => $"CT 阵营: {SelectionLabel(player, PlayerModelSide.CT)}",
                _ => BuildModelMenu(PlayerModelSide.CT));
        }

        menu.AddExitItem("退出");
        menus.DisplayMenu(client, menu);
        PrintControls(client);
    }

    private Menu BuildModelMenu(PlayerModelSide side)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle(player => $"{SideLabel(side)}模型");

        AddSelectionItem(menu, side, string.Empty, "不使用自定义模型");
        if (!_config.DisableRandomModel)
        {
            AddSelectionItem(menu, side, "@random", "每回合随机");
        }

        AddSelectionItem(menu, side, "@default", "服务器默认");
        foreach (var model in _config.Models.Values.Where(
                     model => !model.HideInMenu && model.Supports(side)))
        {
            menu.AddItem(
                player =>
                {
                    if (!CanUseModel(player, model))
                    {
                        return $"{model.DisplayName} [无权限]";
                    }

                    return IsSelected(player, side, model.Index)
                        ? $"[已选] {model.DisplayName}"
                        : model.DisplayName;
                },
                controller =>
                {
                    if (!CanUseModel(controller.Client, model))
                    {
                        controller.Client.Print(
                            HudPrintChannel.Chat,
                            $"{_config.Prefix} 你没有使用该模型的权限。");
                        controller.Refresh();
                        return;
                    }

                    if (IsSelected(controller.Client, side, model.Index)
                        && (model.MeshGroups.Count > 0 || model.Skins.Count > 0)
                        && IsCurrentAppliedModel(controller.Client, model.Index))
                    {
                        controller.Next(BuildCustomizationMenu(model));
                        return;
                    }

                    SaveSelection(controller.Client, side, model.Index, showMessage: true);
                    controller.Refresh();
                });
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private void AddSelectionItem(Menu menu, PlayerModelSide side, string selection, string label)
    {
        menu.AddItem(
            player => IsSelected(player, side, selection) ? $"[已选] {label}" : label,
            controller =>
            {
                SaveSelection(controller.Client, side, selection, showMessage: true);
                controller.Refresh();
            });
    }

    private bool SaveSelection(
        IGameClient client,
        PlayerModelSide side,
        string selection,
        bool showMessage)
    {
        if (IsForced(client, side))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 该阵营使用服务器强制模型。");
            return false;
        }

        if (selection != "@default"
            && _config.ModelChangeCooldownSecond > 0
            && _cooldowns.TryGetValue(client.SteamId.AsPrimitive(), out var nextAllowed)
            && nextAllowed > DateTimeOffset.UtcNow)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((nextAllowed - DateTimeOffset.UtcNow).TotalSeconds));
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 请等待 {seconds} 秒后再更换模型。");
            return false;
        }

        if (selection == "@random" && _config.DisableRandomModel)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 随机模型已关闭。");
            return false;
        }

        if (selection is not ("" or "@random" or "@default")
            && (!_config.Models.TryGetValue(selection, out var model)
                || !model.Supports(side)
                || !CanUseModel(client, model)))
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 该模型不能用于所选阵营。");
            return false;
        }

        SetSelection(client, side, selection);
        if (selection != "@default" && _config.ModelChangeCooldownSecond > 0)
        {
            _cooldowns[client.SteamId.AsPrimitive()] =
                DateTimeOffset.UtcNow.AddSeconds(_config.ModelChangeCooldownSecond);
        }

        if (!_config.DisableInstantChange
            && client.GetPlayerController() is { } controller
            && controller.GetPlayerPawn() is { IsAlive: true }
            && SideIncludesTeam(side, controller.Team))
        {
            ReapplyCurrentModel(client);
        }

        if (showMessage)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} {SideLabel(side)}模型已保存。");
        }

        return true;
    }

    private bool IsSelected(IGameClient client, PlayerModelSide side, string selection)
        => side switch
        {
            PlayerModelSide.All => GetSelection(client, PlayerModelSide.T) == selection
                && GetSelection(client, PlayerModelSide.CT) == selection,
            PlayerModelSide.T => GetSelection(client, PlayerModelSide.T) == selection,
            PlayerModelSide.CT => GetSelection(client, PlayerModelSide.CT) == selection,
            _ => false,
        };

    private bool IsForced(IGameClient client, PlayerModelSide side)
        => side switch
        {
            PlayerModelSide.All => GetDefaultRule(client, PlayerModelSide.T)?.Force == true
                || GetDefaultRule(client, PlayerModelSide.CT)?.Force == true,
            PlayerModelSide.T => GetDefaultRule(client, PlayerModelSide.T)?.Force == true,
            PlayerModelSide.CT => GetDefaultRule(client, PlayerModelSide.CT)?.Force == true,
            _ => false,
        };

    private static bool SideIncludesTeam(PlayerModelSide side, CStrikeTeam team)
        => side == PlayerModelSide.All
            || side == PlayerModelSide.T && team == CStrikeTeam.TE
            || side == PlayerModelSide.CT && team == CStrikeTeam.CT;

    private bool IsCurrentAppliedModel(IGameClient client, string modelIndex)
        => client.GetPlayerController() is { } controller
            && _appliedModels.TryGetValue(
                (client.SteamId.AsPrimitive(), controller.Team),
                out var current)
            && current.Equals(modelIndex, StringComparison.OrdinalIgnoreCase);

    private void ShowMeshMenu(IGameClient client)
    {
        if (!TryGetAppliedModel(client, out _, out var model) || model.MeshGroups.Count == 0)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前模型没有可调网格组。");
            return;
        }

        if (TryGetMenu(client, out var menus))
        {
            menus.DisplayMenu(client, BuildMeshMenu(model));
            PrintControls(client);
        }
    }

    private void ShowSkinMenu(IGameClient client)
    {
        if (!TryGetAppliedModel(client, out _, out var model) || model.Skins.Count == 0)
        {
            client.Print(HudPrintChannel.Chat, $"{_config.Prefix} 当前模型没有可调材质组。");
            return;
        }

        if (TryGetMenu(client, out var menus))
        {
            menus.DisplayMenu(client, BuildSkinMenu(model));
            PrintControls(client);
        }
    }

    private Menu BuildCustomizationMenu(PlayerModelDefinition model)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle(model.DisplayName);
        if (model.MeshGroups.Count > 0)
        {
            menu.AddSubMenu("网格组", BuildMeshMenu(model));
        }

        if (model.Skins.Count > 0)
        {
            menu.AddSubMenu("材质组", BuildSkinMenu(model));
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private Menu BuildMeshMenu(PlayerModelDefinition model)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle($"{model.DisplayName} - 网格组");
        foreach (var (rawName, element) in model.MeshGroups)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var group = ParseMeshGroup(rawName, element);
            menu.AddSubMenu(group.Name, _ => BuildMeshChoiceMenu(model, group));
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private Menu BuildMeshChoiceMenu(PlayerModelDefinition model, MeshGroup group)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle(group.Name);
        foreach (var choice in group.Choices)
        {
            menu.AddItem(
                client => IsMeshChoiceSelected(client, model, choice)
                    ? $"[已选] {choice.Name}"
                    : choice.Name,
                controller =>
                {
                    var selected = GetMeshGroups(controller.Client, model.Index).ToHashSet();
                    var currentlySelected = choice.Values.All(selected.Contains);
                    if (group.Radio || group.OptionalRadio)
                    {
                        foreach (var value in group.Choices.SelectMany(item => item.Values))
                        {
                            selected.Remove(value);
                        }

                        if (!group.OptionalRadio || !currentlySelected)
                        {
                            selected.UnionWith(choice.Values);
                        }
                    }
                    else if (currentlySelected)
                    {
                        selected.ExceptWith(choice.Values);
                    }
                    else
                    {
                        selected.UnionWith(choice.Values);
                    }

                    SetMeshGroups(controller.Client, model.Index, selected);
                    ReapplyCurrentModel(controller.Client);
                    controller.Refresh();
                });
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private Menu BuildSkinMenu(PlayerModelDefinition model)
    {
        var menu = new Menu();
        menu.SetShowIndex(false);
        menu.SetTitle($"{model.DisplayName} - 材质组");
        foreach (var (name, skin) in model.Skins)
        {
            menu.AddItem(
                client => GetSkin(client, model.Index) == skin ? $"[已选] {name}" : name,
                controller =>
                {
                    SetSkin(controller.Client, model.Index, skin);
                    ReapplyCurrentModel(controller.Client);
                    controller.Refresh();
                });
        }

        menu.AddBackItem("返回");
        return menu;
    }

    private bool IsMeshChoiceSelected(
        IGameClient client,
        PlayerModelDefinition model,
        MeshChoice choice)
    {
        var selected = GetMeshGroups(client, model.Index).ToHashSet();
        return choice.Values.Length > 0 && choice.Values.All(selected.Contains);
    }

    private static MeshGroup ParseMeshGroup(string rawName, JsonElement element)
    {
        var radio = rawName.Contains("@radio", StringComparison.OrdinalIgnoreCase);
        var optionalRadio = rawName.Contains("@opradio", StringComparison.OrdinalIgnoreCase);
        var name = rawName
            .Replace("@combination", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("@opradio", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("@radio", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        var choices = new List<MeshChoice>();
        foreach (var property in element.EnumerateObject())
        {
            int[] values;
            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var single))
            {
                values = [single];
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                values = property.Value.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.Number)
                    .Select(value => value.GetInt32())
                    .ToArray();
            }
            else
            {
                continue;
            }

            choices.Add(new MeshChoice(property.Name, values));
        }

        return new MeshGroup(name, radio, optionalRadio, choices);
    }

    private static string NormalizeCommand(string command)
    {
        if (command.StartsWith("ms_", StringComparison.OrdinalIgnoreCase))
        {
            return command[3..];
        }

        return command.StartsWith("css_", StringComparison.OrdinalIgnoreCase)
            ? command[4..]
            : command;
    }

    private sealed record MeshChoice(string Name, int[] Values);
    private sealed record MeshGroup(
        string Name,
        bool Radio,
        bool OptionalRadio,
        IReadOnlyList<MeshChoice> Choices);
}
