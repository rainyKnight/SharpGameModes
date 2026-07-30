using SharpGameModes.Contracts;

namespace SharpGameModes.Domain.Tests;

public sealed class BotIdentityRegistryTests : IDisposable
{
    public BotIdentityRegistryTests() => BotIdentityRegistry.Clear();

    [Fact]
    public void ManagedSlot_RemainsBotWhenEngineFlagIsHidden()
    {
        BotIdentityRegistry.MarkManaged(17);

        Assert.True(BotIdentityRegistry.IsBot(engineFakeClient: false, 17));
        Assert.False(BotIdentityRegistry.IsBot(engineFakeClient: false, 18));
        Assert.True(BotIdentityRegistry.IsBot(engineFakeClient: true, 18));
    }

    [Fact]
    public void ReleaseAndClear_RemoveOnlyExpectedSlots()
    {
        BotIdentityRegistry.MarkManaged(0);
        BotIdentityRegistry.MarkManaged(63);

        BotIdentityRegistry.Release(0);

        Assert.False(BotIdentityRegistry.IsManagedBot(0));
        Assert.True(BotIdentityRegistry.IsManagedBot(63));

        BotIdentityRegistry.Clear();

        Assert.False(BotIdentityRegistry.IsManagedBot(63));
    }

    public void Dispose() => BotIdentityRegistry.Clear();
}
