namespace SharpGameModes.Contracts;

public interface IBotHider
{
    public const string Identity = "SharpGameModes.Contracts.IBotHider";

    bool IsActive { get; }

    bool IsManagedBot(int slot);

    ulong GetBotSteamId(int slot);

    int[] GetManagedSlots();

    string GetPersonaName(int slot);

    int GetPing(int slot);

    string GetCrosshairCode(int slot);

    bool HasBotAvatar(int slot);

    int GetConfiguredAvatarSize(int slot);

    uint GetScoreboardFlair(int slot);

    (string Name, ulong Address)[] GetSignatures();

    bool SetBotSteamId(int slot, ulong steamId64);

    bool SetCrosshairCode(int slot, string code);

    bool SetBotAvatar(int slot, string pngPath);

    bool SetPersonaName(int slot, string name);

    bool SetScoreboardFlair(int slot, uint itemDefIndex);

    bool SetDisguise(bool enabled);

    bool SetNameSource(bool useBotInfo);
}
