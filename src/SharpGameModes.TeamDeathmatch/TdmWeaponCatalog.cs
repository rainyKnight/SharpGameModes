using System.Diagnostics.CodeAnalysis;

namespace SharpGameModes.TeamDeathmatch;

internal enum TdmWeaponSlot
{
    Primary,
    Secondary,
}

internal sealed record TdmWeapon(string Alias, string DisplayName, string EntityName, TdmWeaponSlot Slot);

internal static class TdmWeaponCatalog
{
    private static readonly Dictionary<string, TdmWeapon> Weapons = Build();

    public static IReadOnlyCollection<string> Aliases => Weapons.Keys;

    public static bool TryResolve(string alias, [NotNullWhen(true)] out TdmWeapon? weapon)
        => Weapons.TryGetValue(NormalizeAlias(alias), out weapon);

    public static string NormalizeEntityName(string item)
    {
        var normalized = NormalizeAlias(item);
        return Weapons.TryGetValue(normalized, out var weapon)
            ? weapon.EntityName
            : normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : $"weapon_{normalized}";
    }

    private static Dictionary<string, TdmWeapon> Build()
    {
        var catalog = new Dictionary<string, TdmWeapon>(StringComparer.OrdinalIgnoreCase);
        Add(catalog, new("ak", "AK-47", "weapon_ak47", TdmWeaponSlot.Primary), "ak47");
        Add(catalog, new("a1", "M4A1-S", "weapon_m4a1_silencer", TdmWeaponSlot.Primary), "m4a1s", "m4a1", "m4s");
        Add(catalog, new("a4", "M4A4", "weapon_m4a1", TdmWeaponSlot.Primary), "m4", "m4a4");
        Add(catalog, new("ssg", "SSG 08", "weapon_ssg08", TdmWeaponSlot.Primary), "ssg08", "鸟狙", "鸟");
        Add(catalog, new("awp", "AWP", "weapon_awp", TdmWeaponSlot.Primary), "大狙");
        Add(catalog, new("negev", "Negev", "weapon_negev", TdmWeaponSlot.Primary), "内格夫");
        Add(catalog, new("aug", "AUG", "weapon_aug", TdmWeaponSlot.Primary));
        Add(catalog, new("sg553", "SG 553", "weapon_sg556", TdmWeaponSlot.Primary), "sg", "sg556");
        Add(catalog, new("mp9", "MP9", "weapon_mp9", TdmWeaponSlot.Primary));
        Add(catalog, new("mp7", "MP7", "weapon_mp7", TdmWeaponSlot.Primary));
        Add(catalog, new("mac10", "MAC-10", "weapon_mac10", TdmWeaponSlot.Primary), "mac");
        Add(catalog, new("de", "Desert Eagle", "weapon_deagle", TdmWeaponSlot.Secondary), "deagle", "沙鹰");
        Add(catalog, new("fn57", "Five-SeveN", "weapon_fiveseven", TdmWeaponSlot.Secondary), "57", "fiveseven");
        Add(catalog, new("tec9", "Tec-9", "weapon_tec9", TdmWeaponSlot.Secondary), "tec");
        return catalog;
    }

    private static void Add(Dictionary<string, TdmWeapon> catalog, TdmWeapon weapon, params string[] aliases)
    {
        catalog[weapon.Alias] = weapon;
        foreach (var alias in aliases)
        {
            catalog[NormalizeAlias(alias)] = weapon;
        }
    }

    private static string NormalizeAlias(string alias)
        => alias.Trim().Trim('"').ToLowerInvariant() switch
        {
            "weapon_ak47" or "ak47" => "ak",
            "weapon_m4a1_silencer" or "m4a1_silencer" or "m4a1s" or "a1" => "a1",
            "weapon_m4a1" or "m4" or "m4a4" or "a4" => "a4",
            "weapon_ssg08" or "ssg08" or "ssg" or "鸟狙" or "鸟" => "ssg",
            "weapon_awp" or "awp" or "ju" or "大狙" => "awp",
            "weapon_deagle" or "deagle" or "沙鹰" => "de",
            "weapon_fiveseven" or "fiveseven" or "five-seven" => "fn57",
            "weapon_tec9" or "tec-9" => "tec9",
            "weapon_negev" or "negev" or "内格夫" => "negev",
            "weapon_aug" => "aug",
            "weapon_sg556" or "sg556" or "sg" => "sg553",
            "weapon_mp9" => "mp9",
            "weapon_mp7" => "mp7",
            "weapon_mac10" or "mac-10" => "mac10",
            var value => value,
        };
}
