using System.Text;
using AgentRp.Data;

namespace AgentRp.Services;

public enum StorySceneContextSection
{
    Actor,
    Location,
    CharactersInScene,
    OtherKnownCharacters,
    ObjectsInScene,
    StoryContext,
    ContentGuidance,
    HistorySummary,
    Snapshot,
    Transcript,
    EarlierPrivateIntentContinuity,
    CharacterAppearances
}

public static class StorySceneContextSectionDefaults
{
    public static IReadOnlyList<StorySceneContextSection> OrderedSections { get; } =
    [
        StorySceneContextSection.Actor,
        StorySceneContextSection.Location,
        StorySceneContextSection.CharactersInScene,
        StorySceneContextSection.OtherKnownCharacters,
        StorySceneContextSection.ObjectsInScene,
        StorySceneContextSection.StoryContext,
        StorySceneContextSection.ContentGuidance,
        StorySceneContextSection.HistorySummary,
        StorySceneContextSection.Snapshot,
        StorySceneContextSection.Transcript,
        StorySceneContextSection.EarlierPrivateIntentContinuity,
        StorySceneContextSection.CharacterAppearances
    ];
}

public static class StorySceneSharedPromptBuilder
{
    public static string BuildContextSummary(StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        foreach (var section in StorySceneContextSectionDefaults.OrderedSections)
            AppendContextSection(builder, context, section);

        return builder.ToString().TrimEnd();
    }

    public static string BuildContextSection(StorySceneGenerationContext context, StorySceneContextSection section)
    {
        var builder = new StringBuilder();
        AppendContextSection(builder, context, section);
        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyDictionary<StorySceneContextSection, string> BuildContextSections(StorySceneGenerationContext context) =>
        StorySceneContextSectionDefaults.OrderedSections.ToDictionary(
            section => section,
            section => BuildContextSection(context, section));

    public static void AppendContextSection(StringBuilder builder, StorySceneGenerationContext context, StorySceneContextSection section)
    {
        switch (section)
        {
            case StorySceneContextSection.Actor:
                AppendActorContext(builder, context);
                break;
            case StorySceneContextSection.Location:
                AppendLocationContext(builder, context);
                break;
            case StorySceneContextSection.CharactersInScene:
                AppendCharactersInSceneContext(builder, context);
                break;
            case StorySceneContextSection.OtherKnownCharacters:
                AppendOtherKnownCharactersContext(builder, context);
                break;
            case StorySceneContextSection.ObjectsInScene:
                AppendObjectsInSceneContext(builder, context);
                break;
            case StorySceneContextSection.StoryContext:
                AppendStoryContext(builder, context.StoryContext);
                break;
            case StorySceneContextSection.ContentGuidance:
                AppendContentGuidance(builder, context.StoryContext);
                break;
            case StorySceneContextSection.HistorySummary:
                AppendHistorySummaryContext(builder, context);
                break;
            case StorySceneContextSection.Snapshot:
                AppendSnapshotContext(builder, context);
                break;
            case StorySceneContextSection.Transcript:
                AppendTranscriptContext(builder, context);
                break;
            case StorySceneContextSection.EarlierPrivateIntentContinuity:
                AppendEarlierPrivateIntentContinuityContext(builder, context);
                break;
            case StorySceneContextSection.CharacterAppearances:
                AppendCharacterAppearancesContext(builder, context);
                break;
            default:
                throw new InvalidOperationException($"Building the scene context prompt failed because context section '{section}' is not supported.");
        }
    }

    private static void AppendActorContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        builder.AppendLine($"**Actor:** {context.Actor.Name}");
        builder.AppendLine($"- Summary: {PromptInlineText(context.Actor.Summary)}");
        if (context.Actor.IsNarrator)
            builder.AppendLine($"- Narrator guidance: {PromptInlineText(context.Actor.NarratorGuidance, "None")}");
        else
        {
            builder.AppendLine($"- Appearance: {PromptInlineText(context.Actor.Appearance, "None")}");
            builder.AppendLine($"- Voice: {PromptInlineText(context.Actor.Voice, "None")}");
            builder.AppendLine($"- Hides: {PromptInlineText(context.Actor.Hides, "None")}");
            builder.AppendLine($"- Tendency: {PromptInlineText(context.Actor.Tendency, "None")}");
            builder.AppendLine($"- Constraint: {PromptInlineText(context.Actor.Constraint, "None")}");
            builder.AppendLine($"- Relationships: {PromptInlineText(context.Actor.Relationships, "None")}");
            builder.AppendLine($"- Likes / beliefs: {PromptInlineText(context.Actor.LikesBeliefs, "None")}");
            builder.AppendLine($"- Private motivations: {PromptInlineText(context.Actor.PrivateMotivations, "None")}");
        }

        if (!string.IsNullOrWhiteSpace(context.Actor.HiddenKnowledge))
            builder.AppendLine($"- Hidden knowledge: {PromptInlineText(context.Actor.HiddenKnowledge)}");

        builder.AppendLine();
    }

