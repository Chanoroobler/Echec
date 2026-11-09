using ChessArmy.Core.Pvp;
using Xunit;

namespace ChessArmy.Core.Tests;

public class PvpBotFallbackTests
{
    [Fact]
    public void DefaultDelayIsTenSeconds() => Assert.Equal(10f, new PvpConfig().BotAfterSeconds);

    [Fact]
    public void DelayIsFortySecondsWhenOtherPlayersAreOnline()
    {
        var config = new PvpConfig();
        Assert.Equal(10f, config.BotDelay(othersOnline: false));
        Assert.Equal(40f, config.BotDelay(othersOnline: true));
        Assert.False(PvpBotFallback.ShouldUseBot(39f, config.BotDelay(true), failed: false, hasOpponent: false, joining: false));
        Assert.True(PvpBotFallback.ShouldUseBot(40f, config.BotDelay(true), failed: false, hasOpponent: false, joining: false));
    }

    [Fact]
    public void NoBotBeforeTheDelay() =>
        Assert.False(PvpBotFallback.ShouldUseBot(9.9f, 10f, failed: false, hasOpponent: false, joining: false));

    [Fact]
    public void BotOnceTheDelayIsReached() =>
        Assert.True(PvpBotFallback.ShouldUseBot(10f, 10f, failed: false, hasOpponent: false, joining: false));

    [Fact]
    public void SteamFailureGivesABotAtOnce() =>
        Assert.True(PvpBotFallback.ShouldUseBot(0f, 10f, failed: true, hasOpponent: false, joining: false));

    [Fact]
    public void HumanOpponentAlwaysWinsTheRace()
    {
        Assert.False(PvpBotFallback.ShouldUseBot(10f, 10f, failed: false, hasOpponent: true, joining: false));
        Assert.False(PvpBotFallback.ShouldUseBot(60f, 10f, failed: true, hasOpponent: true, joining: false));
    }

    [Fact]
    public void JoinInFlightGetsAGraceThenTheBotTakesOver()
    {
        var grace = PvpBotFallback.JoinGraceSeconds;
        Assert.False(PvpBotFallback.ShouldUseBot(10f, 10f, failed: false, hasOpponent: false, joining: true));
        Assert.False(PvpBotFallback.ShouldUseBot(10f + grace - 0.1f, 10f, failed: false, hasOpponent: false, joining: true));
        Assert.True(PvpBotFallback.ShouldUseBot(10f + grace, 10f, failed: false, hasOpponent: false, joining: true));
    }

    [Theory]
    [InlineData(0f, 10)]
    [InlineData(0.1f, 10)]
    [InlineData(2.4f, 8)]
    [InlineData(9.5f, 1)]
    [InlineData(10f, 0)]
    [InlineData(12f, 0)]
    public void CountdownRoundsUpAndNeverGoesNegative(float searchTime, int expected) =>
        Assert.Equal(expected, PvpBotFallback.SecondsLeft(searchTime, 10f));
}
