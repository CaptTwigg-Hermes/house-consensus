namespace HouseConsensus.Shared;

public enum ScoreStatus
{
    Complete,
    Incomplete,
    NeedsReview,
    NotScored
}

public static class ScoreStatusRules
{
    public static ScoreStatus Resolve(
        double? score,
        bool aiAssessed,
        double? coveragePercent,
        bool? familyPrivacyAvailable,
        bool hasCompleteBreakdown,
        string? scoreRuleVersion)
    {
        if (!score.HasValue)
            return aiAssessed ? ScoreStatus.Incomplete : ScoreStatus.NotScored;

        if (!coveragePercent.HasValue || !familyPrivacyAvailable.HasValue
            || string.IsNullOrWhiteSpace(scoreRuleVersion))
            return ScoreStatus.NeedsReview;

        if (coveragePercent.Value < 100 || familyPrivacyAvailable is false || !hasCompleteBreakdown)
            return ScoreStatus.Incomplete;

        return ScoreStatus.Complete;
    }
}
