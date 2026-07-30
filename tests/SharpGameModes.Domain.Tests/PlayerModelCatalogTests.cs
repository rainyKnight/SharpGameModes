using System.Text.Json;
using SharpGameModes.Contracts;
using SharpGameModes.Domain;

namespace SharpGameModes.Domain.Tests;

public sealed class PlayerModelCatalogTests
{
    [Theory]
    [InlineData("classic", PlayerModelSide.T, true)]
    [InlineData("classic", PlayerModelSide.CT, true)]
    [InlineData("zombie", PlayerModelSide.CT, true)]
    [InlineData("zombie", PlayerModelSide.T, false)]
    public void ModePolicy_LeavesZombieCtSelectableAndReservesZombieT(
        string mode,
        PlayerModelSide side,
        bool expected)
        => Assert.Equal(
            expected,
            PlayerModelModePolicy.CanApplyPlayerModel(ModeId.Parse(mode), side));

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void ExampleCatalogParsesAndUsesStockModels()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Config",
            "sharp",
            "configs",
            "sharp-gamemodes",
            "player-models.jsonc");
        var config = JsonSerializer.Deserialize<PlayerModelCatalogConfig>(
            File.ReadAllText(path),
            Options);

        Assert.NotNull(config);
        config.Validate();
        Assert.False(config.Enabled);
        Assert.Equal(3, config.Models.Count);
        Assert.Equal(
            "characters/models/tm_phoenix/tm_phoenix.vmdl",
            config.Models["example_t"].Path);
        Assert.Equal(PlayerModelSide.CT, config.Models["example_ct"].Side);
        Assert.Equal(PlayerModelSide.All, config.Models["example_both"].Side);
        Assert.True(config.Models["example_both"].Supports(PlayerModelSide.T));
        Assert.True(config.Models["example_both"].Supports(PlayerModelSide.CT));
    }

    [Fact]
    public void SideSpecificModelsOnlySupportTheirConfiguredSide()
    {
        var model = new PlayerModelDefinition { Path = "model.vmdl", Side = PlayerModelSide.CT };

        Assert.True(model.Supports(PlayerModelSide.CT));
        Assert.False(model.Supports(PlayerModelSide.T));
        Assert.False(model.Supports(PlayerModelSide.All));
    }

    [Fact]
    public void MeshGroupMaskAppliesSelectedAndFixedStates()
    {
        var mask = PlayerModelMeshGroups.CalculateMask(
            [1, 3, 7],
            new Dictionary<int, int> { [3] = 0, [5] = 1 });

        Assert.Equal((1UL << 1) | (1UL << 5) | (1UL << 7), mask);
        Assert.Equal([1, 5, 7], PlayerModelMeshGroups.EnabledGroups(mask));
    }

    [Theory]
    [InlineData("\"eku\"", 1)]
    [InlineData("[\"eku\", \"miku\"]", 2)]
    public void DefaultModelIndexAcceptsPmcStringAndArrayForms(string json, int expectedCount)
    {
        var rule = JsonSerializer.Deserialize<PlayerModelDefaultRule>(
            $"{{\"index\":{json}}}",
            Options);

        Assert.NotNull(rule);
        Assert.Equal(expectedCount, rule.Index.Length);
    }
}
