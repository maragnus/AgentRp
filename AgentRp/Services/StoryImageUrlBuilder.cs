namespace AgentRp.Services;

public static class StoryImageUrlBuilder
{
    public static string Build(Guid imageId) => $"/story-images/{imageId}";
}
