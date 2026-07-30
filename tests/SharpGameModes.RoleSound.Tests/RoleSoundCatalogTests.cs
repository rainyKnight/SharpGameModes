using Xunit;

namespace SharpGameModes.RoleSound.Tests;

public sealed class RoleSoundCatalogTests
{
    [Fact]
    public void ResolvesExactFolderAndImplicitProfileMappings()
    {
        var config = CreateConfig();
        config.ModelFolderToVoiceProfile["mapped_model"] = "voice_a";
        config.Normalize();
        var catalog = new RoleSoundCatalog(config);

        Assert.Equal(
            "voice_a",
            catalog.ResolveProfileName("characters/models/mapped_model/player/player.vmdl"));
        Assert.Equal(
            "voice_a",
            catalog.ResolveProfileName("characters/models/voice_a/player/player.vmdl"));
    }

    [Fact]
    public void ExactPathMappingHasPriorityOverFolderMapping()
    {
        var config = CreateConfig();
        config.ModelFolderToVoiceProfile["mapped_model"] = "voice_a";
        config.ModelPathToVoiceProfile["characters/models/mapped_model/player/player.vmdl"] = "voice_b";
        config.Normalize();
        var catalog = new RoleSoundCatalog(config);

        Assert.Equal(
            "voice_b",
            catalog.ResolveProfileName("characters\\models\\mapped_model\\player\\player.vmdl"));
    }

    [Fact]
    public void BuildsConfiguredSoundEventName()
    {
        var config = CreateConfig();
        config.Normalize();
        var catalog = new RoleSoundCatalog(config);
        Assert.True(catalog.TrySelect("voice_a", "death", new Random(1), out var selected));

        Assert.Equal("rolesound.voice_a.dead", catalog.BuildSoundEventName(selected));
    }

    [Fact]
    public void UsesEventFallbackProfile()
    {
        var config = CreateConfig();
        config.VoiceProfiles["voice_b"] = new VoiceProfileConfig
        {
            Events = new Dictionary<string, List<string>>
            {
                ["round_end"] = ["sounds/rolesound/voice_b/win.vsnd_c"],
            },
        };
        config.EventFallbackVoiceProfiles["round_end"] = ["voice_b"];
        config.Normalize();
        var catalog = new RoleSoundCatalog(config);

        Assert.True(catalog.TrySelect("voice_a", "round_end", new Random(1), out var selected));
        Assert.Equal("voice_b", selected.ProfileName);
    }

    [Theory]
    [InlineData("characters/models/anomea/player/model.vmdl", "anomea")]
    [InlineData("CHARACTERS\\MODELS\\Anomea\\player\\model.vmdl", "anomea")]
    [InlineData("models/player/custom.vmdl", null)]
    public void ExtractsModelFolder(string path, string? expected)
        => Assert.Equal(expected, RoleSoundCatalog.ExtractModelFolder(path));

    private static RoleSoundConfig CreateConfig()
        => new()
        {
            SoundEventTemplate = "rolesound.{profile}.{sound_event}",
            SoundEventNames = new Dictionary<string, string>
            {
                ["death"] = "dead",
            },
            VoiceProfiles = new Dictionary<string, VoiceProfileConfig>
            {
                ["voice_a"] = new()
                {
                    Events = new Dictionary<string, List<string>>
                    {
                        ["death"] = ["sounds/rolesound/voice_a/dead.vsnd_c"],
                    },
                },
            },
        };
}
