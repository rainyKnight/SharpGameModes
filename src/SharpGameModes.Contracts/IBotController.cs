namespace SharpGameModes.Contracts;

/// <summary>
/// Pure ModSharp equivalent of CS2-Bot-Controller's ABI 16 shared API.
/// The interface is registered for the lifetime of SharpGameModes.BotMatch, while
/// mutating operations only succeed when botmatch mode is active.
/// </summary>
public interface IBotController
{
    public const string Identity = "SharpGameModes.Contracts.IBotController";
    public const int CurrentAbiVersion = 16;
    public const int KnifeDefinition = 9001;

    int AbiVersion { get; }

    bool IsActive { get; }

    bool Lock(int slot, BotLockKind kind);

    bool Lock(int slot, BotLockTarget target);

    bool Unlock(int slot, BotLockKind kind);

    bool UnlockAll(BotLockKind kind);

    bool IsLocked(int slot, BotLockKind kind);

    BotLockTarget GetWeaponLock(int slot);

    bool StartRecord(int slot);

    bool StopRecord(int slot);

    bool IsRecording(int slot);

    int RecordedTickCount(int slot);

    (BotReplayTick[] Ticks, BotSubtickMove[] Subticks) GetRecordedMotion(int slot);

    bool LoadReplay(int slot, BotReplayTick[] ticks, BotSubtickMove[] subticks);

    bool LoadReplayExtended(
        int slot,
        BotReplayTick[] ticks,
        BotSubtickMove[] subticks,
        BotReplayCommandFrame[] commandFrames,
        BotReplayMovementExtra[] movementExtras);

    bool TransferRecordingToReplay(int sourceSlot, int destinationSlot);

    bool SetReplayPawn(int slot, nint pawn);

    bool StartReplay(int slot, bool loop = false);

    bool StopReplay(int slot);

    int ReplayCursor(int slot);

    int ReplayTotal(int slot);

    bool IsReplaying(int slot);

    bool TryGetReplayTick(int slot, out BotReplayTick tick);

    bool SwitchBotWeapon(int slot, int definitionIndex);

    int BotActiveWeaponDef(int slot);

    long InjectUsercmd(int slot, ulong buttonMask, int durationMs = 0);

    bool CancelUsercmdInjection(int slot, long injectionId);

    bool GetBotProfile(int slot, out BotProfileData profile);

    bool SetBuyPlan(int slot, string aliases);

    bool SetBuySkip(int slot);

    bool ClearBuyPlan(int slot);

    bool ClearAllBuyPlans();

    int BuyPlanItemCount(int slot);

    bool CanSendVoice();

    int GetVoiceStatus();

    int SendVoiceFrame(
        int recipientSlot,
        int senderClient,
        ulong senderXuid,
        byte[] audio,
        int audioBytes,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        uint[] packetOffsets,
        int packetOffsetCount,
        int tick,
        int audibleMask);
}
