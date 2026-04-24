using System.Text;
using AgentRp.Data;

namespace AgentRp.Services;

internal sealed record StorySceneAppearancePromptCharacter(
    string Name,
    string CurrentAppearance);

internal static class StorySceneAppearancePromptBuilder
{
    internal static string BuildSystemPrompt() =>
        """
        You update character scene state.

        Return structured output only.

        Scene state is what is visibly true about each character right now:
        clothing, carried items, body position, location, posture, visible condition, and current physical contact with people or objects.

        Use the prior scene state as the starting point.
        Use the latest transcript to update it.

        Keep stable details from the prior state unless the transcript changes or contradicts them.
        Stable details include clothing, carried items, injuries, location, posture, and physical contact.

        Do not drop outfits or carried items just because they were not mentioned again.

        Temporary details fade unless the latest transcript still supports them.
        Temporary details include facial expressions, brief gestures, glances, momentary touches, and passing reactions.

        For each character:
        - keep unchanged stable details regardless of percieved importance
        - keep details about what is exposed or not being worn
        - add new visible details
        - replace changed details
        - remove contradicted details

        Write only the current snapshot.
        Do not recap actions.
        Do not explain changes.
        Do not include thoughts, motives, memories, or personality.

        Return one result for every character currently in the scene.
        If a character has no supported current scene state, set hasCurrentSceneState to false and currentSceneState to "".

        The summary must mention only characters with hasCurrentSceneState true.
        """;

    internal static string BuildUserPrompt(
        IReadOnlyList<StorySceneAppearancePromptCharacter> characters,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceLatestEntry,
        StoryContentIntensity explicitContent,
        StoryContentIntensity violentContent)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Content guidance:");
        builder.AppendLine($"- Explicit content: {StorySceneSharedPromptBuilder.FormatContentIntensityLabel(explicitContent)}");
        builder.AppendLine($"- Violent content: {StorySceneSharedPromptBuilder.FormatContentIntensityLabel(violentContent)}");
        builder.AppendLine();
        builder.AppendLine("Characters in the scene with initial appearance:");

        foreach (var character in characters)
        {
            builder.Append($"- **{character.Name}:** ");
            builder.AppendLine($"{StorySceneSharedPromptBuilder.TrimInlineText(character.CurrentAppearance, "None")}");
        }

        builder.AppendLine();
        builder.AppendLine("**Transcript:**");
        if (transcriptSinceLatestEntry.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in transcriptSinceLatestEntry)
                builder.AppendLine($"- **{message.SpeakerName}:** {message.Content}");
        }

        builder.AppendLine();
        builder.AppendLine(
            """
            Return one decision for every character currently in the scene.

            For each character, resolve the best supported current scene state from the transcript plus prior current scene state.

            Important:
            - Eagerly replace outdated prior details with newer supported details
            - Do not update by appending history
            - Describe only what is true now
            - Include where the character is relative to other characters, furniture, and objects when supported
            - Include current interaction with sheets, bed, doorway, wall, chair, or other visible scene elements when supported
            - If a prior detail is not reaffirmed and may no longer be true, leave it out
            - Forbidden means do not include that kind of detail
            - Allowed means include it only when naturally supported
            - Encouraged means prefer supported detail over softening it, but never invent it
            """);
        return builder.ToString().TrimEnd();
    }
}
