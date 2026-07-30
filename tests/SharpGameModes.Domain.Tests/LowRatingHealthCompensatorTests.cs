using SharpGameModes.Domain;
using System.Text.Json;

namespace SharpGameModes.Domain.Tests;

public sealed class LowRatingHealthCompensatorTests
{
    private static readonly LowRatingHealthPolicy Policy = new();

    [Fact]
    public void Assign_CreatesLegacyInitialHealthAndState()
    {
        var decision = LowRatingHealthCompensator.Assign(0.2, false, null, Policy);

        Assert.Equal(500, decision.Health);
        Assert.Equal(0.2, decision.DisplayRating);
        Assert.NotNull(decision.Assignment);
        Assert.Equal(500, decision.State?.CurrentHealth);
        Assert.True(decision.StateChanged);
    }

    [Fact]
    public void Assign_ReusesNonOverrideStateWhenAggregateRatingChanges()
    {
        var state = new HealthCompensationState(0.2, false, 500, 0.8, 2, 0.9);

        var decision = LowRatingHealthCompensator.Assign(0.4, false, state, Policy);

        Assert.Equal(500, decision.Health);
        Assert.Equal(0.2, decision.DisplayRating);
        Assert.Equal(state, decision.State);
        Assert.False(decision.StateChanged);
    }

    [Fact]
    public void Assign_ResetsStateWhenOverrideValueChanges()
    {
        var state = new HealthCompensationState(0.2, true, 500, 0.8, 2, 0.9);

        var decision = LowRatingHealthCompensator.Assign(0.4, true, state, Policy);

        Assert.Equal(250, decision.Health);
        Assert.True(decision.Assignment?.ResetState);
        Assert.Equal(0.4, decision.State?.BaseRating);
    }

    [Fact]
    public void Assign_DisabledPolicyKeepsLegacyStaticCompensation()
    {
        var decision = LowRatingHealthCompensator.Assign(0.5, false, null, Policy with { Enabled = false });

        Assert.Equal(200, decision.Health);
        Assert.Null(decision.Assignment);
    }

    [Theory]
    [InlineData(2.0, 450)]
    [InlineData(0.5, 550)]
    public void ApplyFeedback_LimitsEachHealthChangeToTenPercent(double matchRating, int expectedHealth)
    {
        var assignment = new HealthAssignment(0.2, false, 500, false);
        var state = new HealthCompensationState(0.2, false, 500, 0, 0, 0);

        var decision = LowRatingHealthCompensator.ApplyFeedback(
            assignment,
            state,
            matchRating,
            roundsPlayed: 8,
            Policy);

        Assert.Equal(expectedHealth, decision.State?.CurrentHealth);
        Assert.Equal(1, decision.State?.SampleCount);
    }

    [Fact]
    public void ApplyFeedback_DoesNotLearnFromShortMatch()
    {
        var assignment = new HealthAssignment(0.2, false, 500, false);
        var state = new HealthCompensationState(0.2, false, 500, 0, 0, 0);

        var decision = LowRatingHealthCompensator.ApplyFeedback(
            assignment,
            state,
            matchRating: 1.2,
            roundsPlayed: 7,
            Policy);

        Assert.Equal(500, decision.State?.CurrentHealth);
        Assert.Equal(0, decision.State?.SampleCount);
        Assert.Equal(1.2, decision.State?.LastMatchRating);
    }

    [Fact]
    public void ApplyFeedback_RemovesCompensationAtHealthyRatingAndOneHundredHealth()
    {
        var assignment = new HealthAssignment(0.9, false, 100, false);
        var state = new HealthCompensationState(0.9, false, 100, 0, 0, 0);

        var decision = LowRatingHealthCompensator.ApplyFeedback(
            assignment,
            state,
            matchRating: 1.1,
            roundsPlayed: 8,
            Policy);

        Assert.True(decision.Removed);
        Assert.Null(decision.State);
    }

    [Fact]
    public void LegacyStateJson_DeserializesWithoutConversion()
    {
        const string json =
            """
            {
              "version": 1,
              "last_updated_utc": "2026-07-11T17:44:23Z",
              "players": {
                "76561198000000030": {
                  "base_rating": 0.2,
                  "base_rating_overridden": false,
                  "current_health": 500,
                  "effective_rating_ema": 0,
                  "sample_count": 0,
                  "last_match_rating": 0
                }
              }
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        var store = JsonSerializer.Deserialize<LegacyStateStore>(json, options);

        var state = Assert.Single(store!.Players).Value;
        Assert.Equal(0.2, state.BaseRating);
        Assert.Equal(500, state.CurrentHealth);
        Assert.False(state.BaseRatingOverridden);
    }

    private sealed class LegacyStateStore
    {
        public int Version { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; }
        public Dictionary<string, HealthCompensationState> Players { get; set; } = [];
    }
}
