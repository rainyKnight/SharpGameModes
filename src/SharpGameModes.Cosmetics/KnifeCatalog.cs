namespace SharpGameModes.Cosmetics;

internal sealed record KnifeOption(string ClassName, string DisplayName, int DefinitionIndex);

internal static class KnifeCatalog
{
    public const string DefaultClassName = "weapon_knife";

    public static readonly KnifeOption[] Options =
    [
        new(DefaultClassName, "默认刀", 0),
        new("weapon_bayonet", "刺刀", 500),
        new("weapon_knife_css", "经典匕首", 503),
        new("weapon_knife_flip", "折叠刀", 505),
        new("weapon_knife_gut", "穿肠刀", 506),
        new("weapon_knife_karambit", "爪子刀", 507),
        new("weapon_knife_m9_bayonet", "M9 刺刀", 508),
        new("weapon_knife_tactical", "猎杀者匕首", 509),
        new("weapon_knife_falchion", "弯刀", 512),
        new("weapon_knife_survival_bowie", "鲍伊猎刀", 514),
        new("weapon_knife_butterfly", "蝴蝶刀", 515),
        new("weapon_knife_push", "暗影双匕", 516),
        new("weapon_knife_cord", "系绳匕首", 517),
        new("weapon_knife_canis", "求生匕首", 518),
        new("weapon_knife_ursus", "熊刀", 519),
        new("weapon_knife_gypsy_jackknife", "折刀", 520),
        new("weapon_knife_outdoor", "流浪者匕首", 521),
        new("weapon_knife_stiletto", "短剑", 522),
        new("weapon_knife_widowmaker", "锯齿爪刀", 523),
        new("weapon_knife_skeleton", "骷髅匕首", 525),
        new("weapon_knife_kukri", "廓尔喀刀", 526),
    ];

    public static bool TryGet(string className, out KnifeOption option)
    {
        option = Options.FirstOrDefault(
            candidate => candidate.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))!;
        return option is not null;
    }

    public static bool IsKnifeClassName(string className)
        => className.Contains("knife", StringComparison.OrdinalIgnoreCase)
            || className.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
}
