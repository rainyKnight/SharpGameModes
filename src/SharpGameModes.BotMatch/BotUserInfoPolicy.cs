using Google.Protobuf;

namespace SharpGameModes.BotMatch;

internal enum BotUserInfoRewriteResult
{
    Invalid,
    NotTarget,
    AlreadyDesired,
    Rewritten,
}

internal static class BotUserInfoPolicy
{
    public static BotUserInfoRewriteResult RewriteFakePlayer(
        ReadOnlySpan<byte> payload,
        int expectedUserId,
        bool fakePlayer,
        out byte[] rewritten)
    {
        rewritten = [];
        if (payload.IsEmpty)
        {
            return BotUserInfoRewriteResult.Invalid;
        }

        CMsgPlayerInfo playerInfo;
        try
        {
            playerInfo = CMsgPlayerInfo.Parser.ParseFrom(payload.ToArray());
        }
        catch (InvalidProtocolBufferException)
        {
            return BotUserInfoRewriteResult.Invalid;
        }

        if (!playerInfo.HasUserid || playerInfo.Userid != expectedUserId)
        {
            return BotUserInfoRewriteResult.NotTarget;
        }

        if (playerInfo.Fakeplayer == fakePlayer)
        {
            return BotUserInfoRewriteResult.AlreadyDesired;
        }

        playerInfo.Fakeplayer = fakePlayer;
        rewritten = playerInfo.ToByteArray();
        return BotUserInfoRewriteResult.Rewritten;
    }
}
