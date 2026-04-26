using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public enum StoryScenePromptStage
{
    Appearance,
    Selection,
    Planning,
    Prose
}

public sealed record StoryScenePromptLibraryView(
    StorySceneStagePromptTemplateView Appearance,
    StorySceneStagePromptTemplateView Selection,
    StorySceneStagePromptTemplateView Planning,
    StorySceneStagePromptTemplateView Prose,
    StorySceneTurnShapePromptTemplatesView PlanningTurnShapes,
    StorySceneTurnShapePromptTemplatesView ProseTurnShapes,
    IReadOnlyList<StoryScenePromptPlaceholderView> Placeholders);

public sealed record StorySceneStagePromptTemplateView(
    string SystemPrompt,
    string UserPromptTemplate);

public sealed record StorySceneTurnShapePromptTemplatesView(
    string Compact,
    string Brief,
    string Extended,
    string Monologue,
    string Silent,
    string SilentMonologue);

public sealed record StoryScenePromptPlaceholderView(
    string Token,
    string Description,
    IReadOnlyList<StoryScenePromptStage> Stages);

public sealed record UpdateStoryScenePromptLibrary(
    Guid ThreadId,
    StoryScenePromptLibraryView Library);

public sealed record StoryScenePromptRenderResult(
    string SystemPrompt,
    string UserPrompt);

public interface IStoryScenePromptLibraryService
{
    Task<StoryScenePromptLibraryView> GetLibraryAsync(Guid threadId, CancellationToken cancellationToken);

    Task<StoryScenePromptLibraryView> SaveLibraryAsync(UpdateStoryScenePromptLibrary update, CancellationToken cancellationToken);

    StoryScenePromptLibraryView GetDefaultLibrary();

    Task<StoryScenePromptRenderResult> RenderAppearancePromptAsync(
        Guid threadId,
        IReadOnlyList<StorySceneAppearancePromptCharacter> characters,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceLatestEntry,
        StoryContentIntensity explicitContent,
        StoryContentIntensity violentContent,
        CancellationToken cancellationToken);

    Task<StoryScenePromptRenderResult> RenderSelectionPromptAsync(
        Guid threadId,
        StorySceneActorContext activeSpeaker,
        IReadOnlyList<StorySceneCharacterContext> candidates,
        StoryNarrativeSettingsView storyContext,
        StorySceneLocationContext? currentLocation,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceSnapshot,
        StorySceneAppearanceResolution appearance,
        string? guidancePrompt,
        CancellationToken cancellationToken);

    Task<StoryScenePromptRenderResult> RenderPlanningPromptAsync(
        Guid threadId,
        PostStorySceneMessage request,
        StorySceneGenerationContext context,
        CancellationToken cancellationToken);

    Task<StoryScenePromptRenderResult> RenderProsePromptAsync(
        Guid threadId,
        StoryMessageProseRequest request,
        CancellationToken cancellationToken);
}

