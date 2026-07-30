using SharpGameModes.Domain;

namespace SharpGameModes.Domain.Tests;

public sealed class WeaponSkinCatalogTests
{
    [Fact]
    public void ParseAcceptsLegacyStringAndNumberIdentifiers()
    {
        const string json =
            """
            [
              {
                "weapon_defindex": 7,
                "weapon_name": "weapon_ak47",
                "paint": "302",
                "paint_name": "Vulcan",
                "legacy_model": true
              },
              {
                "weapon_defindex": "7",
                "weapon_name": "weapon_ak47",
                "paint": 0,
                "paint_name": "Default",
                "legacy_model": false
              }
            ]
            """;

        var catalog = WeaponSkinCatalog.Parse(json);

        var group = Assert.Single(catalog.Weapons);
        Assert.Equal(2, group.Paints.Count);
        Assert.True(catalog.TryGetPaint(7, 302, out var paint));
        Assert.True(paint.LegacyModel);
        Assert.Equal(0, group.Paints[0].PaintKit);
    }

    [Fact]
    public void ParseRejectsCatalogWithoutValidRows()
    {
        Assert.Throws<InvalidDataException>(() => WeaponSkinCatalog.Parse("[]"));
    }
}
