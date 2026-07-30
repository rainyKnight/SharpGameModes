using Google.Protobuf;
using SharpGameModes.BotMatch;

namespace SharpGameModes.BotMatch.Tests;

public sealed class BotUserInfoPolicyTests
{
    [Fact]
    public void RewriteFakePlayer_ChangesOnlyTargetFlagSemantics()
    {
        var source = new CMsgPlayerInfo
        {
            Name = "SharpGameModes Bot",
            Xuid = 76561198000000001UL,
            Userid = 42,
            Steamid = 76561198000000002UL,
            Fakeplayer = true,
            Ishltv = false,
        };

        var result = BotUserInfoPolicy.RewriteFakePlayer(
            source.ToByteArray(),
            expectedUserId: 42,
            fakePlayer: false,
            out var rewritten);

        Assert.Equal(BotUserInfoRewriteResult.Rewritten, result);
        var actual = CMsgPlayerInfo.Parser.ParseFrom(rewritten);
        Assert.Equal(source.Name, actual.Name);
        Assert.Equal(source.Xuid, actual.Xuid);
        Assert.Equal(source.Userid, actual.Userid);
        Assert.Equal(source.Steamid, actual.Steamid);
        Assert.False(actual.Fakeplayer);
        Assert.Equal(source.Ishltv, actual.Ishltv);
    }

    [Fact]
    public void RewriteFakePlayer_DoesNotRewriteAlreadyDesiredPayload()
    {
        var source = new CMsgPlayerInfo
        {
            Userid = 7,
            Fakeplayer = false,
        };

        var result = BotUserInfoPolicy.RewriteFakePlayer(
            source.ToByteArray(),
            expectedUserId: 7,
            fakePlayer: false,
            out var rewritten);

        Assert.Equal(BotUserInfoRewriteResult.AlreadyDesired, result);
        Assert.Empty(rewritten);
    }

    [Fact]
    public void RewriteFakePlayer_RejectsAnotherClient()
    {
        var source = new CMsgPlayerInfo
        {
            Userid = 8,
            Fakeplayer = true,
        };

        var result = BotUserInfoPolicy.RewriteFakePlayer(
            source.ToByteArray(),
            expectedUserId: 9,
            fakePlayer: false,
            out var rewritten);

        Assert.Equal(BotUserInfoRewriteResult.NotTarget, result);
        Assert.Empty(rewritten);
    }

    [Fact]
    public void RewriteFakePlayer_RejectsMalformedPayload()
    {
        var result = BotUserInfoPolicy.RewriteFakePlayer(
            [0x0A, 0x05, 0x41],
            expectedUserId: 1,
            fakePlayer: false,
            out var rewritten);

        Assert.Equal(BotUserInfoRewriteResult.Invalid, result);
        Assert.Empty(rewritten);
    }
}
