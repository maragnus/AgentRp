using Microsoft.Extensions.AI;

namespace AgentRp.Services;

internal static class StoryMessageTokenUsageMapper
{
    public static StoryMessageTokenUsage? Map(UsageDetails? usage)
    {
        if (usage is null)
            return null;

        if (!usage.InputTokenCount.HasValue
            && !usage.OutputTokenCount.HasValue
            && !usage.TotalTokenCount.HasValue)
            return null;

        return new StoryMessageTokenUsage(
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount);
    }
}
