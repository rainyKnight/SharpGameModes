using System.Threading;

namespace SharpGameModes.Contracts;

/// <summary>
/// Tracks engine bots whose public fake-client identity is temporarily hidden.
/// Consumers must combine this registry with the engine's native fake-client flag.
/// </summary>
public static class BotIdentityRegistry
{
    private static long _managedSlots;

    public static bool IsManagedBot(int slot)
        => slot is >= 0 and < 64
            && (unchecked((ulong)Volatile.Read(ref _managedSlots)) & (1UL << slot)) != 0;

    public static bool IsBot(bool engineFakeClient, int slot)
        => engineFakeClient || IsManagedBot(slot);

    public static void MarkManaged(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 63);
        Interlocked.Or(ref _managedSlots, unchecked((long)(1UL << slot)));
    }

    public static void Release(int slot)
    {
        if (slot is < 0 or >= 64)
        {
            return;
        }

        Interlocked.And(ref _managedSlots, unchecked((long)~(1UL << slot)));
    }

    public static void Clear() => Interlocked.Exchange(ref _managedSlots, 0);
}
