using System.Diagnostics.CodeAnalysis;

namespace SharpGameModes.ZombieInfection;

internal enum ZombieWeaponSlot
{
    Primary,
    Secondary,
}

internal sealed record ZombieWeapon(string Alias, string DisplayName, string EntityName, ZombieWeaponSlot Slot);

internal static class ZombieWeaponCatalog
{
    private static readonly Dictionary<string, ZombieWeapon> Weapons = Build();

    public static IEnumerable<string> Aliases => Weapons.Keys;

    public static bool TryResolve(string alias, [NotNullWhen(true)] out ZombieWeapon? weapon)
        => Weapons.TryGetValue(Normalize(alias), out weapon);

    private static Dictionary<string, ZombieWeapon> Build()
    {
        var catalog = new Dictionary<string, ZombieWeapon>(StringComparer.OrdinalIgnoreCase);
        Add(catalog, new("ak", "AK-47", "weapon_ak47", ZombieWeaponSlot.Primary), "ak47");
        Add(catalog, new("a1", "M4A1-S", "weapon_m4a1_silencer", ZombieWeaponSlot.Primary), "m4", "m4a1", "m4a1s", "m4s");
        Add(catalog, new("a4", "M4A4", "weapon_m4a1", ZombieWeaponSlot.Primary), "m4a4");
        Add(catalog, new("awp", "AWP", "weapon_awp", ZombieWeaponSlot.Primary), "ju", "大狙");
        Add(catalog, new("ssg", "SSG 08", "weapon_ssg08", ZombieWeaponSlot.Primary), "ssg08", "鸟狙", "鸟");
        Add(catalog, new("aug", "AUG", "weapon_aug", ZombieWeaponSlot.Primary));
        Add(catalog, new("sg553", "SG 553", "weapon_sg556", ZombieWeaponSlot.Primary), "sg", "sg556");
        Add(catalog, new("galil", "Galil AR", "weapon_galilar", ZombieWeaponSlot.Primary), "galilar");
        Add(catalog, new("famas", "FAMAS", "weapon_famas", ZombieWeaponSlot.Primary));
        Add(catalog, new("mp9", "MP9", "weapon_mp9", ZombieWeaponSlot.Primary));
        Add(catalog, new("mac10", "MAC-10", "weapon_mac10", ZombieWeaponSlot.Primary), "mac");
        Add(catalog, new("mp7", "MP7", "weapon_mp7", ZombieWeaponSlot.Primary));
        Add(catalog, new("mp5sd", "MP5-SD", "weapon_mp5sd", ZombieWeaponSlot.Primary), "mp5");
        Add(catalog, new("ump45", "UMP-45", "weapon_ump45", ZombieWeaponSlot.Primary), "ump");
        Add(catalog, new("p90", "P90", "weapon_p90", ZombieWeaponSlot.Primary));
        Add(catalog, new("bizon", "PP-Bizon", "weapon_bizon", ZombieWeaponSlot.Primary), "ppbizon");
        Add(catalog, new("nova", "Nova", "weapon_nova", ZombieWeaponSlot.Primary));
        Add(catalog, new("xm1014", "XM1014", "weapon_xm1014", ZombieWeaponSlot.Primary), "xm");
        Add(catalog, new("mag7", "MAG-7", "weapon_mag7", ZombieWeaponSlot.Primary));
        Add(catalog, new("sawedoff", "Sawed-Off", "weapon_sawedoff", ZombieWeaponSlot.Primary));
        Add(catalog, new("m249", "M249", "weapon_m249", ZombieWeaponSlot.Primary));
        Add(catalog, new("negev", "Negev", "weapon_negev", ZombieWeaponSlot.Primary), "内格夫");
        Add(catalog, new("g3sg1", "G3SG1", "weapon_g3sg1", ZombieWeaponSlot.Primary), "g3");
        Add(catalog, new("scar20", "SCAR-20", "weapon_scar20", ZombieWeaponSlot.Primary), "scar");
        Add(catalog, new("de", "Desert Eagle", "weapon_deagle", ZombieWeaponSlot.Secondary), "deagle", "沙鹰");
        Add(catalog, new("r8", "R8 Revolver", "weapon_revolver", ZombieWeaponSlot.Secondary), "revolver");
        Add(catalog, new("glock", "Glock-18", "weapon_glock", ZombieWeaponSlot.Secondary));
        Add(catalog, new("usp", "USP-S", "weapon_usp_silencer", ZombieWeaponSlot.Secondary), "usps", "usp-s");
        Add(catalog, new("p2000", "P2000", "weapon_hkp2000", ZombieWeaponSlot.Secondary));
        Add(catalog, new("p250", "P250", "weapon_p250", ZombieWeaponSlot.Secondary));
        Add(catalog, new("fn57", "Five-SeveN", "weapon_fiveseven", ZombieWeaponSlot.Secondary), "57", "fiveseven");
        Add(catalog, new("tec9", "Tec-9", "weapon_tec9", ZombieWeaponSlot.Secondary), "tec");
        Add(catalog, new("cz", "CZ75-Auto", "weapon_cz75a", ZombieWeaponSlot.Secondary), "cz75");
        Add(catalog, new("elite", "Dual Berettas", "weapon_elite", ZombieWeaponSlot.Secondary), "dual", "duals");
        return catalog;
    }

    private static void Add(Dictionary<string, ZombieWeapon> catalog, ZombieWeapon weapon, params string[] aliases)
    {
        catalog[weapon.Alias] = weapon;
        foreach (var alias in aliases)
        {
            catalog[Normalize(alias)] = weapon;
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().Trim('"').TrimStart('!', '！', '.', '/').ToLowerInvariant();
        if (normalized.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }
        else if (normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized switch
        {
            "m4a1_silencer" or "m4a1s" or "m4s" or "m4a1" or "m4" => "a1",
            "m4a4" => "a4",
            "ssg08" or "鸟狙" or "鸟" => "ssg",
            "ju" or "大狙" => "awp",
            "sg556" or "sg" => "sg553",
            "mac-10" or "mac" => "mac10",
            "mp5" or "mp5-sd" => "mp5sd",
            "ump" or "ump-45" => "ump45",
            "ppbizon" or "pp-bizon" => "bizon",
            "xm" => "xm1014",
            "mag-7" => "mag7",
            "sawed-off" => "sawedoff",
            "内格夫" => "negev",
            "g3" => "g3sg1",
            "scar" => "scar20",
            "deagle" or "沙鹰" => "de",
            "revolver" => "r8",
            "usp-s" or "usps" => "usp",
            "hkp2000" => "p2000",
            "fiveseven" or "five-seven" or "57" => "fn57",
            "tec-9" or "tec" => "tec9",
            "cz75a" or "cz75" or "cz-75" => "cz",
            "dual" or "duals" => "elite",
            _ => normalized,
        };
    }
}
