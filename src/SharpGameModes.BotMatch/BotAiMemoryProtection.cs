using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpGameModes.BotMatch;

internal sealed class BotAiMemoryProtection : IDisposable
{
    private readonly nint _address;
    private readonly nuint _length;
    private readonly uint _windowsProtection;
    private readonly int _linuxProtection;
    private readonly bool _windows;
    private bool _disposed;

    private BotAiMemoryProtection(
        nint address,
        nuint length,
        uint windowsProtection,
        int linuxProtection,
        bool windows)
    {
        _address = address;
        _length = length;
        _windowsProtection = windowsProtection;
        _linuxProtection = linuxProtection;
        _windows = windows;
    }

    public static bool TryMakeWritable(
        nint address,
        int length,
        out BotAiMemoryProtection? scope)
    {
        scope = null;
        if (address == 0 || length <= 0)
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!BotAiNativeMemory.VirtualProtect(
                    address,
                    (nuint)length,
                    0x40,
                    out var previous))
            {
                return false;
            }

            scope = new BotAiMemoryProtection(
                address,
                (nuint)length,
                previous,
                0,
                windows: true);
            return true;
        }

        var pageSize = Environment.SystemPageSize;
        var start = (long)address & ~(pageSize - 1L);
        var end = ((long)address + length + pageSize - 1L)
            & ~(pageSize - 1L);
        var pageLength = checked((nuint)(end - start));
        var original = FindLinuxProtection(address);
        if (BotAiNativeMemory.MProtect(
                (nint)start,
                pageLength,
                original | 2) != 0)
        {
            return false;
        }

        scope = new BotAiMemoryProtection(
            (nint)start,
            pageLength,
            0,
            original,
            windows: false);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_windows)
        {
            BotAiNativeMemory.VirtualProtect(
                _address,
                _length,
                _windowsProtection,
                out _);
        }
        else
        {
            BotAiNativeMemory.MProtect(
                _address,
                _length,
                _linuxProtection);
        }
    }

    private static int FindLinuxProtection(nint address)
    {
        try
        {
            var target = (ulong)address;
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var columns = line.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2)
                {
                    continue;
                }

                var range = columns[0].Split('-', 2);
                if (range.Length != 2
                    || !ulong.TryParse(
                        range[0],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var start)
                    || !ulong.TryParse(
                        range[1],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var end)
                    || target < start
                    || target >= end)
                {
                    continue;
                }

                var permissions = columns[1];
                var protection = 0;
                if (permissions.Length > 0 && permissions[0] == 'r')
                {
                    protection |= 1;
                }

                if (permissions.Length > 1 && permissions[1] == 'w')
                {
                    protection |= 2;
                }

                if (permissions.Length > 2 && permissions[2] == 'x')
                {
                    protection |= 4;
                }

                return protection == 0 ? 5 : protection;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 5;
    }
}

internal static partial class BotAiNativeMemory
{
    [LibraryImport("libc", EntryPoint = "mprotect")]
    internal static partial int MProtect(
        nint address,
        nuint length,
        int protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtect(
        nint address,
        nuint length,
        uint newProtection,
        out uint oldProtection);
}