    private static void AppendLocationContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        if (context.CurrentLocation is not null)
        {
            builder.AppendLine($"**Location:** {PromptInlineText(context.CurrentLocation.Name)}");
            if (!string.IsNullOrWhiteSpace(context.CurrentLocation.Summary))
                builder.AppendLine($"- Summary: {PromptInlineText(context.CurrentLocation.Summary)}");
            if (!string.IsNullOrWhiteSpace(context.CurrentLocation.Details))
                builder.AppendLine($"- Details: {PromptInlineText(context.CurrentLocation.Details)}");
            builder.AppendLine();
        }
    }

    private static void AppendCharactersInSceneContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        var nonActorCharacters = context.Actor.CharacterId.HasValue
            ? context.Characters.Where(x => x.CharacterId != context.Actor.CharacterId.Value).ToList()
            : context.Characters.ToList();
        var sceneCharacters = nonActorCharacters.Where(x => x.IsPresentInScene).ToList();
        if (sceneCharacters.Count > 0)
        {
            builder.AppendLine("**Characters in the scene:**")
                .AppendLine($"- **{context.Actor.Name}:** current actor");
            foreach (var character in sceneCharacters)
                builder.AppendLine($"- **{character.Name}:** {PromptInlineText(character.Summary)} | Appearance: {PromptInlineText(character.Appearance, "None")} | Voice: {PromptInlineText(character.Voice, "None")} | Relationships: {PromptInlineText(character.Relationships, "None")}");
            builder.AppendLine();
        }
    }

    private static void AppendOtherKnownCharactersContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        var nonActorCharacters = context.Actor.CharacterId.HasValue
            ? context.Characters.Where(x => x.CharacterId != context.Actor.CharacterId.Value).ToList()
            : context.Characters.ToList();
        var otherCharacters = nonActorCharacters.Where(x => !x.IsPresentInScene).ToList();
        if (otherCharacters.Count > 0)
        {
            builder.AppendLine($"**Other known characters:** {string.Join(", ", otherCharacters.Select(x => x.Name))}");
            builder.AppendLine();
        }
    }

    private static void AppendObjectsInSceneContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        if (context.SceneObjects.Count > 0)
        {
            builder.AppendLine("**Objects in the scene:**");
            foreach (var item in context.SceneObjects)
                builder.AppendLine($"- {item.Name} | {PromptInlineText(item.Summary)} | Details: {PromptInlineText(item.Details, "None")}");
            builder.AppendLine();
        }
    }

    private static void AppendHistorySummaryContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        if (!string.IsNullOrEmpty(context.HistorySummary))
            builder.AppendLine($"**History summary:** {PromptInlineText(context.HistorySummary)}");
    }

    private static void AppendSnapshotContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        if (!string.IsNullOrEmpty(context.LatestSnapshot?.Summary))
            builder.AppendLine($"**Snapshot:** {PromptInlineText(context.LatestSnapshot.Summary)}");
    }

    private static void AppendTranscriptContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        builder.AppendLine("**Transcript:**");
        if (context.TranscriptSinceSnapshot.Count > 0)
        {
            foreach (var message in context.TranscriptSinceSnapshot)
            {
                builder.AppendLine($"- {message.SpeakerName}: {PromptInlineText(message.Content, "None")}");
                if (!string.IsNullOrWhiteSpace(message.PrivateIntent))
                    builder.AppendLine($"  Private Intent: {PromptInlineText(message.PrivateIntent)}");
            }
        }
        else
        {
            builder.AppendLine("- None");
        }
        builder.AppendLine();
    }

    private static void AppendEarlierPrivateIntentContinuityContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        var transcriptMessageIds = context.TranscriptSinceSnapshot.Select(x => x.MessageId).ToHashSet();
        var earlierPrivateIntentMessages = (context.PrivateIntentTranscript ?? [])
            .Where(x => !transcriptMessageIds.Contains(x.MessageId))
            .Where(x => !string.IsNullOrWhiteSpace(x.PrivateIntent))
            .ToList();
        if (earlierPrivateIntentMessages.Count > 0)
        {
            builder.AppendLine("**Earlier private intent continuity:**");
            foreach (var message in earlierPrivateIntentMessages)
                builder.AppendLine($"- {message.SpeakerName}: Private Intent: {PromptInlineText(message.PrivateIntent)}");
            builder.AppendLine();
        }
    }

    private static void AppendCharacterAppearancesContext(StringBuilder builder, StorySceneGenerationContext context)
    {
        var currentAppearanceCharacters = context.Characters
            .Where(x => x.IsPresentInScene && !string.IsNullOrWhiteSpace(x.CurrentAppearance))
            .ToList();
        if (currentAppearanceCharacters.Count > 0)
        {
            builder.AppendLine("**Character appearances:**");
            foreach (var character in currentAppearanceCharacters)
                builder.AppendLine($"- {character.Name}: {PromptInlineText(character.CurrentAppearance, "None")}");
        }
    }

    internal static string BuildPlannerDetail(StoryMessagePlannerResult planner)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Turn shape:** {FormatTurnShape(planner.TurnShape)}");
        builder.AppendLine($"**Beat:** {planner.Beat}");
        builder.AppendLine($"**Intent:** {planner.Intent}");
        builder.AppendLine($"**Immediate goal:** {planner.ImmediateGoal}");
        builder.AppendLine($"**Why now:** {planner.WhyNow}");
        builder.AppendLine($"**Change introduced:** {planner.ChangeIntroduced}");
        builder.AppendLine($"**Private Intent:** {PromptInlineText(planner.PrivateIntent, "None")}");
        builder.AppendLine($"**Narrative Guardrails:** {FormatList(planner.NarrativeGuardrails)}");
        return builder.ToString().TrimEnd();
    }

    internal static string BuildAppearanceDetail(StorySceneAppearanceResolution appearance)
    {
        var builder = new StringBuilder();
        if (appearance.EffectiveCharacters.Count == 0)
        {
            builder.AppendLine("No current appearance or physical state details have been captured for this scene yet.");
        }
        else
        {
            builder.AppendLine($"**Latest appearance block:** {appearance.LatestEntry?.Summary ?? "None"}");
            builder.AppendLine("**Current appearances:**");
            foreach (var character in appearance.EffectiveCharacters)
                builder.AppendLine($"- {character.CharacterName}: {PromptInlineText(character.CurrentAppearance, "None captured yet")}");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string BuildAppearanceContextSummary(
        StoryNarrativeSettingsView storyContext,
        IReadOnlyList<StorySceneCharacterContext> characters,
        StorySceneAppearanceResolution appearance)
    {
        var builder = new StringBuilder();
        AppendContentGuidance(builder, storyContext);
        builder.AppendLine("Characters currently in the scene:");
        foreach (var character in characters.Where(x => x.IsPresentInScene))
            builder.AppendLine($"- {character.Name} | Appearance: {PromptInlineText(character.Appearance, "None")} | Prior current appearance: {PromptInlineText(character.CurrentAppearance, "None")}");

        builder.AppendLine("Transcript:");
        if (appearance.TranscriptSinceLatestEntry.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in appearance.TranscriptSinceLatestEntry)
                builder.AppendLine($"- {message.SpeakerName}: {PromptInlineText(message.Content, "None")}");
        }

        return builder.ToString().TrimEnd();
    }

    public static void AppendStoryContext(StringBuilder builder, StoryNarrativeSettingsView? storyContext)
    {
        var context = storyContext ?? CreateDefaultStoryContext();
        var hasGenre = !string.IsNullOrWhiteSpace(context.Genre);
        var hasSetting = !string.IsNullOrWhiteSpace(context.Setting);
        var hasTone = !string.IsNullOrWhiteSpace(context.Tone);
        var hasDirection = !string.IsNullOrWhiteSpace(context.StoryDirection);

        if (!hasGenre && !hasSetting && !hasTone && !hasDirection)
            return;

        builder.AppendLine("**Story context:**");
        if (hasGenre)
            builder.AppendLine($"- Genre: {PromptInlineText(context.Genre)}");
        if (hasSetting)
            builder.AppendLine($"- Setting: {PromptInlineText(context.Setting)}");
        if (hasTone)
            builder.AppendLine($"- Tone: {PromptInlineText(context.Tone)}");
        if (hasDirection)
            builder.AppendLine($"- Story premise / direction: {PromptInlineText(context.StoryDirection)}");
        builder.AppendLine();
    }

    public static void AppendContentGuidance(StringBuilder builder, StoryNarrativeSettingsView? storyContext)
    {
        var context = storyContext ?? CreateDefaultStoryContext();
        builder.AppendLine("**Content guidance:**");
        builder.AppendLine($"- Explicit content: {FormatContentIntensity(context.ExplicitContent)}");
        builder.AppendLine($"- Violent content: {FormatContentIntensity(context.ViolentContent)}");
        builder.AppendLine();
    }

    public static string FormatContentIntensity(StoryContentIntensity intensity) => intensity switch
    {
        StoryContentIntensity.Forbidden => "Forbidden. Do not introduce or describe this content.",
        StoryContentIntensity.Encouraged => "Encouraged when supported and scene-relevant. Lean into it without inventing it.",
        _ => "Allowed when naturally supported by the scene."
    };

    public static string FormatContentIntensityLabel(StoryContentIntensity intensity) => intensity switch
    {
        StoryContentIntensity.Forbidden => "Forbidden",
        StoryContentIntensity.Encouraged => "Encouraged",
        _ => "Allowed"
    };

    public static string FormatTurnShape(StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact => "compact",
        StoryTurnShape.Brief => "brief",
        StoryTurnShape.Extended => "extended",
        StoryTurnShape.Monologue => "monologue",
        StoryTurnShape.Silent => "silent",
        StoryTurnShape.SilentMonologue => "silent monologue",
        _ => turnShape.ToString().ToLowerInvariant()
    };

    public static string PromptInlineText(string? value, string fallback = "Unknown") =>
        string.IsNullOrWhiteSpace(value) ? fallback : CollapseWhitespace(value);

    public static string TrimInlineText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatList(IReadOnlyList<string> values) => values.Count == 0 ? "None" : string.Join("; ", values);

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static StoryNarrativeSettingsView CreateDefaultStoryContext() => new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        StoryContentIntensity.Allowed,
        StoryContentIntensity.Allowed);
}
