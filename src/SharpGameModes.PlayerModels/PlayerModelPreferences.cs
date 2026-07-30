using System.Text.Json;
using SharpGameModes.Domain;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;

namespace SharpGameModes.PlayerModels;

public sealed partial class PlayerModelsModule
{
    private const string TModelCookie = "sharp-gamemodes.pmc.t_model";
    private const string CTModelCookie = "sharp-gamemodes.pmc.ct_model";
    private const string MeshCookiePrefix = "sharp-gamemodes.pmc.meshgroups.";
    private const string SkinCookiePrefix = "sharp-gamemodes.pmc.skin.";

    private string GetSelection(IGameClient client, PlayerModelSide side)
    {
        var preferences = _preferences?.Instance;
        if (preferences is null || !preferences.IsLoaded(client.SteamId))
        {
            return "@default";
        }

        var key = side == PlayerModelSide.T ? TModelCookie : CTModelCookie;
        return TryGetString(preferences, client, key) ?? "@default";
    }

    private void SetSelection(IGameClient client, PlayerModelSide side, string selection)
    {
        var preferences = _preferences?.Instance
            ?? throw new InvalidOperationException("ClientPreferences is unavailable.");
        if (side is PlayerModelSide.All or PlayerModelSide.T)
        {
            preferences.SetCookie(client.SteamId, TModelCookie, selection);
        }

        if (side is PlayerModelSide.All or PlayerModelSide.CT)
        {
            preferences.SetCookie(client.SteamId, CTModelCookie, selection);
        }
    }