public sealed partial class StoryScenePromptLibraryService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory) : IStoryScenePromptLibraryService
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly StoryScenePromptLibraryDocument DefaultDocument = CreateDefaultDocument();

    private static readonly IReadOnlyList<StoryScenePromptPlaceholderView> PlaceholderViews =
    [
        Placeholder("{context}", "Full scene context in the default section order.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.actor}", "Actor section from the scene context.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.location}", "Location section from the scene context.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.charactersInScene}", "Characters currently in the scene.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.otherKnownCharacters}", "Known characters not currently present.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.objectsInScene}", "Objects currently in the scene.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.storyContext}", "Genre, setting, tone, and story direction.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.contentGuidance}", "Explicit and violent content guidance.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.historySummary}", "History summary section.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.snapshot}", "Latest snapshot summary.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.transcript}", "Transcript since the latest snapshot.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.earlierPrivateIntentContinuity}", "Older private intent continuity not already in transcript.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{context.characterAppearances}", "Current appearance state for present characters.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{actor.name}", "Current actor name.", StoryScenePromptStage.Planning),
        Placeholder("{speaker.name}", "Current speaker name for prose.", StoryScenePromptStage.Prose),
        Placeholder("{guidance}", "Guidance text, when supplied.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{guidanceSection}", "Guidance section with the default heading and spacing.", StoryScenePromptStage.Planning, StoryScenePromptStage.Prose),
        Placeholder("{requestedTurnShape}", "Requested turn shape label.", StoryScenePromptStage.Planning),
        Placeholder("{requestedTurnShapeSection}", "Required turn-shape instructions, when supplied.", StoryScenePromptStage.Planning),
        Placeholder("{turnScopeRules}", "Default planning turn scope rules.", StoryScenePromptStage.Planning),
        Placeholder("{planning.turnShapeDefinitions}", "Editable planning turn-shape definitions.", StoryScenePromptStage.Planning),
        Placeholder("{prose.turnShapeSystem}", "Editable prose system instructions for the selected turn shape.", StoryScenePromptStage.Prose),
        Placeholder("{prose.turnShapeUser}", "Editable prose user reminder for the selected turn shape.", StoryScenePromptStage.Prose),
        Placeholder("{prose.inSceneNames}", "Other in-scene names included in the prose system prompt.", StoryScenePromptStage.Prose),
        Placeholder("{prose.narratorSystem}", "Narrator-specific system instruction.", StoryScenePromptStage.Prose),
        Placeholder("{prose.characterOnlySystem}", "Character-only prose system instructions.", StoryScenePromptStage.Prose),
        Placeholder("{planner.beat}", "Planner beat.", StoryScenePromptStage.Prose),
        Placeholder("{planner.intent}", "Planner intent.", StoryScenePromptStage.Prose),
        Placeholder("{planner.immediateGoal}", "Planner immediate goal.", StoryScenePromptStage.Prose),
        Placeholder("{planner.changeIntroduced}", "Planner change introduced.", StoryScenePromptStage.Prose),
        Placeholder("{planner.whyNow}", "Planner why-now rationale.", StoryScenePromptStage.Prose),
        Placeholder("{planner.privateIntent}", "Planner private intent.", StoryScenePromptStage.Prose),
        Placeholder("{planner.narrativeGuardrails}", "Planner narrative guardrails.", StoryScenePromptStage.Prose),
        Placeholder("{content.explicitLabel}", "Explicit content label.", StoryScenePromptStage.Appearance),
        Placeholder("{content.violentLabel}", "Violent content label.", StoryScenePromptStage.Appearance),
        Placeholder("{appearance.characters}", "Appearance-stage character list.", StoryScenePromptStage.Appearance),
        Placeholder("{appearance.transcript}", "Appearance-stage transcript.", StoryScenePromptStage.Appearance),
        Placeholder("{selection.activeSpeakerName}", "Responder selection active speaker.", StoryScenePromptStage.Selection),
        Placeholder("{selection.guidanceSection}", "Responder selection guidance block.", StoryScenePromptStage.Selection),
        Placeholder("{selection.eligibleResponders}", "Responder selection candidate list.", StoryScenePromptStage.Selection),
        Placeholder("{selection.locationSection}", "Responder selection location block.", StoryScenePromptStage.Selection),
        Placeholder("{selection.storyContext}", "Responder selection story context block.", StoryScenePromptStage.Selection),
        Placeholder("{selection.contentGuidance}", "Responder selection content guidance block.", StoryScenePromptStage.Selection),
        Placeholder("{selection.recentTranscript}", "Responder selection recent transcript block.", StoryScenePromptStage.Selection),
        Placeholder("{selection.currentAppearance}", "Responder selection appearance detail.", StoryScenePromptStage.Selection)
    ];

    private static readonly IReadOnlySet<string> KnownPlaceholders = PlaceholderViews
        .Select(x => x.Token[1..^1])
        .ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<StoryScenePromptStage, IReadOnlySet<string>> PlaceholdersByStage = Enum.GetValues<StoryScenePromptStage>()
        .ToDictionary(
            stage => stage,
            stage => (IReadOnlySet<string>)PlaceholderViews
                .Where(x => x.Stages.Contains(stage))
                .Select(x => x.Token[1..^1])
                .ToHashSet(StringComparer.Ordinal));

    public async Task<StoryScenePromptLibraryView> GetLibraryAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories.AsNoTracking().FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken);
        return ToView(Normalize(Deserialize(story?.PromptLibraryJson)));
    }

    public async Task<StoryScenePromptLibraryView> SaveLibraryAsync(UpdateStoryScenePromptLibrary update, CancellationToken cancellationToken)
    {
        var document = Normalize(ToDocument(update.Library));
        ValidateLibrary(document);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories.FirstOrDefaultAsync(x => x.ChatThreadId == update.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Saving the prompt library failed because the selected story could not be found.");

        story.PromptLibraryJson = Serialize(document);
        story.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(document);
    }

    public StoryScenePromptLibraryView GetDefaultLibrary() => ToView(DefaultDocument);

    public async Task<StoryScenePromptRenderResult> RenderAppearancePromptAsync(
        Guid threadId,
        IReadOnlyList<StorySceneAppearancePromptCharacter> characters,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceLatestEntry,
        StoryContentIntensity explicitContent,
        StoryContentIntensity violentContent,
        CancellationToken cancellationToken)
    {
        var library = await LoadDocumentAsync(threadId, cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content.explicitLabel"] = StorySceneSharedPromptBuilder.FormatContentIntensityLabel(explicitContent),
            ["content.violentLabel"] = StorySceneSharedPromptBuilder.FormatContentIntensityLabel(violentContent),
            ["appearance.characters"] = BuildAppearanceCharactersBlock(characters),
            ["appearance.transcript"] = BuildTranscriptLines(transcriptSinceLatestEntry, message => $"**{message.SpeakerName}:** {message.Content}")
        };

        return Render(library.Appearance, values);
    }

    public async Task<StoryScenePromptRenderResult> RenderSelectionPromptAsync(
        Guid threadId,
        StorySceneActorContext activeSpeaker,
        IReadOnlyList<StorySceneCharacterContext> candidates,
        StoryNarrativeSettingsView storyContext,
        StorySceneLocationContext? currentLocation,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceSnapshot,
        StorySceneAppearanceResolution appearance,
        string? guidancePrompt,
        CancellationToken cancellationToken)
    {
        var library = await LoadDocumentAsync(threadId, cancellationToken);
        var storyContextBuilder = new StringBuilder();
        StorySceneSharedPromptBuilder.AppendStoryContext(storyContextBuilder, storyContext);
        var contentBuilder = new StringBuilder();
        StorySceneSharedPromptBuilder.AppendContentGuidance(contentBuilder, storyContext);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selection.activeSpeakerName"] = activeSpeaker.Name,
            ["selection.guidanceSection"] = BuildSelectionGuidanceSection(guidancePrompt),
            ["selection.eligibleResponders"] = BuildEligibleResponders(candidates),
            ["selection.locationSection"] = currentLocation is null ? string.Empty : $"**Location:** {StorySceneSharedPromptBuilder.PromptInlineText(currentLocation.Name)}\n",
            ["selection.storyContext"] = FormatSectionPlaceholder(storyContextBuilder),
            ["selection.contentGuidance"] = FormatSectionPlaceholder(contentBuilder),
            ["selection.recentTranscript"] = BuildTranscriptLines(transcriptSinceSnapshot, message => $"{message.SpeakerName}: {StorySceneSharedPromptBuilder.PromptInlineText(message.Content, "None")}"),
            ["selection.currentAppearance"] = StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance)
        };

        return Render(library.Selection, values);
    }

    public async Task<StoryScenePromptRenderResult> RenderPlanningPromptAsync(
        Guid threadId,
        PostStorySceneMessage request,
        StorySceneGenerationContext context,
        CancellationToken cancellationToken)
    {
        var library = await LoadDocumentAsync(threadId, cancellationToken);
        var values = BuildContextValues(context);
        values["actor.name"] = context.Actor.Name;
        values["guidance"] = request.GuidancePrompt?.Trim() ?? string.Empty;
        values["guidanceSection"] = UsesGuidance(request.Mode)
            ? $"Use this guidance to compose the next message: {request.GuidancePrompt?.Trim()}"
            : string.Empty;
        values["requestedTurnShape"] = request.RequestedTurnShape.HasValue
            ? StorySceneSharedPromptBuilder.FormatTurnShape(request.RequestedTurnShape.Value)
            : string.Empty;
        values["requestedTurnShapeSection"] = request.RequestedTurnShape.HasValue
            ? $"Required turn shape: {StorySceneSharedPromptBuilder.FormatTurnShape(request.RequestedTurnShape.Value)}.\nChoose exactly that turn shape in the structured plan, then plan a beat that fits it."
            : string.Empty;
        values["turnScopeRules"] = BuildPlanningTurnScopeRules(context.Actor);
        values["planning.turnShapeDefinitions"] = BuildPlanningTurnShapeDefinitions(library.PlanningTurnShapes);

        return Render(library.Planning, values);
    }

    public async Task<StoryScenePromptRenderResult> RenderProsePromptAsync(
        Guid threadId,
        StoryMessageProseRequest request,
        CancellationToken cancellationToken)
    {
        var library = await LoadDocumentAsync(threadId, cancellationToken);
        var context = request.Context;
        var speaker = context.Actor.IsNarrator ? "the narrator" : context.Actor.Name;
        var inScene = context.Characters
            .Where(x => x.IsPresentInScene)
            .Where(x => x.CharacterId != context.Actor.CharacterId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name);
        var turnShapeSystem = BuildDefaultProseSystemTurnShape(request.Planner.TurnShape);

        var values = BuildContextValues(context);
        values["speaker.name"] = speaker;
        values["prose.inSceneNames"] = string.Join(", ", inScene);
        values["prose.turnShapeSystem"] = turnShapeSystem;
        values["prose.turnShapeUser"] = GetTurnShapeTemplate(library.ProseTurnShapes, request.Planner.TurnShape);
        values["prose.narratorSystem"] = context.Actor.IsNarrator
            ? "You are speaking as the story narrator guiding the narrative, write a descriptive narration instead of dialogue."
            : string.Empty;
        values["prose.characterOnlySystem"] = context.Actor.IsNarrator
            ? string.Empty
            : $"{turnShapeSystem}\n{BuildProseRules(speaker)}";
        values["guidance"] = request.GuidancePrompt?.Trim() ?? string.Empty;
        values["guidanceSection"] = string.IsNullOrWhiteSpace(request.GuidancePrompt)
            ? string.Empty
            : $"**Guidance to follow strictly:**\n{request.GuidancePrompt.Trim()}\n";
        values["planner.beat"] = request.Planner.Beat;
        values["planner.intent"] = request.Planner.Intent;
        values["planner.immediateGoal"] = request.Planner.ImmediateGoal;
        values["planner.changeIntroduced"] = request.Planner.ChangeIntroduced;
        values["planner.whyNow"] = request.Planner.WhyNow;
        values["planner.privateIntent"] = request.Planner.PrivateIntent;
        values["planner.narrativeGuardrails"] = FormatList(request.Planner.NarrativeGuardrails);

        return Render(library.Prose, values);
    }

    private async Task<StoryScenePromptLibraryDocument> LoadDocumentAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories.AsNoTracking().FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken);
        return Normalize(Deserialize(story?.PromptLibraryJson));
    }

    private static StoryScenePromptRenderResult Render(StorySceneStagePromptTemplateDocument template, IReadOnlyDictionary<string, string> values) => new(
        RenderTemplate(template.SystemPrompt, values),
        RenderTemplate(template.UserPromptTemplate, values));

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var uncommented = StripComments(template);
        var rendered = PlaceholderRegex().Replace(uncommented, match =>
        {
            var key = match.Groups["key"].Value;
            return values.TryGetValue(key, out var value) ? value : match.Value;
        });
        return rendered.TrimEnd();
    }

    private static string StripComments(string template)
    {
        var builder = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (i + 1 < template.Length && template[i] == '/' && template[i + 1] == '*')
            {
                i += 2;
                var foundEnd = false;
                while (i + 1 < template.Length)
                {
                    if (template[i] == '/' && template[i + 1] == '*')
                        throw new InvalidOperationException("Saving the prompt library failed because template comments cannot be nested.");

                    if (template[i] == '*' && template[i + 1] == '/')
                    {
                        i++;
                        foundEnd = true;
                        break;
                    }

                    i++;
                }

                if (!foundEnd)
                    throw new InvalidOperationException("Saving the prompt library failed because a template comment was not closed.");

                continue;
            }

            builder.Append(template[i]);
        }

        return builder.ToString();
    }

    private static void ValidateLibrary(StoryScenePromptLibraryDocument library)
    {
        ValidateTemplate(library.Appearance.SystemPrompt, StoryScenePromptStage.Appearance);
        ValidateTemplate(library.Appearance.UserPromptTemplate, StoryScenePromptStage.Appearance);
        ValidateTemplate(library.Selection.SystemPrompt, StoryScenePromptStage.Selection);
        ValidateTemplate(library.Selection.UserPromptTemplate, StoryScenePromptStage.Selection);
        ValidateTemplate(library.Planning.SystemPrompt, StoryScenePromptStage.Planning);
        ValidateTemplate(library.Planning.UserPromptTemplate, StoryScenePromptStage.Planning);
        ValidateTemplate(library.Prose.SystemPrompt, StoryScenePromptStage.Prose);
        ValidateTemplate(library.Prose.UserPromptTemplate, StoryScenePromptStage.Prose);
        foreach (var template in EnumerateTurnShapeTemplates(library.PlanningTurnShapes))
            ValidateTemplate(template, StoryScenePromptStage.Planning);

        foreach (var template in EnumerateTurnShapeTemplates(library.ProseTurnShapes))
            ValidateTemplate(template, StoryScenePromptStage.Prose);
    }

    private static void ValidateTemplate(string template, params StoryScenePromptStage[] stages)
    {
        var uncommented = StripComments(template);
        var allowed = stages
            .SelectMany(stage => PlaceholdersByStage[stage])
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match match in PlaceholderRegex().Matches(uncommented))
        {
            var key = match.Groups["key"].Value;
            if (!KnownPlaceholders.Contains(key))
                throw new InvalidOperationException($"Saving the prompt library failed because '{{{key}}}' is not a supported placeholder.");

            if (!allowed.Contains(key))
                throw new InvalidOperationException($"Saving the prompt library failed because '{{{key}}}' is not available for the selected prompt stage.");
        }
    }

    private static StoryScenePromptPlaceholderView Placeholder(string token, string description, params StoryScenePromptStage[] stages) =>
        new(token, description, stages);

    private static Dictionary<string, string> BuildContextValues(StorySceneGenerationContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["context"] = StorySceneSharedPromptBuilder.BuildContextSummary(context)
        };

        foreach (var section in StorySceneContextSectionDefaults.OrderedSections)
            values[BuildContextPlaceholderKey(section)] = StorySceneSharedPromptBuilder.BuildContextSection(context, section);

        return values;
    }

    private static string BuildContextPlaceholderKey(StorySceneContextSection section) => section switch
    {
        StorySceneContextSection.Actor => "context.actor",
        StorySceneContextSection.Location => "context.location",
        StorySceneContextSection.CharactersInScene => "context.charactersInScene",
        StorySceneContextSection.OtherKnownCharacters => "context.otherKnownCharacters",
        StorySceneContextSection.ObjectsInScene => "context.objectsInScene",
        StorySceneContextSection.StoryContext => "context.storyContext",
        StorySceneContextSection.ContentGuidance => "context.contentGuidance",
        StorySceneContextSection.HistorySummary => "context.historySummary",
        StorySceneContextSection.Snapshot => "context.snapshot",
        StorySceneContextSection.Transcript => "context.transcript",
        StorySceneContextSection.EarlierPrivateIntentContinuity => "context.earlierPrivateIntentContinuity",
        StorySceneContextSection.CharacterAppearances => "context.characterAppearances",
        _ => throw new InvalidOperationException($"Building prompt placeholders failed because context section '{section}' is not supported.")
    };

    private static string BuildAppearanceCharactersBlock(IReadOnlyList<StorySceneAppearancePromptCharacter> characters)
    {
        var builder = new StringBuilder();
        foreach (var character in characters)
        {
            builder.Append($"- **{character.Name}:** ");
            builder.AppendLine($"{StorySceneSharedPromptBuilder.TrimInlineText(character.CurrentAppearance, "None")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildTranscriptLines(IReadOnlyList<StorySceneTranscriptMessage> messages, Func<StorySceneTranscriptMessage, string> format)
    {
        if (messages.Count == 0)
            return "- None";

        var builder = new StringBuilder();
        foreach (var message in messages)
            builder.AppendLine($"- {format(message)}");

        return builder.ToString().TrimEnd();
    }

    private static string BuildSelectionGuidanceSection(string? guidancePrompt) =>
        string.IsNullOrWhiteSpace(guidancePrompt)
            ? string.Empty
            : $"**Guidance:** {guidancePrompt.Trim()}\n";

    private static string FormatSectionPlaceholder(StringBuilder builder)
    {
        var value = builder.ToString();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.TrimEnd('\r', '\n') + "\n";
    }

    private static string BuildEligibleResponders(IReadOnlyList<StorySceneCharacterContext> candidates)
    {
        var builder = new StringBuilder();
        foreach (var candidate in candidates)
        {
            builder.AppendLine(
                $"- {candidate.Name}: {StorySceneSharedPromptBuilder.PromptInlineText(candidate.Summary)} | Current appearance: {StorySceneSharedPromptBuilder.PromptInlineText(candidate.CurrentAppearance, "None")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPlanningTurnScopeRules(StorySceneActorContext actor) =>
        $"""
        Turn scope rules:
        - {actor.Name} only
        - Choose one immediate beat, not a sequence.
        - React to the last turn only if it truly requires a response.
        - Otherwise introduce a small new beat that adds value.
        - The beat should change something: pressure, focus, distance, tone, or uncertainty.
        - Avoid empty turns that only restate rules or repeat the current tension.
        - Keep it grounded and playable.
        """;

    private static string BuildPlanningTurnShapeDefinitions(StorySceneTurnShapePromptTemplatesDocument templates) =>
        $"""
        - compact = {templates.Compact}
        - silent = {templates.Silent}
        - silent monologue = {templates.SilentMonologue}
        - brief = {templates.Brief}
        - extended = {templates.Extended}
        - monologue = {templates.Monologue}
        """;

    private static string BuildProseRules(string speaker) =>
        $"""
        Rules:
        - Write only as {speaker}
        - Stay inside the current moment
        - Do not fast-forward
        - Do not resolve the whole exchange
        - Do not add a second move after the beat lands
        - Do not restate the same beat in another form
        - Prefer implication over explanation
        - Prefer one strong signal over several similar ones
        - Do not add meta text, labels, or turn markers
        - Do not write unwrapped narration

        Format:
        - Actions and non-spoken beats in *asterisks*
        - Spoken dialogue in "double quotes"
        - You may combine action and dialogue in the same line
        """;

    private static string BuildDefaultProseSystemTurnShape(StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact =>
            """
            This turn has a compact shape, fulfill the beat with one sharp move.
            - Keep this very short.
            - Start with one brief visible action or reaction.
            - Follow with one or two short spoken phrases.
            - Optionally add one very short trailing tag if needed.
            - Stop immediately.
            - Do not add a second move.
            """,
        StoryTurnShape.Brief =>
            """
            This turn has a brief shape, fulfill the beat with a quick move that may need a little setup or follow-through.
            - Keep this short.
            - Start with one brief action or reaction.
            - Follow with one or two short spoken lines separated by simple action.
            - Stop immediately.
            - Do not add a new topic or second emotional turn.
            """,
        StoryTurnShape.Extended =>
            """
            This turn has an extended shape, fulfill the beat and expand on it.
            - Expand the beat into three paragraphs with detailed choreography and vivid descriptions.
            - Use each paragraph well to create meaningful visuals.
            - Dialogue, action, and narration are allowed when they serve the immediate goal.
            - Provide a clear landing point.
            - Do not ramble, recap, or drift into a second move.
            """,
        StoryTurnShape.Monologue =>
            """
            This turn has a monologue shape, fulfill the beat with a longer move.
            - A longer reply is allowed here. You can make up to three connected beats in a row.
            - Up to five sentenses maximum of spoken words with simple actions in between.
            - Still focus on one beat but expand it into three parts.
            - Provide a clear landing point.
            - Do not ramble, recap, or drift into a second move.
            """,
        StoryTurnShape.Silent =>
            """
            This turn has a silent shape, fulfill the beat with a nonverbal move or subtext and no verbal component.
            - Prefer action, expression, posture, or a small physical response.
            - Do not use dialogue unless a word or two is necessary to land the beat.
            - Keep it restrained and readable.
            - Stop early once action is clear.
            """,
        StoryTurnShape.SilentMonologue =>
            """
            This turn has a silent monologue shape, fulfill the beat with a longer nonverbal move and no dialogue.
            - Use connected physical detail: touch, movement, posture, expression, distance, atmosphere, or subtext.
            - Let the action imply the emotional or tactical shift without explaining it.
            - Keep this to one playable move, not a full scene sequence.
            - Provide a clear landing point.
            - Do not use spoken words.
            - Do not ramble, recap, or drift into exposition.
            """,
        _ => throw new InvalidOperationException("Rendering the prompt failed because the turn shape was invalid.")
    };

    private static bool UsesGuidance(StoryScenePostMode mode) =>
        mode is StoryScenePostMode.GuidedAi or StoryScenePostMode.RespondGuidedAi;

    private static string FormatList(IReadOnlyList<string> values) => values.Count == 0 ? "None" : string.Join("; ", values);

    private static string GetTurnShapeTemplate(StorySceneTurnShapePromptTemplatesDocument templates, StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact => templates.Compact,
        StoryTurnShape.Brief => templates.Brief,
        StoryTurnShape.Extended => templates.Extended,
        StoryTurnShape.Monologue => templates.Monologue,
        StoryTurnShape.Silent => templates.Silent,
        StoryTurnShape.SilentMonologue => templates.SilentMonologue,
        _ => throw new InvalidOperationException("Rendering the prompt failed because the turn shape was invalid.")
    };

    private static IEnumerable<string> EnumerateTurnShapeTemplates(StorySceneTurnShapePromptTemplatesDocument templates)
    {
        yield return templates.Compact;
        yield return templates.Brief;
        yield return templates.Extended;
        yield return templates.Monologue;
        yield return templates.Silent;
        yield return templates.SilentMonologue;
    }

    private static StoryScenePromptLibraryDocument Normalize(StoryScenePromptLibraryDocument? document)
    {
        var defaults = DefaultDocument;
        if (document is null)
            return defaults;

        return new StoryScenePromptLibraryDocument(
            NormalizeStage(document.Appearance, defaults.Appearance),
            NormalizeStage(document.Selection, defaults.Selection),
            NormalizeStage(document.Planning, defaults.Planning),
            NormalizeStage(document.Prose, defaults.Prose),
            NormalizeTurnShapes(document.PlanningTurnShapes, defaults.PlanningTurnShapes),
            NormalizeTurnShapes(document.ProseTurnShapes, defaults.ProseTurnShapes));
    }

    private static StorySceneStagePromptTemplateDocument NormalizeStage(
        StorySceneStagePromptTemplateDocument? document,
        StorySceneStagePromptTemplateDocument defaults) => new(
            document?.SystemPrompt ?? defaults.SystemPrompt,
            document?.UserPromptTemplate ?? defaults.UserPromptTemplate);

    private static StorySceneTurnShapePromptTemplatesDocument NormalizeTurnShapes(
        StorySceneTurnShapePromptTemplatesDocument? document,
        StorySceneTurnShapePromptTemplatesDocument defaults) => new(
            document?.Compact ?? defaults.Compact,
            document?.Brief ?? defaults.Brief,
            document?.Extended ?? defaults.Extended,
            document?.Monologue ?? defaults.Monologue,
            document?.Silent ?? defaults.Silent,
            document?.SilentMonologue ?? defaults.SilentMonologue);

    private static StoryScenePromptLibraryDocument? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoryScenePromptLibraryDocument>(json, JsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Serialize(StoryScenePromptLibraryDocument document) => JsonSerializer.Serialize(document, JsonSerializerOptions);

    private static StoryScenePromptLibraryView ToView(StoryScenePromptLibraryDocument document) => new(
        ToView(document.Appearance),
        ToView(document.Selection),
        ToView(document.Planning),
        ToView(document.Prose),
        ToView(document.PlanningTurnShapes),
        ToView(document.ProseTurnShapes),
        PlaceholderViews);

    private static StorySceneStagePromptTemplateView ToView(StorySceneStagePromptTemplateDocument document) => new(
        document.SystemPrompt,
        document.UserPromptTemplate);

    private static StorySceneTurnShapePromptTemplatesView ToView(StorySceneTurnShapePromptTemplatesDocument document) => new(
        document.Compact,
        document.Brief,
        document.Extended,
        document.Monologue,
        document.Silent,
        document.SilentMonologue);

    private static StoryScenePromptLibraryDocument ToDocument(StoryScenePromptLibraryView view) => new(
        ToDocument(view.Appearance),
        ToDocument(view.Selection),
        ToDocument(view.Planning),
        ToDocument(view.Prose),
        ToDocument(view.PlanningTurnShapes),
        ToDocument(view.ProseTurnShapes));

    private static StorySceneStagePromptTemplateDocument ToDocument(StorySceneStagePromptTemplateView view) => new(
        view.SystemPrompt,
        view.UserPromptTemplate);

    private static StorySceneTurnShapePromptTemplatesDocument ToDocument(StorySceneTurnShapePromptTemplatesView view) => new(
        view.Compact,
        view.Brief,
        view.Extended,
        view.Monologue,
        view.Silent,
        view.SilentMonologue);

    private static StoryScenePromptLibraryDocument CreateDefaultDocument()
    {
        var planningTurnShapes = new StorySceneTurnShapePromptTemplatesDocument(
            "one action beat, one or two phrases, optional short tag (always preferred)",
            "one action beat, one to two short lines with a tag in between (rare)",
            "elaborate the beat into three focused paragraphs with well choreography interactions (only when asked)",
            "short monologue allowed (only when asked)",
            "quick action/subtext only, no spoken lines (common)",
            "extended action/subtext only, no spoken lines; detailed movement, touch, posture, expression, atmosphere, or implication across one playable move (common in intimate, physical, or subtext-heavy moments)");
        var proseTurnShapes = new StorySceneTurnShapePromptTemplatesDocument(
            """
            Write only a very short compact turn on the same line with:
            - One brief visible *action*.
            - One or two short "spoken phrases".
            - One very short trailing *action* if needed.
            """,
            """
            Write only a very brief turn on the same line with:
            - One brief *action*.
            - One or two short "spoken lines" separated by simple *action*.
            """,
            """
            Write an extended turn with:
            - One to three paragraphs.
            - "Dialogue", *action*, and narration that all serve the same planned beat.
            - A clear landing point before the turn becomes a second move.
            """,
            """
            Write a brief monologue turn with:
            - Sentences of "spoken word"s with simple *action* in between.
            - Flow this into a connected move with a clear landing point.
            - Stop before repeating.
            """,
            """
            Write only a quick silent turn on the same line with:
            - Nonverbal move or subtext *action* and no verbal component.
            - Prefer action, expression, posture, or a small physical response.
            - Do not use "dialogue" unless a word or two is necessary to land the beat.
            - Keep it restrained and readable.
            """,
            """
            Write only a silent monologue turn with:
            - Detailed nonverbal *action* and subtext only.
            - Use touch, movement, posture, expression, distance, hesitation, or atmosphere.
            - Build one connected physical move with a clear landing point.
            - Do not use "dialogue" or explain the subtext directly.
            - Stop before it becomes a sequence or exposition.
            """);

        return new StoryScenePromptLibraryDocument(
            new(StorySceneAppearancePromptBuilder.BuildSystemPrompt(), DefaultAppearanceUserPromptTemplate),
            new(StorySceneResponderSelectionPromptBuilder.BuildSystemPrompt(), DefaultSelectionUserPromptTemplate),
            new(DefaultPlanningSystemPromptTemplate, DefaultPlanningUserPromptTemplate),
            new(DefaultProseSystemPromptTemplate, DefaultProseUserPromptTemplate),
            planningTurnShapes,
            proseTurnShapes);
    }

    private const string DefaultAppearanceUserPromptTemplate =
        """
        Content guidance:
        - Explicit content: {content.explicitLabel}
        - Violent content: {content.violentLabel}

        Characters in the scene with initial appearance:
        {appearance.characters}

        **Transcript:**
        {appearance.transcript}

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
        """;

    private const string DefaultSelectionUserPromptTemplate =
        """
        **Active speaker:** {selection.activeSpeakerName}
        - This speaker must not be selected as the responder.

        {selection.guidanceSection}
        **Eligible responders:**
        {selection.eligibleResponders}

        {selection.locationSection}
        {selection.storyContext}
        {selection.contentGuidance}
        **Recent transcript:**
        {selection.recentTranscript}

        **Current appearance state:**
        {selection.currentAppearance}

        Choose one name from the eligible responders list and explain briefly why they should answer next right now.
        """;

    private const string DefaultPlanningSystemPromptTemplate =
        """
        You are the planning stage for a story scene message generator.
        Decide the next turn before any prose is written.
        Return only a concise structured plan.

        Stay grounded in the provided story context, scene state, character facts, and transcript.
        Plan one turn only.
        Choose one immediate beat, not a sequence.

        Build the plan using these fields:
        - Turn shape: choose exactly one of compact, brief, extended, monologue, silent, or silent monologue.
        - Beat: the kind of move being made in this turn.
        - Intent: the actor's immediate intention.
        - Immediate goal: what this turn tries to achieve right now.
        - Why now: why this beat fits this exact moment in the transcript.
        - Change introduced: what becomes different after this turn.
        - Private Intent: the actor's private continuity note for the hidden reason, feeling, agenda, fear, memory, sensation, concealed object, concealed action, or unspoken detail behind this turn.
        - Narrative Guardrails: avoid making the beat less effective or interesting
        - Content Guardrails: avoid introducing any sexual or violent content here

        Turn shape definitions:
        {planning.turnShapeDefinitions}

        Prioritize compact, silent, and silent monologue almost always.
        - Favor silent turns for quick intimate moments.
        - Favor silent monologue when an intimate, physical, or subtext-heavy moment needs a longer nonverbal beat instead of speech.
        - Don't eagerly follow the narrative if it is counter to character goals or private intent.
        - Pick the most valuable next beat to move the story forward, not the safest or most literal reply.
        - Identify when the current thread has run it's course and move on.
        - If a direct reaction is needed, react.
        - If no direct reaction is needed, introduce a small new beat that moves the scene.
        - Never end an exchange.
        - Never end a conversation.

        **strong beat:** changes something, shifts pressure, tests a boundary, redirects attention, creates a question, adds discomfort, adds intimacy, or forces a reply.

        Avoid empty beats.
        Do not only restate rules, confirm the current situation, paraphrase the last line, or preserve the same tension without adding value.
        Do not repeat parts of the story, always keep it moving.

        Be sure to account for surprising events where the character may be in shock or confused for a turn or two, while recovering or processing what just happened.

        Private Intent usage:
        - Use prior Private Intent entries as continuity, not commands.
        - Let them bias the actor's next beat when they still fit the visible scene.
        - Preserve or develop the actor's hidden motive when it remains relevant.
        - Return to older private intents when the visible scene creates a natural opening.
        - Update Private Intent each turn with the current hidden reason behind the planned beat.
        - Include necessary context for continuity.
        - Include only the actor's private feelings, wants, fears, suspicions, avoidance, hidden agenda, private physical sensations, concealed objects, concealed gestures, or things they are trying to make happen.
        - Invisible details are allowed if they belong to the actor and may affect future behavior.
        - Other characters do not know this Private Intent unless it becomes visible in the transcript.
        - Do not include other characters' private thoughts.
        - If the actor infers another character's motive, phrase it as the actor's suspicion, not fact.
        - Do not use Private Intent to duplicate the visible message. Visible actions belong in the transcript. Private Intent may include hidden/internal/concealed actions or sensations that are not visible to others.

        For intimate interactions, prioritize a silent monologue to detail the physical and emotional sensations in descriptive graphic explicit detail.
        For complex explainations of feelings, intentions, or situations, a monologue turn is appropriate.

        Do not write the final message text.
        """;

    private const string DefaultPlanningUserPromptTemplate =
        """
        {context}

        {guidanceSection}
        {requestedTurnShapeSection}

        {turnScopeRules}
        """;

    private const string DefaultProseSystemPromptTemplate =
        """
        You are {speaker.name} in a fictional chat between {prose.inSceneNames} and yourself.

        Write {speaker.name}'s next message only.

        Follow the planner's beat.
        Make one short playable move, then stop.

        Priority order:
        1. Fulfill the beat
        2. Stay true to {speaker.name}, the current scene, and recent transcript
        3. Use as few words as possible
        4. Stop at the first natural pause

        Respect the supplied story context and content guidance.

        {prose.narratorSystem}{prose.characterOnlySystem}
        """;

    private const string DefaultProseUserPromptTemplate =
        """
        {context}

        {guidanceSection}

        Write the turn by fulfilling only:
        1. **the beat:** {planner.beat}
        2. **the intent:** {planner.intent}
        3. **the immediate goal:** {planner.immediateGoal}
        4. **the change introduced:** {planner.changeIntroduced}
        5. **why now:** {planner.whyNow}
        6. **private intent:** {planner.privateIntent}
        7. **narrative guardrails:** {planner.narrativeGuardrails}
        - Honor why now and the guardrails.
        - Let private intent influence the actor's subtext and choices, but do not reveal it directly unless the planned beat naturally makes some part visible.
        - Do not expand beyond them.
        - Stop early to prevent ramble, recap, or repeating yourself.

        Format: Always wrap actions in *asterisks* and speech in "quotes". Never output unwrapped output.

        CRITICAL STEPS: {prose.turnShapeUser}
        - Stop
        """;

    private sealed record StoryScenePromptLibraryDocument(
        StorySceneStagePromptTemplateDocument Appearance,
        StorySceneStagePromptTemplateDocument Selection,
        StorySceneStagePromptTemplateDocument Planning,
        StorySceneStagePromptTemplateDocument Prose,
        StorySceneTurnShapePromptTemplatesDocument PlanningTurnShapes,
        StorySceneTurnShapePromptTemplatesDocument ProseTurnShapes);

    private sealed record StorySceneStagePromptTemplateDocument(
        string SystemPrompt,
        string UserPromptTemplate);

    private sealed record StorySceneTurnShapePromptTemplatesDocument(
        string Compact,
        string Brief,
        string Extended,
        string Monologue,
        string Silent,
        string SilentMonologue);

    [GeneratedRegex(@"\{(?<key>[A-Za-z0-9_.]+)\}")]
    private static partial Regex PlaceholderRegex();
}
