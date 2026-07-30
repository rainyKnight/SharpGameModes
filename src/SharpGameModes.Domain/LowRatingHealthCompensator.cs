namespace SharpGameModes.Domain;

public sealed record LowRatingHealthPolicy(
    bool Enabled = true,
    double TargetRating = 1.0,
    int MaxHealth = 1000,
    double LearningRate = 0.35,
    double RatingEmaAlpha = 0.3,
    int MinimumRounds = 8,
    double RatingErrorDeadband = 0.1,
    double MaxHealthAdjustmentRatio = 0.1)
{
    public void Validate()
    {
        if (!double.IsFinite(TargetRating)
            || TargetRating <= 0
            || MaxHealth < 100
            || !double.IsFinite(LearningRate)
            || LearningRate is < 0 or > 2
            || !double.IsFinite(RatingEmaAlpha)
            || RatingEmaAlpha is < 0 or > 1
            || MinimumRounds < 1
            || !double.IsFinite(RatingErrorDeadband)
            || RatingErrorDeadband < 0
            || !double.IsFinite(MaxHealthAdjustmentRatio)
            || MaxHealthAdjustmentRatio is < 0 or > 1)
        {
            throw new ArgumentException("Low-rating health policy is invalid.", nameof(LowRatingHealthPolicy));
        }
    }
}

public sealed record HealthCompensationState(
    double BaseRating,
    bool BaseRatingOverridden,
    int CurrentHealth,
    double EffectiveRatingEma,
    int SampleCount,
    double LastMatchRating);

public sealed record HealthAssignment(
    double BaseRating,
    bool BaseRatingOverridden,
    int Health,
    bool ResetState);

public sealed record HealthAssignmentDecision(
    int Health,
    double DisplayRating,
    HealthAssignment? Assignment,
    HealthCompensationState? State,
    bool StateChanged);

public sealed record HealthFeedbackDecision(
    HealthCompensationState? State,
    bool Removed,
    bool Changed);

public static class LowRatingHealthCompensator
{
    private const double MinimumUsableRating = 0.1;

    public static HealthAssignmentDecision Assign(
        double rating,
        bool ratingOverridden,
        HealthCompensationState? savedState,
        LowRatingHealthPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var stateMatchesRatingSource = savedState is not null
            && savedState.BaseRatingOverridden == ratingOverridden
            && (!ratingOverridden || Math.Abs(savedState.BaseRating - rating) <= 0.0001);
        var shouldUseAdaptiveCompensation = policy.Enabled
            && ((stateMatchesRatingSource && savedState!.BaseRating < policy.TargetRating)
                || rating < policy.TargetRating);
        var stateChanged = false;

        if (policy.Enabled
            && savedState is not null
            && !shouldUseAdaptiveCompensation
            && (!stateMatchesRatingSource || savedState.BaseRating >= policy.TargetRating))
        {
            savedState = null;
            stateChanged = true;
        }

        if (shouldUseAdaptiveCompensation)
        {
            var resetState = savedState is not null && !stateMatchesRatingSource;
            if (resetState)
            {
                savedState = null;
                stateChanged = true;
            }

            var baseRating = savedState?.BaseRating ?? rating;
            var health = savedState is null
                ? Math.Clamp(
                    RoundHealth(100.0 * policy.TargetRating / NormalizeRating(baseRating)),
                    100,
                    policy.MaxHealth)
                : Math.Clamp(savedState.CurrentHealth, 100, policy.MaxHealth);
            var state = savedState is null
                ? new HealthCompensationState(baseRating, ratingOverridden, health, 0, 0, 0)
                : savedState with { CurrentHealth = health };
            stateChanged |= savedState is null || savedState.CurrentHealth != health;

            return new HealthAssignmentDecision(
                health,
                baseRating,
                new HealthAssignment(baseRating, ratingOverridden, health, resetState),
                state,
                stateChanged);
        }

        var staticHealth = rating >= policy.TargetRating
            ? 100
            : Math.Max(1, RoundHealth(100.0 / NormalizeRating(rating)));
        return new HealthAssignmentDecision(staticHealth, rating, null, savedState, stateChanged);
    }

    public static HealthFeedbackDecision ApplyFeedback(
        HealthAssignment assignment,
        HealthCompensationState? state,
        double matchRating,
        int roundsPlayed,
        LowRatingHealthPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!double.IsFinite(matchRating) || matchRating < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(matchRating));
        }

        if (roundsPlayed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundsPlayed));
        }

        state = state is null || assignment.ResetState
            ? new HealthCompensationState(
                assignment.BaseRating,
                assignment.BaseRatingOverridden,
                assignment.Health,
                0,
                0,
                0)
            : state;

        if (roundsPlayed < policy.MinimumRounds)
        {
            return new HealthFeedbackDecision(
                state with { LastMatchRating = matchRating },
                Removed: false,
                Changed: true);
        }

        var effectiveRatingEma = state.SampleCount == 0
            ? matchRating
            : state.EffectiveRatingEma * (1.0 - policy.RatingEmaAlpha)
                + matchRating * policy.RatingEmaAlpha;
        var error = policy.TargetRating - effectiveRatingEma;
        var nextHealth = assignment.Health;
        if (Math.Abs(error) > policy.RatingErrorDeadband)
        {
            var adjustmentFactor = Math.Exp(policy.LearningRate * error);
            var minimumAdjustedHealth = Math.Max(
                100,
                (int)Math.Ceiling(assignment.Health * (1.0 - policy.MaxHealthAdjustmentRatio)));
            var maximumAdjustedHealth = Math.Min(
                policy.MaxHealth,
                (int)Math.Floor(assignment.Health * (1.0 + policy.MaxHealthAdjustmentRatio)));
            nextHealth = Math.Clamp(
                RoundHealth(assignment.Health * adjustmentFactor),
                minimumAdjustedHealth,
                maximumAdjustedHealth);
        }

        if (nextHealth == 100 && effectiveRatingEma >= policy.TargetRating)
        {
            return new HealthFeedbackDecision(null, Removed: true, Changed: true);
        }

        return new HealthFeedbackDecision(
            state with
            {
                CurrentHealth = nextHealth,
                EffectiveRatingEma = effectiveRatingEma,
                SampleCount = state.SampleCount + 1,
                LastMatchRating = matchRating,
            },
            Removed: false,
            Changed: true);
    }

    public static HealthCompensationState NormalizeState(
        HealthCompensationState state,
        LowRatingHealthPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!double.IsFinite(state.BaseRating) || state.BaseRating >= policy.TargetRating)
        {
            throw new ArgumentException("Health compensation state has an invalid base rating.", nameof(state));
        }

        return state with
        {
            CurrentHealth = Math.Clamp(state.CurrentHealth, 100, policy.MaxHealth),
            EffectiveRatingEma = double.IsFinite(state.EffectiveRatingEma) ? state.EffectiveRatingEma : 0,
            SampleCount = Math.Max(0, state.SampleCount),
            LastMatchRating = double.IsFinite(state.LastMatchRating) ? state.LastMatchRating : 0,
        };
    }

    private static double NormalizeRating(double rating)
        => double.IsFinite(rating) && rating > 0 ? rating : MinimumUsableRating;

    private static int RoundHealth(double health)
    {
        if (!double.IsFinite(health) || health >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (health <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)Math.Round(health, MidpointRounding.AwayFromZero);
    }
}
