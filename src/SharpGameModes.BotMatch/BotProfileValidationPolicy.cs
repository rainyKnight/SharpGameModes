namespace SharpGameModes.BotMatch;

internal static class BotProfileValidationPolicy
{
    private const int MinimumUnverifiedDatabaseBytes = 64 * 1024;

    public static bool TryValidate(
        int resolvedDatabaseBytes,
        long expectedDatabaseBytes,
        out string error)
    {
        if (resolvedDatabaseBytes <= 0)
        {
            error = "the GAME search path did not resolve botprofile.db";
            return false;
        }

        if (expectedDatabaseBytes > 0)
        {
            if (resolvedDatabaseBytes == expectedDatabaseBytes)
            {
                error = string.Empty;
                return true;
            }

            error =
                $"the GAME search path resolved {resolvedDatabaseBytes} bytes, " +
                $"but the selected source contains {expectedDatabaseBytes} bytes";
            return false;
        }

        if (resolvedDatabaseBytes >= MinimumUnverifiedDatabaseBytes)
        {
            error = string.Empty;
            return true;
        }

        error =
            $"the unverified VPK fallback resolved only {resolvedDatabaseBytes} bytes; " +
            $"at least {MinimumUnverifiedDatabaseBytes} bytes are required";
        return false;
    }
}
