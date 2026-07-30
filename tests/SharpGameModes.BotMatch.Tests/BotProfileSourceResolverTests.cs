namespace SharpGameModes.BotMatch.Tests;

public sealed class BotProfileSourceResolverTests
{
    [Theory]
    [InlineData("low", "Low")]
    [InlineData(" Medium ", "Medium")]
    [InlineData(" HLTVTOP10 ", "HLTVTop10")]
    [InlineData(" HLTVTOP37 ", "HLTVTop10")]
    [InlineData("HIGH", "High")]
    public void TryNormalizeTier_AcceptsSupportedTiers(
        string value,
        string expected)
    {
        Assert.True(
            BotProfileSourceResolver.TryNormalizeTier(
                value,
                out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("expert")]
    [InlineData("medium/../../")]
    public void TryNormalizeTier_RejectsUnsupportedValues(string value)
    {
        Assert.False(
            BotProfileSourceResolver.TryNormalizeTier(
                value,
                out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void Resolve_PrefersVpkAndUsesRawDatabaseForExactValidation()
    {
        using var directory = new TemporaryDirectory();
        var tier = Path.Combine(directory.Path, "HLTVTop10");
        Directory.CreateDirectory(tier);
        File.WriteAllText(Path.Combine(tier, "botprofile.db"), "raw");
        File.WriteAllText(Path.Combine(tier, "botprofile.vpk"), "vpk");

        var source = BotProfileSourceResolver.Resolve(
            directory.Path,
            "hltvtop37");

        Assert.NotNull(source);
        Assert.Equal(BotProfileSourceFormat.Vpk, source.Value.Format);
        Assert.Equal(Path.Combine(tier, "botprofile.vpk"), source.Value.SearchPath);
        Assert.Equal(
            Path.Combine(tier, "botprofile.db"),
            source.Value.DatabasePath);
        Assert.Equal(3L, source.Value.ExpectedDatabaseBytes);
    }

    [Fact]
    public void Resolve_UsesRawDatabaseDirectoryWhenVpkIsAbsent()
    {
        using var directory = new TemporaryDirectory();
        var tier = Path.Combine(directory.Path, "High");
        Directory.CreateDirectory(tier);
        var database = Path.Combine(tier, "botprofile.db");
        File.WriteAllText(database, "raw");

        var source = BotProfileSourceResolver.Resolve(
            directory.Path,
            "high");

        Assert.NotNull(source);
        Assert.Equal(BotProfileSourceFormat.Directory, source.Value.Format);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(tier) + Path.DirectorySeparatorChar,
            source.Value.SearchPath);
        Assert.Equal(database, source.Value.DatabasePath);
        Assert.Equal(3L, source.Value.ExpectedDatabaseBytes);
    }

    [Fact]
    public void Resolve_UsesVpkAsCompatibilityFallback()
    {
        using var directory = new TemporaryDirectory();
        var tier = Path.Combine(directory.Path, "Low");
        Directory.CreateDirectory(tier);
        var vpk = Path.Combine(tier, "botprofile.vpk");
        File.WriteAllText(vpk, "vpk");

        var source = BotProfileSourceResolver.Resolve(
            directory.Path,
            "low");

        Assert.NotNull(source);
        Assert.Equal(BotProfileSourceFormat.Vpk, source.Value.Format);
        Assert.Equal(vpk, source.Value.SearchPath);
        Assert.Equal(0L, source.Value.ExpectedDatabaseBytes);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenTierDataIsMissing()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(
            BotProfileSourceResolver.Resolve(
                directory.Path,
                "medium"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sharp-gamemodes-botprofile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
            => Directory.Delete(Path, recursive: true);
    }
}
