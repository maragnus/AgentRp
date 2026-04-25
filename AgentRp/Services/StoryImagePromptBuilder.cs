namespace AgentRp.Services;

public static class StoryImagePromptBuilder
{
    public const string LandscapeSize = "1536x1024";
    public const string PortraitSize = "1024x1536";

    public static string BuildReferencePrompt(StoryEntityKind entityKind, string entityName)
    {
        var displayName = string.IsNullOrWhiteSpace(entityName) ? entityKind.ToString() : entityName.Trim();
        var subject = entityKind switch
        {
            StoryEntityKind.Character => "profile image",
            StoryEntityKind.Location => "scene",
            StoryEntityKind.Item => "image",
            _ => "image"
        };

        return $"Create a vivid roleplaying reference {subject} for {displayName}.";
    }

    public static string BuildReferenceSize(StoryEntityKind entityKind) => entityKind switch
    {
        StoryEntityKind.Character => PortraitSize,
        StoryEntityKind.Location => LandscapeSize,
        StoryEntityKind.Item => LandscapeSize,
        _ => LandscapeSize
    };
}
