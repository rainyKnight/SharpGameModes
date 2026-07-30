namespace SharpGameModes.BotMatch;

// Decision tables ported from ed0ard/CS2-Bot-Improver BotBuy 1.0.12,
// commit af1639598b1d7ba64d4850a36f4c819500f3b8ea. The upstream license
// text is distributed at config/sharp/configs/sharp-gamemodes/botmatch-cosmetics/
// UPSTREAM-LICENSE.
public enum BotBuyTeam
{
    None = 0,
    Terrorist = 2,
    CounterTerrorist = 3,
}

internal static class BotBuyPolicy
{
    private static readonly Dictionary<string, ItemRule> ItemRules =
        new(StringComparer.Ordinal)
        {
            ["item_kevlar"] = new(650),
            ["item_assaultsuit"] = new(1000),
            ["item_defuser"] = new(400, BotBuyTeam.CounterTerrorist),
            ["weapon_taser"] = new(200),
            ["weapon_glock"] = new(0, BotBuyTeam.Terrorist),
            ["weapon_hkp2000"] = new(0, BotBuyTeam.CounterTerrorist),
            ["weapon_usp_silencer"] = new(0, BotBuyTeam.CounterTerrorist),
            ["weapon_elite"] = new(300),
            ["weapon_p250"] = new(300),
            ["weapon_tec9"] = new(500, BotBuyTeam.Terrorist),
            ["weapon_fiveseven"] = new(500, BotBuyTeam.CounterTerrorist),
            ["weapon_deagle"] = new(700),
            ["weapon_cz75a"] = new(500),
            ["weapon_revolver"] = new(600),
            ["weapon_mac10"] = new(1050, BotBuyTeam.Terrorist),
            ["weapon_mp9"] = new(1250, BotBuyTeam.CounterTerrorist),
            ["weapon_mp7"] = new(1500),
            ["weapon_mp5sd"] = new(1500),
            ["weapon_ump45"] = new(1200),
            ["weapon_bizon"] = new(1400),
            ["weapon_p90"] = new(2350),
            ["weapon_nova"] = new(1050),
            ["weapon_xm1014"] = new(2000),
            ["weapon_sawedoff"] = new(1100, BotBuyTeam.Terrorist),
            ["weapon_mag7"] = new(1300, BotBuyTeam.CounterTerrorist),
            ["weapon_galilar"] = new(1800, BotBuyTeam.Terrorist),
            ["weapon_ak47"] = new(2700, BotBuyTeam.Terrorist),
            ["weapon_sg556"] = new(3000, BotBuyTeam.Terrorist),
            ["weapon_famas"] = new(1950, BotBuyTeam.CounterTerrorist),
            ["weapon_m4a1"] = new(2900, BotBuyTeam.CounterTerrorist),
            ["weapon_m4a1_silencer"] = new(2900, BotBuyTeam.CounterTerrorist),
            ["weapon_aug"] = new(3300, BotBuyTeam.CounterTerrorist),
            ["weapon_ssg08"] = new(1700),
            ["weapon_awp"] = new(4750),
            ["weapon_scar20"] = new(5000, BotBuyTeam.CounterTerrorist),
            ["weapon_g3sg1"] = new(5000, BotBuyTeam.Terrorist),
            ["weapon_negev"] = new(1700),
            ["weapon_m249"] = new(5200),
        };

    private static readonly Dictionary<ushort, string> WeaponNames = new()
    {
        [1] = "weapon_deagle",
        [2] = "weapon_elite",
        [3] = "weapon_fiveseven",
        [4] = "weapon_glock",
        [7] = "weapon_ak47",
        [8] = "weapon_aug",
        [9] = "weapon_awp",
        [10] = "weapon_famas",
        [11] = "weapon_g3sg1",
        [13] = "weapon_galilar",
        [14] = "weapon_m249",
        [16] = "weapon_m4a1",
        [17] = "weapon_mac10",
        [19] = "weapon_p90",
        [23] = "weapon_mp5sd",
        [24] = "weapon_ump45",
        [25] = "weapon_xm1014",
        [26] = "weapon_bizon",
        [27] = "weapon_mag7",
        [28] = "weapon_negev",
        [29] = "weapon_sawedoff",
        [30] = "weapon_tec9",
        [31] = "weapon_taser",
        [32] = "weapon_hkp2000",
        [33] = "weapon_mp7",
        [34] = "weapon_mp9",
        [35] = "weapon_nova",
        [36] = "weapon_p250",
        [38] = "weapon_scar20",
        [39] = "weapon_sg556",
        [40] = "weapon_ssg08",
        [60] = "weapon_m4a1_silencer",
        [61] = "weapon_usp_silencer",
        [63] = "weapon_cz75a",
        [64] = "weapon_revolver",
    };

    public static bool TryGetPurchasePrice(
        string itemName,
        BotBuyTeam team,
        int armor,
        out int price)
    {
        price = 0;
        if (!ItemRules.TryGetValue(itemName, out var rule)
            || rule.Team != BotBuyTeam.None && rule.Team != team)
        {
            return false;
        }

        price = itemName == "item_assaultsuit" && armor > 99
            ? 350
            : rule.Price;
        return true;
    }

    public static bool TryGetRefundPrice(
        string itemName,
        BotBuyTeam team,
        out int price)
    {
        price = 0;
        if (!ItemRules.TryGetValue(itemName, out var rule)
            || itemName == "item_defuser"
            || rule.Team != BotBuyTeam.None && rule.Team != team)
        {
            return false;
        }

        price = rule.Price;
        return true;
    }

    public static bool TryGetWeaponName(
        ushort itemDefinitionIndex,
        out string weaponName)
        => WeaponNames.TryGetValue(itemDefinitionIndex, out weaponName!);

    public static bool IsPrimaryWeapon(string? weaponName)
        => weaponName is not null
            && (weaponName.StartsWith("weapon_ak", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_m4", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_aug", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_galilar", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_famas", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_awp", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_ssg08", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_mp", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_ump", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_p90", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_bizon", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_nova", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_mag7", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_sawedoff", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_xm1014", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_negev", StringComparison.Ordinal)
                || weaponName.StartsWith("weapon_m249", StringComparison.Ordinal));

    public static bool IsFirstRoundOfHalf(
        int roundsPlayed,
        int maxRounds,
        int overtimeMaxRounds)
    {
        maxRounds = maxRounds <= 0 ? 24 : maxRounds;
        overtimeMaxRounds = overtimeMaxRounds <= 0 ? 6 : overtimeMaxRounds;
        var half = maxRounds / 2;
        var overtimeHalf = overtimeMaxRounds / 2;
        return roundsPlayed == 0
            || roundsPlayed == half
            || roundsPlayed == maxRounds
            || roundsPlayed > maxRounds
                && overtimeHalf > 0
                && (roundsPlayed - maxRounds) % overtimeHalf == 0;
    }

    public static bool IsSecondToLastRoundOfHalf(
        int roundsPlayed,
        int maxRounds)
    {
        maxRounds = maxRounds <= 0 ? 24 : maxRounds;
        var half = maxRounds / 2;
        return roundsPlayed == half - 2
            || roundsPlayed == maxRounds - 2;
    }

    private readonly record struct ItemRule(
        int Price,
        BotBuyTeam Team = BotBuyTeam.None);
}
