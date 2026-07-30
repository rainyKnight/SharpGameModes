using System.Text.RegularExpressions;

namespace SharpGameModes.BotMatch.Tests;

public sealed class HltvTop10ProfileTests
{
    private static readonly string[] ExpectedRoster =
    [
        "apEX", "ZywOo", "ropz", "mezii", "flameZ",
        "yuurih", "FalleN", "KSCERATO", "YEKINDAR", "molodoy",
        "NiKo", "TeSeS", "m0NESY", "karrigan", "kyousuke",
        "jL", "torzsi", "Spinx", "xelex", "xertioN",
        "enkay J", "frozen", "Twistzz", "broky", "jcobbb",
        "bLitz", "Techno4K", "mzinho", "910", "cobrazera",
        "Aleksib", "iM", "b1t", "w0nderful", "makazze",
        "sh1ro", "magixx", "tN1R", "zont1x", "donk",
        "huNter-", "NertZ", "SunPayus", "HeavyGod", "MATYS",
        "MAJ3R", "XANTARES", "woxic", "soulfly", "Wicadia",
    ];

    [Fact]
    public void Database_ContainsOnlyTheFirstTenUpstreamTeams()
    {
        var database = File.ReadAllText(DatabasePath());
        var profiles = Regex.Matches(
                database,
                "^(?<templates>[^\\r\\n\"]+?)\\s+\"(?<name>[^\"]+)\"\\s*$",
                RegexOptions.Multiline)
            .Select(match => new
            {
                Templates = match.Groups["templates"].Value,
                Name = match.Groups["name"].Value,
            })
            .ToArray();

        Assert.Equal(ExpectedRoster, profiles.Select(profile => profile.Name));
        Assert.All(
            profiles,
            profile => Assert.StartsWith("Pro", profile.Templates));
        Assert.DoesNotContain(
            profiles,
            profile => profile.Templates.StartsWith(
                "Rank",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Database_DocumentsThePinnedUpstreamRoster()
    {
        var database = File.ReadAllText(DatabasePath());

        Assert.Contains("Curated HLTVTop10 roster.", database);
        Assert.Contains(
            "7649abe4b1f0b67c6826aea0c3c488348799ca60",
            database);
    }

    private static string DatabasePath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Config",
            "csgo",
            "overrides",
            "HLTVTop10",
            "botprofile.db");
}
