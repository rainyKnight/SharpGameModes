namespace SharpGameModes.WorkshopMount.Tests;

public sealed class WorkshopClientAdvertisementPolicyTests
{
    private const string ResourceAddon = "3191706064";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ResourceAddon)]
    [InlineData(" 3191706064 ")]
    public void AdvertisesResourceAddonForValveMapReply(string? serverAddons)
    {
        Assert.True(WorkshopClientAdvertisementPolicy.ShouldAdvertise(
            ResourceAddon,
            serverAddons));
    }

    [Theory]
    [InlineData("3071005299")]
    [InlineData("3071005299,3191706064")]
    [InlineData("3191706064,3071005299")]
    [InlineData("de_dogtown")]
    public void PreservesWorkshopMapState(string serverAddons)
    {
        Assert.False(WorkshopClientAdvertisementPolicy.ShouldAdvertise(
            ResourceAddon,
            serverAddons));
    }
}
