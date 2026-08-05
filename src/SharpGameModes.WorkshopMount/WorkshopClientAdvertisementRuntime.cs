// Connection-reply behavior is adapted from Source2ZE/MultiAddonManager.
// See THIRD_PARTY_NOTICES.md and LICENSES/GPL-3.0-MultiAddonManager.txt.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Hooks;

[assembly: DisableRuntimeMarshalling]

namespace SharpGameModes.WorkshopMount;

/// <summary>
/// Advertises the configured resource addon in the connection reply for Valve
/// maps. ModSharp's built-in dual-addon state machine remains responsible for
/// replies that already contain a Workshop map or multiple addons.
/// </summary>
internal sealed class WorkshopClientAdvertisementRuntime : IDisposable
{
    private const int ServerAddonsOffset = 344;
    private const string ReplyConnectionLinux = "55 B9 ? ? ? ? 41 B8";
    private const string ReplyConnectionWindows = "48 8B C4 55 41 55 41 56";

    private static WorkshopClientAdvertisementRuntime? s_active;
    private static unsafe delegate* unmanaged<nint, nint, void> s_replyConnectionOriginal;

    private readonly object _connectionGate = new();
    private readonly ISharedSystem _shared;
    private readonly ILogger _logger;
    private readonly string _addonId;
    private IDetourHook? _hook;
    private nint _addonIdUtf8;
    private long _replies;
    private long _advertised;
    private long _preserved;
    private long _errors;
    private bool _active;

    public WorkshopClientAdvertisementRuntime(
        ISharedSystem shared,
        ulong addonId,
        ILogger logger)
    {
        _shared = shared;
        _logger = logger;
        _addonId = addonId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Activate()
    {
        if (_active)
        {
            return true;
        }

        if (Volatile.Read(ref s_active) is not null)
        {
            _logger.LogError("Another Workshop client advertisement hook is already active.");
            return false;
        }

        try
        {
            var signature = OperatingSystem.IsWindows()
                ? ReplyConnectionWindows
                : ReplyConnectionLinux;
            var target = _shared.GetLibraryModuleManager().Engine.FindPatternExactly(signature);
            if (target == 0)
            {
                _logger.LogError("ReplyConnection signature could not be resolved in engine2.");
                return false;
            }

            _addonIdUtf8 = Marshal.StringToCoTaskMemUTF8(_addonId);
            unsafe
            {
                _hook = _shared.GetHookManager().CreateDetourHook();
                _hook.Prepare(
                    target,
                    (nint)(delegate* unmanaged<nint, nint, void>)&HookReplyConnection);
                if (!_hook.Install())
                {
                    _logger.LogError(
                        "Failed to install the ReplyConnection detour at 0x{Address:X}.",
                        target);
                    Volatile.Write(ref s_active, null);
                    s_replyConnectionOriginal = null;
                    ReleaseHook();
                    ReleaseAddonString();
                    return false;
                }

                s_replyConnectionOriginal =
                    (delegate* unmanaged<nint, nint, void>)_hook.Trampoline;
                _active = true;
                Volatile.Write(ref s_active, this);
            }

            _logger.LogInformation(
                "Workshop client connection advertisement enabled for addon {AddonId} at 0x{Address:X}.",
                _addonId,
                target);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to enable Workshop client connection advertisement.");
            Volatile.Write(ref s_active, null);
            unsafe
            {
                s_replyConnectionOriginal = null;
            }

            ReleaseHook();
            ReleaseAddonString();
            return false;
        }
    }

    public WorkshopClientAdvertisementSnapshot GetSnapshot()
        => new(
            Interlocked.Read(ref _replies),
            Interlocked.Read(ref _advertised),
            Interlocked.Read(ref _preserved),
            Interlocked.Read(ref _errors));

    public void Dispose()
    {
        if (ReferenceEquals(Volatile.Read(ref s_active), this))
        {
            Volatile.Write(ref s_active, null);
        }

        _active = false;
        ReleaseHook();

        lock (_connectionGate)
        {
            ReleaseAddonString();
        }

        unsafe
        {
            s_replyConnectionOriginal = null;
        }

        var snapshot = GetSnapshot();
        _logger.LogInformation(
            "Workshop client connection advertisement disabled. Replies {Replies}, advertised {Advertised}, preserved {Preserved}, errors {Errors}.",
            snapshot.Replies,
            snapshot.Advertised,
            snapshot.Preserved,
            snapshot.Errors);
    }

    private void ReleaseHook()
    {
        var hook = Interlocked.Exchange(ref _hook, null);
        if (hook is null)
        {
            return;
        }

        try
        {
            hook.Uninstall();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to uninstall the ReplyConnection detour cleanly.");
        }
        finally
        {
            hook.Dispose();
        }
    }

    private void ReleaseAddonString()
    {
        var pointer = Interlocked.Exchange(ref _addonIdUtf8, 0);
        if (pointer != 0)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void HookReplyConnection(nint server, nint client)
    {
        var original = s_replyConnectionOriginal;
        if (original == null)
        {
            return;
        }

        var runtime = Volatile.Read(ref s_active);
        if (runtime is null || !runtime._active || server == 0 || client == 0)
        {
            original(server, client);
            return;
        }

        Interlocked.Increment(ref runtime._replies);
        lock (runtime._connectionGate)
        {
            var addonsField = (nint*)(server + ServerAddonsOffset);
            var originalAddonsPointer = *addonsField;
            var replaced = false;
            try
            {
                var originalAddons = originalAddonsPointer == 0
                    ? null
                    : Marshal.PtrToStringUTF8(originalAddonsPointer);
                if (runtime._addonIdUtf8 != 0
                    && WorkshopClientAdvertisementPolicy.ShouldAdvertise(
                        runtime._addonId,
                        originalAddons))
                {
                    *addonsField = runtime._addonIdUtf8;
                    replaced = true;
                    Interlocked.Increment(ref runtime._advertised);
                }
                else
                {
                    Interlocked.Increment(ref runtime._preserved);
                }

            }
            catch
            {
                Interlocked.Increment(ref runtime._errors);
            }

            try
            {
                original(server, client);
            }
            finally
            {
                if (replaced)
                {
                    *addonsField = originalAddonsPointer;
                }
            }
        }
    }
}

internal readonly record struct WorkshopClientAdvertisementSnapshot(
    long Replies,
    long Advertised,
    long Preserved,
    long Errors);
