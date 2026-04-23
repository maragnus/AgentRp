using System.Text;

namespace AgentRp.Services;

internal static class StorySceneResponderSelectionPromptBuilder
{
    internal static string BuildSystemPrompt() =>
        """
        You choose who in the current scene should respond next in an automatic story chat.
        Return only structured data.

        Choose exactly one responder from the provided candidate list.
        Never choose the active speaker.
        Never choose the narrator.
        Never choose someone who is not currently present in the scene.

        Pick the responder who creates the most interesting immediate next turn for this exact moment.
        Favor the character with the strongest local reason, pressure, opportunity, or emotional stake to answer now.
        """;

    internal static string BuildUserPrompt(
        StorySceneActorContext activeSpeaker,
        IReadOnlyList<StorySceneCharacterContext> candidates,
        StoryNarrativeSettingsView storyContext,
        StorySceneLocationContext? currentLocation,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceSnapshot,
        StorySceneAppearanceResolution appearance,
        string? guidancePrompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Active speaker:** {activeSpeaker.Name}");
        builder.AppendLine($"- This speaker must not be selected as the responder.");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(guidancePrompt))
        {
            builder.AppendLine($"**Guidance:** {guidancePrompt.Trim()}");
            builder.AppendLine();
        }
        builder.AppendLine("**Eligible responders:**");
        foreach (var candidate in candidates)
        {
            builder.AppendLine($"- {candidate.Name}: {StorySceneSharedPromptBuilder.PromptInlineText(candidate.Summary)} | Current appearance: {StorySceneSharedPromptBuilder.PromptInlineText(candidate.CurrentAppearance, "None")}");
        }
        builder.AppendLine();
        if (currentLocation is not null)
            builder.AppendLine($"**Location:** {StorySceneSharedPromptBuilder.PromptInlineText(currentLocation.Name)}").AppendLine();
        StorySceneSharedPromptBuilder.AppendStoryContext(builder, storyContext);
        StorySceneSharedPromptBuilder.AppendContentGuidance(builder, storyContext);
        builder.AppendLine("**Recent transcript:**");
        if (transcriptSinceSnapshot.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in transcriptSinceSnapshot)
                builder.AppendLine($"- {message.SpeakerName}: {StorySceneSharedPromptBuilder.PromptInlineText(message.Content, "None")}");
        }

        builder.AppendLine();
        builder.AppendLine("**Current appearance state:**");
        builder.AppendLine(StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance));
        builder.AppendLine();
        builder.AppendLine("Choose one name from the eligible responders list and explain briefly why they should answer next right now.");
        return builder.ToString().TrimEnd();
    }
}
