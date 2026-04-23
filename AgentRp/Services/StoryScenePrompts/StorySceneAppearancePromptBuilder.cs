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
        You resolve current in-scene character scene state from a story transcript.

        Return structured output only.

        Your job is to produce a current snapshot for each character in the scene.
        This is not a history, not a recap, and not a list of changes.

        Scene state means what is true and visible now for each character.
        It may include:
        - clothing or lack of clothing
        - visible physical state such as sweaty, overheated, cold, tense, trembling, injured, exhausted, wet
        - body position or posture
        - body language or facial expression, only if still true now
        - where they are relative to the room, furniture, objects, or other characters
        - what they are currently touching, holding, lying on, under, against, facing, blocking, or interacting with

        Evidence:
        - Use only the transcript
        - Use the provided prior scene state as fallback only when it still appears true
        - Do not use general character description

        Resolution rules:
        - Resolve each character to the best supported current state
        - Prefer newer evidence over older evidence
        - Replace outdated prior details with newer supported details
        - Do not merge old and new details into a running description
        - Do not describe how a character got into their current position
        - Do not include intermediate actions unless they are still true now
        - If a prior detail is no longer clearly true, leave it out
        - Minimal supported details still count
        - Relative position and interaction with objects or other characters count as scene state
        - Lack of clothing counts as scene state when supported

        Output rules:
        - Return one result for every character currently in the scene
        - Set hasCurrentSceneState to true only when at least one specific current detail is supported
        - If no specific current detail is supported, set hasCurrentSceneState to false and set currentSceneState to an empty string
        - currentSceneState must describe only the character's present state
        - Write currentSceneState as a compact snapshot, not a sequence of actions
        - Prefer present-state phrases over action narration
        - Do not include motivations, interpretation, future actions, or unsupported assumptions
        - Do not drop details just to be brief

        The summary must mention only characters with hasCurrentSceneState true.
        The summary must describe each character as they appear now.
        Respect the supplied explicit-content and violent-content guidance.
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
