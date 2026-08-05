namespace SharpGameModes.WorkshopMount;

internal static class WorkshopClientAdvertisementPolicy
{
    public static bool ShouldAdvertise(string addonId, string? serverAddons)
    {
        if (string.IsNullOrWhiteSpace(serverAddons))
        {
            return true;
        }

        var addons = serverAddons.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return addons.Length == 1
            && string.Equals(addons[0], addonId, StringComparison.Ordinal);
    }
}
