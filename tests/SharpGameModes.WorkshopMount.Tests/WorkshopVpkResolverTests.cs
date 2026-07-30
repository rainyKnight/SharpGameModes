namespace SharpGameModes.WorkshopMount.Tests;

public sealed class WorkshopVpkResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharp-gamemodes-workshop-mount-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveUsesUnsuffixedBasePathForChunkedVpk()
    {
        const ulong addonId = 3191706064;
        var addonDirectory = CreateAddonDirectory(addonId);
        var indexPath = Path.Combine(addonDirectory, $"{addonId}_dir.vpk");
        File.WriteAllBytes(indexPath, [1]);

        var result = WorkshopVpkResolver.Resolve(_root, addonId);

        Assert.NotNull(result);
        Assert.True(result.Value.IsChunked);
        Assert.Equal(indexPath, result.Value.IndexPath);
        Assert.Equal(
            Path.Combine(addonDirectory, $"{addonId}.vpk"),
            result.Value.SearchPath);
    }

    [Fact]
    public void ResolveFallsBackToLegacyVpk()
    {
        const ulong addonId = 3191706064;
        var addonDirectory = CreateAddonDirectory(addonId);
        var legacyPath = Path.Combine(addonDirectory, $"{addonId}.vpk");
        File.WriteAllBytes(legacyPath, [1]);

        var result = WorkshopVpkResolver.Resolve(_root, addonId);

        Assert.NotNull(result);
        Assert.False(result.Value.IsChunked);
        Assert.Equal(legacyPath, result.Value.IndexPath);
        Assert.Equal(legacyPath, result.Value.SearchPath);
    }

    [Fact]
    public void ResolveReturnsNullWhenAddonIsMissing()
    {
        Assert.Null(WorkshopVpkResolver.Resolve(_root, 3191706064));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateAddonDirectory(ulong addonId)
    {
        var runtimeDirectory = OperatingSystem.IsWindows() ? "win64" : "linuxsteamrt64";
        var directory = Path.Combine(
            _root,
            "bin",
            runtimeDirectory,
            "steamapps",
            "workshop",
            "content",
            "730",
            addonId.ToString());
        Directory.CreateDirectory(directory);
        return directory;
    }
}