    private int[] GetMeshGroups(IGameClient client, string modelIndex)
    {
        var preferences = _preferences?.Instance;
        if (preferences is null || !preferences.IsLoaded(client.SteamId))
        {
            return [];
        }

        var raw = TryGetString(preferences, client, MeshCookiePrefix + modelIndex);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<int[]>(raw)
                ?.Where(value => value is >= 0 and <= 63)
                .Distinct()
                .Order()
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool HasMeshGroupCookie(IGameClient client, string modelIndex)
        => _preferences?.Instance is { } preferences
            && preferences.IsLoaded(client.SteamId)
            && preferences.GetCookie(client.SteamId, MeshCookiePrefix + modelIndex) is not null;

    private void SetMeshGroups(IGameClient client, string modelIndex, IEnumerable<int> groups)
    {
        var values = groups.Where(value => value is >= 0 and <= 63).Distinct().Order().ToArray();
        _preferences?.Instance?.SetCookie(
            client.SteamId,
            MeshCookiePrefix + modelIndex,
            JsonSerializer.Serialize(values));
    }

    private int GetSkin(IGameClient client, string modelIndex)
    {
        var preferences = _preferences?.Instance;
        if (preferences is null || !preferences.IsLoaded(client.SteamId))
        {
            return 0;
        }

        try
        {
            return preferences.GetCookie(client.SteamId, SkinCookiePrefix + modelIndex)?.GetNumber() is { } value
                ? checked((int)value)
                : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    private void SetSkin(IGameClient client, string modelIndex, int skin)
        => _preferences?.Instance?.SetCookie(client.SteamId, SkinCookiePrefix + modelIndex, skin);

    private static string? TryGetString(
        IClientPreference preferences,
        IGameClient client,
        string key)
    {
        try
        {
            return preferences.GetCookie(client.SteamId, key)?.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ValidateSelections(IGameClient client)
    {
        if (_preferences?.Instance is not { } preferences || !preferences.IsLoaded(client.SteamId))
        {
            return;
        }

        foreach (var side in new[] { PlayerModelSide.T, PlayerModelSide.CT })
        {
            var selection = GetSelection(client, side);
            var defaultRule = GetDefaultRule(client, side);
            if (defaultRule?.Force == true)
            {
                if (selection != "@default")
                {
                    SetSelection(client, side, "@default");
                }

                continue;
            }

            if (_config.DisableAutoCheck)
            {
                continue;
            }

            var valid = selection switch
            {
                "" or "@default" => true,
                "@random" => !_config.DisableRandomModel,
                _ => _config.Models.TryGetValue(selection, out var model)
                    && model.Supports(side)
                    && CanUseModel(client, model),
            };
            if (!valid)
            {
                SetSelection(client, side, "@default");
                client.Print(
                    HudPrintChannel.Chat,
                    $"{_config.Prefix} {(side == PlayerModelSide.T ? "T" : "CT")} 模型配置失效，已恢复默认。");
            }
        }
    }

    private PlayerModelDefaultRule? GetDefaultRule(IGameClient client, PlayerModelSide side)
    {
        var candidates = new Dictionary<string, PlayerModelDefaultRule>(
            _defaults.DefaultModels.All,
            StringComparer.OrdinalIgnoreCase);
        var sideRules = side == PlayerModelSide.T
            ? _defaults.DefaultModels.T
            : _defaults.DefaultModels.CT;
        foreach (var (key, rule) in sideRules)
        {
            candidates[key] = rule;
        }

        var steamId = client.SteamId.AsPrimitive().ToString();
        if (candidates.TryGetValue(steamId, out var exact))
        {
            return exact;
        }

        foreach (var (key, rule) in candidates)
        {
            if (key.StartsWith('@') && HasPermission(client, key[1..]))
            {
                return rule;
            }
        }

        foreach (var (key, rule) in candidates)
        {
            if (key.StartsWith('#') && HasPermission(client, key[1..]))
            {
                return rule;
            }
        }

        return candidates.GetValueOrDefault("*");
    }

    private bool HasBasicPermission(IGameClient client)
        => string.IsNullOrWhiteSpace(_config.BasicPermission)
            || HasPermission(client, _config.BasicPermission.TrimStart('@', '#'));

    private bool CanUseModel(IGameClient client, PlayerModelDefinition model)
    {
        foreach (var permission in model.Permissions)
        {
            if (!MatchesPermissionEntry(client, permission))
            {
                return false;
            }
        }

        return model.PermissionsOr.Length == 0
            || model.PermissionsOr.Any(permission => MatchesPermissionEntry(client, permission));
    }

    private bool MatchesPermissionEntry(IGameClient client, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        if (entry[0] is '@' or '#')
        {
            return HasPermission(client, entry[1..]);
        }

        return ulong.TryParse(entry, out var steamId)
            && client.SteamId.AsPrimitive() == steamId;
    }

    private bool HasPermission(IGameClient client, string permission)
        => !string.IsNullOrWhiteSpace(permission)
            && _admins?.Instance?.GetAdmin(client.SteamId)?.HasPermission(permission) == true;

    private PlayerModelDefinition? ResolveSelection(
        IGameClient client,
        PlayerModelSide side,
        string selection)
    {
        if (selection == "@default")
        {
            var rule = GetDefaultRule(client, side);
            if (rule is null || rule.Index.Length == 0)
            {
                return null;
            }

            selection = rule.Index[Random.Shared.Next(rule.Index.Length)];
        }

        if (string.IsNullOrEmpty(selection))
        {
            return null;
        }

        if (selection == "@random")
        {
            var models = _config.Models.Values
                .Where(model => model.Supports(side) && CanUseModel(client, model))
                .ToArray();
            return models.Length == 0 ? null : models[Random.Shared.Next(models.Length)];
        }

        return _config.Models.TryGetValue(selection, out var model)
            && model.Supports(side)
            && CanUseModel(client, model)
                ? model
                : null;
    }

    private string SelectionLabel(IGameClient client, PlayerModelSide side)
    {
        var selection = GetSelection(client, side);
        return selection switch
        {
            "" => "未设置",
            "@random" => "每回合随机",
            "@default" => "服务器默认",
            _ => _config.Models.TryGetValue(selection, out var model)
                ? model.DisplayName
                : "服务器默认",
        };
    }

    private static string SideLabel(PlayerModelSide side)
        => side switch
        {
            PlayerModelSide.All => "全部阵营",
            PlayerModelSide.T => "T 阵营",
            PlayerModelSide.CT => "CT 阵营",
            _ => throw new ArgumentOutOfRangeException(nameof(side)),
        };

    private static bool TryParseSide(string value, out PlayerModelSide side)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "all":
            case "a":
            case "全部":
                side = PlayerModelSide.All;
                return true;
            case "t":
            case "te":
                side = PlayerModelSide.T;
                return true;
            case "ct":
                side = PlayerModelSide.CT;
                return true;
            default:
                side = default;
                return false;
        }
    }
}
