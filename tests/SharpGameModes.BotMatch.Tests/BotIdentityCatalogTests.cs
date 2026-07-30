namespace SharpGameModes.BotMatch.Tests;

public sealed class BotIdentityCatalogTests
{
    [Fact]
    public void PackagedCatalog_PreservesLatestBotHiderProfiles()
    {
        var catalog = BotIdentityCatalog.Load(CatalogPath());

        Assert.Equal(1_939, catalog.Count);
        Assert.Equal(1_424, catalog.CrosshairCount);
        Assert.Equal(1_420, catalog.FlairCount);
        Assert.True(catalog.TryGetByName("s1mple", out var s1mple));
        Assert.Equal(73_936_547U, s1mple.AccountId);
        Assert.Equal(
            BotIdentityCatalog.SteamId64IndividualBase + 73_936_547U,
            s1mple.SteamId64);
        Assert.StartsWith("CSGO-", s1mple.CrosshairCode);
        Assert.InRange(s1mple.ScoreboardFlair, 1U, ushort.MaxValue);
    }

    [Fact]
    public void ChooseAvailable_PrefersExactBotProfileName()
    {
        var catalog = BotIdentityCatalog.Load(CatalogPath());

        var selected = catalog.ChooseAvailable(
            "ZywOo",
            new HashSet<ulong>(),
            new Random(7));

        Assert.NotNull(selected);
        Assert.Equal("ZywOo", selected.Name);
    }

    [Fact]
    public void ChooseAvailable_AvoidsOccupiedSteamIdEvenForExactName()
    {
        var catalog = BotIdentityCatalog.Load(CatalogPath());
        Assert.True(catalog.TryGetByName("ZywOo", out var exact));

        var selected = catalog.ChooseAvailable(
            "ZywOo",
            new HashSet<ulong> { exact.SteamId64 },
            new Random(7));

        Assert.NotNull(selected);
        Assert.NotEqual(exact.SteamId64, selected.SteamId64);
    }

    [Fact]
    public void EveryPackagedProfile_RespectsNetworkFieldLimits()
    {
        var catalog = BotIdentityCatalog.Load(CatalogPath());

        Assert.All(
            catalog.Profiles,
            profile =>
            {
                Assert.InRange(
                    System.Text.Encoding.UTF8.GetByteCount(profile.Name),
                    1,
                    BotIdentityProfile.MaxNameUtf8Bytes);
                Assert.InRange(
                    System.Text.Encoding.UTF8.GetByteCount(profile.CrosshairCode),
                    0,
                    BotIdentityProfile.MaxCrosshairUtf8Bytes);
                Assert.InRange(profile.ScoreboardFlair, 0U, ushort.MaxValue);
            });
    }

    private static string CatalogPath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Config",
            "sharp",
            "configs",
            "sharp-gamemodes",
            "botmatch-identities",
            "bot_info.json");
}
