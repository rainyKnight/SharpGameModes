using System.Collections.Frozen;
using System.Text.Json;

namespace SharpGameModes.Domain;

public sealed record WeaponPaintOption(
    int WeaponDefinitionIndex,
    string WeaponName,
    int PaintKit,
    string PaintName,
    bool LegacyModel);

public sealed record WeaponPaintGroup(
    int WeaponDefinitionIndex,
    string WeaponName,
    IReadOnlyList<WeaponPaintOption> Paints);

public sealed class WeaponSkinCatalog
{
    private readonly FrozenDictionary<int, WeaponPaintGroup> _byWeapon;

    private WeaponSkinCatalog(FrozenDictionary<int, WeaponPaintGroup> byWeapon)
    {
        _byWeapon = byWeapon;
        Weapons = byWeapon.Values
            .OrderBy(static group => group.WeaponName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<WeaponPaintGroup> Weapons { get; }

    public static WeaponSkinCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Weapon skin catalog root must be an array.");
        }

        var options = new Dictionary<(int Weapon, int Paint), WeaponPaintOption>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!TryGetInt(element, "weapon_defindex", out var weaponIndex)
                || !TryGetInt(element, "paint", out var paintKit)
                || weaponIndex <= 0
                || paintKit < 0)
            {
                continue;
            }

            var weaponName = GetString(element, "weapon_name");
            var paintName = GetString(element, "paint_name");
            if (string.IsNullOrWhiteSpace(weaponName) || string.IsNullOrWhiteSpace(paintName))
            {
                continue;
            }

            var legacyModel = element.TryGetProperty("legacy_model", out var legacyElement)
                && legacyElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && legacyElement.GetBoolean();
            options.TryAdd(
                (weaponIndex, paintKit),
                new WeaponPaintOption(weaponIndex, weaponName, paintKit, paintName, legacyModel));
        }

        if (options.Count == 0)
        {
            throw new InvalidDataException("Weapon skin catalog does not contain any valid entries.");
        }

        var groups = options.Values
            .GroupBy(static option => option.WeaponDefinitionIndex)
            .Select(
                static group =>
                {
                    var paints = group
                        .OrderBy(static option => option.PaintKit == 0 ? 0 : 1)
                        .ThenBy(static option => option.PaintName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new WeaponPaintGroup(group.Key, paints[0].WeaponName, paints);
                })
            .ToFrozenDictionary(static group => group.WeaponDefinitionIndex);
        return new WeaponSkinCatalog(groups);
    }

    public bool TryGetWeapon(int weaponDefinitionIndex, out WeaponPaintGroup group)
        => _byWeapon.TryGetValue(weaponDefinitionIndex, out group!);

    public bool TryGetPaint(int weaponDefinitionIndex, int paintKit, out WeaponPaintOption option)
    {
        option = null!;
        if (!_byWeapon.TryGetValue(weaponDefinitionIndex, out var group))
        {
            return false;
        }

        option = group.Paints.FirstOrDefault(paint => paint.PaintKit == paintKit)!;
        return option is not null;
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var propertyElement))
        {
            return false;
        }

        return propertyElement.ValueKind switch
        {
            JsonValueKind.Number => propertyElement.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(propertyElement.GetString(), out value),
            _ => false,
        };
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString() ?? string.Empty
                : string.Empty;
}
