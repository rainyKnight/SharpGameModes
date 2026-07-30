using System.Collections.ObjectModel;

namespace SharpGameModes.Cosmetics.Storage;

public sealed record WeaponSkinPreference(
    ulong SteamId,
    int Team,
    int WeaponDefinitionIndex,
    int PaintKit,
    double Wear,
    int Seed,
    string NameTag,
    bool StatTrak,
    int StatTrakCount,
    string Sticker0,
    string Sticker1,
    string Sticker2,
    string Sticker3,
    string Sticker4,
    string Keychain);

public sealed record KnifePreference(
    ulong SteamId,
    int Team,
    string ClassName);

public readonly record struct WeaponSkinKey(
    ulong SteamId,
    int Team,
    int WeaponDefinitionIndex);

public readonly record struct KnifeKey(
    ulong SteamId,
    int Team);

public sealed class CosmeticsSnapshot
{
    public CosmeticsSnapshot(
        IDictionary<WeaponSkinKey, WeaponSkinPreference> weaponSkins,
        IDictionary<KnifeKey, KnifePreference> knives)
    {
        WeaponSkins = new ReadOnlyDictionary<WeaponSkinKey, WeaponSkinPreference>(
            new Dictionary<WeaponSkinKey, WeaponSkinPreference>(weaponSkins));
        Knives = new ReadOnlyDictionary<KnifeKey, KnifePreference>(
            new Dictionary<KnifeKey, KnifePreference>(knives));
    }

    public IReadOnlyDictionary<WeaponSkinKey, WeaponSkinPreference> WeaponSkins { get; }
    public IReadOnlyDictionary<KnifeKey, KnifePreference> Knives { get; }
}
