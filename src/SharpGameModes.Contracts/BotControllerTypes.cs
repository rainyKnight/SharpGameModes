using System.Runtime.InteropServices;

namespace SharpGameModes.Contracts;

public enum BotLockKind
{
    All = 0,
    Aim = 1,
    Weapon = 2,
    Jump = 3,
}

public enum BotLockTarget
{
    None = 0,
    Slot1 = 1,
    Slot2 = 2,
    Slot3 = 3,
    Slot4 = 4,
    Slot5 = 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotMovementSnapshot
{
    public float OriginX;
    public float OriginY;
    public float OriginZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public uint EntityFlags;
    public byte MoveType;
    public byte Pad0;
    public byte Pad1;
    public byte Pad2;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public float DuckAmount;
    public float DuckSpeed;
    public float LadderNormalX;
    public float LadderNormalY;
    public float LadderNormalZ;
    public byte Ducked;
    public byte Ducking;
    public byte DesiresDuck;
    public byte ActualMoveType;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotReplayTick
{
    public BotMovementSnapshot Pre;
    public BotMovementSnapshot Post;
    public int WeaponDefIndex;
    public uint NumSubtick;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotSubtickMove
{
    public float When;
    public uint Button;
    public float Pressed;
    public float AnalogForward;
    public float AnalogLeft;
    public float PitchDelta;
    public float YawDelta;
}

[Flags]
public enum BotReplayCommandFields : uint
{
    None = 0,
    Movement = 1 << 0,
    ViewAngles = 1 << 1,
    Buttons = 1 << 2,
    Mouse = 1 << 3,
    WeaponSelect = 1 << 4,
    LeftHandDesired = 1 << 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotReplayCommandFrame
{
    public float ForwardMove;
    public float LeftMove;
    public float UpMove;
    public float Pitch;
    public float Yaw;
    public float Roll;
    public ulong Buttons;
    public ulong Buttons1;
    public ulong Buttons2;
    public int MouseDx;
    public int MouseDy;
    public int WeaponSelect;
    public uint Fields;
    public byte LeftHandDesired;
    public byte Pad0;
    public byte Pad1;
    public byte Pad2;
}

[Flags]
public enum BotReplayMovementFields : uint
{
    None = 0,
    JumpPressedTime = 1 << 0,
    LastDuckTime = 1 << 1,
    LastActualJumpPress = 1 << 2,
    LastUsableJumpPress = 1 << 3,
    LastLanded = 1 << 4,
    LastLandedVelocity = 1 << 5,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotReplayMovementExtra
{
    public uint Fields;
    public float JumpPressedTime;
    public float LastDuckTime;
    public int LastActualJumpPressTick;
    public float LastActualJumpPressFrac;
    public int LastUsableJumpPressTick;
    public float LastUsableJumpPressFrac;
    public int LastLandedTick;
    public float LastLandedFrac;
    public float LastLandedVelocityX;
    public float LastLandedVelocityY;
    public float LastLandedVelocityZ;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BotProfileData
{
    public float Aggression;
    public float Skill;
    public float Teamwork;
    public float ReactionTime;
    public float AttackDelay;
    public float LookAccelAtk;
    public float LookStiffAtk;
    public float LookDampAtk;
    public int Cost;
    public int Difficulty;
    public int WeaponPrefCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public ushort[] WeaponPref;
}
