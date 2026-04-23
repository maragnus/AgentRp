using System.ComponentModel;
using System.Text;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentRp.Services;

public sealed class StoryEntityAiAssistService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IChatStoryService chatStoryService,
    IStoryFieldGuidanceService storyFieldGuidanceService,
    IThreadAgentService threadAgentService,
    ILogger<StoryEntityAiAssistService> logger) : IStoryEntityAiAssistService
{

    public async Task<StoryEntityAiDraftSessionView> GenerateDraftAsync(StartStoryEntityAiDraft request, CancellationToken cancellationToken)
    {
        var snapshot = await LoadStorySnapshotAsync(request.ThreadId, cancellationToken);
        return await GenerateSessionAsync(
            snapshot,
            request.EntityKind,
            request.EntityId,
            request.Prompt,
            [],
            null,
            cancellationToken);
    }

    public async Task<StoryEntityAiDraftSessionView> RefineDraftAsync(RefineStoryEntityAiDraft request, CancellationToken cancellationToken)
    {
        ValidateSession(request.ThreadId, request.Session);

        var snapshot = await LoadStorySnapshotAsync(request.ThreadId, cancellationToken);
        return await GenerateSessionAsync(
            snapshot,
            request.Session.EntityKind,
            request.Session.EntityId,
            request.Prompt,
            request.Session.PromptHistory,
            request.Session,
            cancellationToken);
    }

    public async Task<StoryEntityAcceptResult> AcceptDraftAsync(AcceptStoryEntityAiDraft request, CancellationToken cancellationToken)
    {
        ValidateSession(request.ThreadId, request.Session);

        return request.Session.Draft switch
        {
            CharacterAiDraftView draft => await AcceptCharacterAsync(request, draft, cancellationToken),
            ItemAiDraftView draft => await AcceptItemAsync(request, draft, cancellationToken),
            LocationAiDraftView draft => await AcceptLocationAsync(request, draft, cancellationToken),
            HistoryFactAiDraftView draft => await AcceptHistoryFactAsync(request, draft, cancellationToken),
            TimelineEntryAiDraftView draft => await AcceptTimelineEntryAsync(request, draft, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported entity kind '{request.Session.EntityKind}'.")
        };
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateSessionAsync(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> priorPrompts,
        StoryEntityAiDraftSessionView? priorSession,
        CancellationToken cancellationToken)
    {
        var trimmedPrompt = prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
            throw new InvalidOperationException("Generating an AI draft failed because the prompt was empty.");

        var agent = await threadAgentService.GetSelectedAgentAsync(snapshot.ThreadId, cancellationToken);
        if (agent is null)
        {
            logger.LogError("No AI provider is configured for story entity AI assist in chat {ThreadId}.", snapshot.ThreadId);
            throw new InvalidOperationException("Generating the AI draft failed because no AI provider is configured for this chat.");
        }

        var guidance = await storyFieldGuidanceService.GetGuidanceAsync(entityKind, cancellationToken);
        if (entityKind == StoryEntityKind.Character)
            guidance = guidance.Where(x => x.FieldKey != StoryEntityFieldKey.PrivateMotivations).ToList();
        var messages = BuildMessages(snapshot, entityKind, entityId, trimmedPrompt, priorSession, guidance);
        var chatOptions = BuildChatOptions(snapshot);
        var promptHistory = priorPrompts.Concat([trimmedPrompt]).ToList();

        return entityKind switch
        {
            StoryEntityKind.Character => await GenerateCharacterSessionAsync(agent, snapshot, entityId, trimmedPrompt, promptHistory, messages, chatOptions, cancellationToken),
            StoryEntityKind.Item => await GenerateItemSessionAsync(agent, snapshot, entityId, trimmedPrompt, promptHistory, messages, chatOptions, cancellationToken),
            StoryEntityKind.Location => await GenerateLocationSessionAsync(agent, snapshot, entityId, trimmedPrompt, promptHistory, messages, chatOptions, cancellationToken),
            StoryEntityKind.HistoryFact => await GenerateHistoryFactSessionAsync(agent, snapshot, entityId, trimmedPrompt, promptHistory, messages, chatOptions, cancellationToken),
            StoryEntityKind.TimelineEntry => await GenerateTimelineEntrySessionAsync(agent, snapshot, entityId, trimmedPrompt, promptHistory, messages, chatOptions, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported entity kind '{entityKind}'.")
        };
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateCharacterSessionAsync(
        ConfiguredAgent agent,
        StorySnapshot snapshot,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await agent.ChatClient.GetResponseAsync<CharacterAiResponse>(
            messages,
            options: chatOptions,
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var model = response.Result;
        var draft = new CharacterAiDraftView(
            RequireText(model.Name, "character name"),
            model.Summary?.Trim() ?? string.Empty,
            model.GeneralAppearance?.Trim() ?? string.Empty,
            model.CorePersonality?.Trim() ?? string.Empty,
            model.Relationships?.Trim() ?? string.Empty,
            model.PreferencesBeliefs?.Trim() ?? string.Empty,
            model.IsPresentInScene);

        return CreateSession(snapshot, StoryEntityKind.Character, entityId, prompt, promptHistory, draft, model.ReviewSummary, draft.Name);
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateItemSessionAsync(
        ConfiguredAgent agent,
        StorySnapshot snapshot,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await agent.ChatClient.GetResponseAsync<ItemAiResponse>(
            messages,
            options: chatOptions,
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var model = response.Result;
        var existingItem = entityId.HasValue ? snapshot.Items.FirstOrDefault(x => x.Id == entityId.Value) : null;
        var ownerReference = ResolveOptionalReference(
            snapshot.Characters.Select(x => new StoryEntityReferenceView(x.Id, x.Name)).ToList(),
            model.OwnerCharacterName,
            existingItem?.OwnerCharacterId);
        var locationReference = ResolveOptionalReference(
            snapshot.Locations.Select(x => new StoryEntityReferenceView(x.Id, x.Name)).ToList(),
            model.LocationName,
            existingItem?.LocationId);
        var draft = new ItemAiDraftView(
            RequireText(model.Name, "item name"),
            model.Summary?.Trim() ?? string.Empty,
            model.Details?.Trim() ?? string.Empty,
            ownerReference?.Id,
            ownerReference?.Name,
            locationReference?.Id,
            locationReference?.Name,
            model.IsPresentInScene);

        return CreateSession(snapshot, StoryEntityKind.Item, entityId, prompt, promptHistory, draft, model.ReviewSummary, draft.Name);
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateLocationSessionAsync(
        ConfiguredAgent agent,
        StorySnapshot snapshot,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await agent.ChatClient.GetResponseAsync<LocationAiResponse>(
            messages,
            options: chatOptions,
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var model = response.Result;
        var draft = new LocationAiDraftView(
            RequireText(model.Name, "location name"),
            model.Summary?.Trim() ?? string.Empty,
            model.Details?.Trim() ?? string.Empty,
            model.IsCurrent);

        return CreateSession(snapshot, StoryEntityKind.Location, entityId, prompt, promptHistory, draft, model.ReviewSummary, draft.Name);
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateHistoryFactSessionAsync(
        ConfiguredAgent agent,
        StorySnapshot snapshot,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await agent.ChatClient.GetResponseAsync<HistoryFactAiResponse>(
            messages,
            options: chatOptions,
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var model = response.Result;
        var draft = new HistoryFactAiDraftView(
            RequireText(model.Title, "fact title"),
            model.Summary?.Trim() ?? string.Empty,
            model.Details?.Trim() ?? string.Empty,
            ResolveReferences(snapshot.Characters, model.CharacterNames, x => x.Id, x => x.Name),
            ResolveReferences(snapshot.Locations, model.LocationNames, x => x.Id, x => x.Name),
            ResolveReferences(snapshot.Items, model.ItemNames, x => x.Id, x => x.Name));

        return CreateSession(snapshot, StoryEntityKind.HistoryFact, entityId, prompt, promptHistory, draft, model.ReviewSummary, draft.Title);
    }

    private async Task<StoryEntityAiDraftSessionView> GenerateTimelineEntrySessionAsync(
        ConfiguredAgent agent,
        StorySnapshot snapshot,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var response = await agent.ChatClient.GetResponseAsync<TimelineEntryAiResponse>(
            messages,
            options: chatOptions,
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var model = response.Result;
        var existingEntry = entityId.HasValue ? snapshot.History.TimelineEntries.FirstOrDefault(x => x.Id == entityId.Value) : null;
        var sortOrder = existingEntry?.SortOrder ?? GetNextTimelineSortOrder(snapshot);
        var draft = new TimelineEntryAiDraftView(
            sortOrder,
            model.WhenText?.Trim() ?? string.Empty,
            RequireText(model.Title, "timeline title"),
            model.Summary?.Trim() ?? string.Empty,
            model.Details?.Trim() ?? string.Empty,
            ResolveReferences(snapshot.Characters, model.CharacterNames, x => x.Id, x => x.Name),
            ResolveReferences(snapshot.Locations, model.LocationNames, x => x.Id, x => x.Name),
            ResolveReferences(snapshot.Items, model.ItemNames, x => x.Id, x => x.Name));

        return CreateSession(snapshot, StoryEntityKind.TimelineEntry, entityId, prompt, promptHistory, draft, model.ReviewSummary, draft.Title);
    }

    private async Task<StoryEntityAcceptResult> AcceptCharacterAsync(
        AcceptStoryEntityAiDraft request,
        CharacterAiDraftView draft,
        CancellationToken cancellationToken)
    {
        var privateMotivations = string.Empty;
        if (request.Session.EntityId.HasValue)
        {
            privateMotivations = (await chatStoryService.GetCharactersAsync(request.ThreadId, cancellationToken))
                .FirstOrDefault(x => x.CharacterId == request.Session.EntityId.Value)?
                .PrivateMotivations
                ?? string.Empty;
        }

        var saved = await chatStoryService.UpsertCharacterAsync(
            new UpsertCharacter(
                request.ThreadId,
                request.Session.EntityId,
                draft.Name,
                draft.Summary,
                draft.GeneralAppearance,
                draft.CorePersonality,
                draft.Relationships,
                draft.PreferencesBeliefs,
                privateMotivations,
                draft.IsPresentInScene,
                false),
            cancellationToken);

        return new StoryEntityAcceptResult(saved.CharacterId, StoryEntityKind.Character, saved.Name);
    }

    private async Task<StoryEntityAcceptResult> AcceptItemAsync(
        AcceptStoryEntityAiDraft request,
        ItemAiDraftView draft,
        CancellationToken cancellationToken)
    {
        var saved = await chatStoryService.UpsertItemAsync(
            new UpsertItem(
                request.ThreadId,
                request.Session.EntityId,
                draft.Name,
                draft.Summary,
                draft.Details,
                draft.OwnerCharacterId,
                draft.LocationId,
                draft.IsPresentInScene,
                false),
            cancellationToken);

        return new StoryEntityAcceptResult(saved.ItemId, StoryEntityKind.Item, saved.Name);
    }

    private async Task<StoryEntityAcceptResult> AcceptLocationAsync(
        AcceptStoryEntityAiDraft request,
        LocationAiDraftView draft,
        CancellationToken cancellationToken)
    {
        var saved = await chatStoryService.UpsertLocationAsync(
            new UpsertLocation(
                request.ThreadId,
                request.Session.EntityId,
                draft.Name,
                draft.Summary,
                draft.Details,
                false),
            cancellationToken);

        if (draft.IsCurrent)
            await chatStoryService.SetCurrentLocationAsync(new SetCurrentLocation(request.ThreadId, saved.LocationId), cancellationToken);
        else if (request.Session.EntityId.HasValue)
            await ClearCurrentLocationIfNeededAsync(request.ThreadId, request.Session.EntityId.Value, cancellationToken);

        return new StoryEntityAcceptResult(saved.LocationId, StoryEntityKind.Location, saved.Name);
    }

    private async Task<StoryEntityAcceptResult> AcceptHistoryFactAsync(
        AcceptStoryEntityAiDraft request,
        HistoryFactAiDraftView draft,
        CancellationToken cancellationToken)
    {
        var saved = await chatStoryService.UpsertHistoryFactAsync(
            new UpsertHistoryFact(
                request.ThreadId,
                request.Session.EntityId,
                draft.Title,
                draft.Summary,
                draft.Details,
                draft.Characters.Select(x => x.Id).ToList(),
                draft.Locations.Select(x => x.Id).ToList(),
                draft.Items.Select(x => x.Id).ToList()),
            cancellationToken);

        return new StoryEntityAcceptResult(saved.FactId, StoryEntityKind.HistoryFact, saved.Title);
    }

    private async Task<StoryEntityAcceptResult> AcceptTimelineEntryAsync(
        AcceptStoryEntityAiDraft request,
        TimelineEntryAiDraftView draft,
        CancellationToken cancellationToken)
    {
        var saved = await chatStoryService.UpsertTimelineEntryAsync(
            new UpsertTimelineEntry(
                request.ThreadId,
                request.Session.EntityId,
                draft.SortOrder,
                string.IsNullOrWhiteSpace(draft.WhenText) ? null : draft.WhenText.Trim(),
                draft.Title,
                draft.Summary,
                draft.Details,
                draft.Characters.Select(x => x.Id).ToList(),
                draft.Locations.Select(x => x.Id).ToList(),
                draft.Items.Select(x => x.Id).ToList()),
            cancellationToken);

        return new StoryEntityAcceptResult(saved.TimelineEntryId, StoryEntityKind.TimelineEntry, saved.Title);
    }

    private async Task ClearCurrentLocationIfNeededAsync(Guid threadId, Guid locationId, CancellationToken cancellationToken)
    {
        var snapshot = await LoadStorySnapshotAsync(threadId, cancellationToken);
        if (snapshot.Scene.CurrentLocationId != locationId)
            return;

        await chatStoryService.SetCurrentLocationAsync(new SetCurrentLocation(threadId, null), cancellationToken);
    }

    private ChatOptions BuildChatOptions(StorySnapshot snapshot)
    {
        [Description("Get the current scene and setting details for the active story.")]
        string GetSceneDetails() => snapshot.BuildSceneToolDetails();

        [Description("Get full details for a character in the active story by character name.")]
        string GetCharacterDetails(string characterName) => snapshot.BuildCharacterToolDetails(characterName);

        [Description("Get full details for a location in the active story by location name.")]
        string GetLocationDetails(string locationName) => snapshot.BuildLocationToolDetails(locationName);

        [Description("Get full details for an item in the active story by item name.")]
        string GetItemDetails(string itemName) => snapshot.BuildItemToolDetails(itemName);

        [Description("Get story history and timeline details related to a topic, title, or entity name.")]
        string GetHistoryDetails(string topic) => snapshot.BuildHistoryToolDetails(topic);

        return new ChatOptions
        {
            Temperature = 0.7f,
            Tools =
            [
                AIFunctionFactory.Create(GetSceneDetails),
                AIFunctionFactory.Create(GetCharacterDetails),
                AIFunctionFactory.Create(GetLocationDetails),
                AIFunctionFactory.Create(GetItemDetails),
                AIFunctionFactory.Create(GetHistoryDetails)
            ]
        };
    }

    private IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> BuildMessages(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId,
        string prompt,
        StoryEntityAiDraftSessionView? priorSession,
        IReadOnlyList<StoryEntityFieldGuidanceView> guidance)
    {
        var systemPrompt = $$"""
        You help authors create and edit structured story entities.
        Target entity kind: {{entityKind}}.
        Return only the structured output for the requested entity kind.
        Use the provided story summaries first, then call read-only tools whenever you need full details.
        Preserve continuity with the existing story.
        If editing an existing entity, revise it instead of creating a duplicate concept.
        Follow any field guidance exactly for output shape, detail level, and suggested length.
        For links to existing characters, locations, and items, return names from the current story when possible.
        {{BuildEntityFocusRules(snapshot, entityKind, entityId)}}
        """;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine(snapshot.BuildOverview(entityKind, entityId));
        var focusSection = BuildEntityFocusSection(snapshot, entityKind, entityId);
        if (!string.IsNullOrWhiteSpace(focusSection))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine(focusSection);
        }

        var guidanceSection = BuildFieldGuidanceSection(guidance);
        if (!string.IsNullOrWhiteSpace(guidanceSection))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Field guidance:");
            userPrompt.AppendLine(guidanceSection);
        }

        if (priorSession is not null)
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Current in-progress AI draft:");
            userPrompt.AppendLine(snapshot.BuildCurrentDraftSummary(priorSession));
        }

        userPrompt.AppendLine();
        userPrompt.AppendLine($"User prompt: {prompt}");

        return
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, systemPrompt),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userPrompt.ToString())
        ];
    }

    private static string BuildEntityFocusRules(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId) => entityKind switch
    {
        StoryEntityKind.Character => $$"""
        The target subject is {{BuildTargetSubject(snapshot, entityKind, entityId)}}.
        Every character field must stay centered on that target character rather than drifting into broad cast summary.
        Character summary must describe who the target character is in standalone terms first.
        Mention other characters only as secondary context, never as the main subject of the summary.
        Avoid defining the target only by reference to another character when stronger traits, vibe, role, appearance, or behavior are available.
        Character relationships must be written from the target character's perspective.
        In each relationship bullet, identify what the other person or place is to the target and how the target sees, treats, or relates to them.
        Do not reverse the direction by describing mainly how the other person sees the target.
        Private motivations are read-only context for the author and should inform tone and continuity, but must never be proposed as a new editable field in the structured response.
        """,
        _ => string.Empty
    };

    private static string BuildEntityFocusSection(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId) => entityKind switch
    {
        StoryEntityKind.Character => $$"""
        Target character focus:
        - Subject: {{BuildTargetSubject(snapshot, entityKind, entityId)}}
        - Keep the summary centered on the target character's own identity, vibe, role, appearance, or behavior.
        - Use other characters or places only as supporting context when they clarify the target.
        - For relationships, treat the target character as the implied speaker for every bullet.
        - A relationship line should answer "what is this person or place to the target, and how does the target view them?"
        - If a relationship mentions the other side's opinion, frame it only as something the target knows or believes.
        """,
        _ => string.Empty
    };

    private static string BuildFieldGuidanceSection(IReadOnlyList<StoryEntityFieldGuidanceView> guidance)
    {
        if (guidance.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var field in guidance)
        {
            builder.AppendLine($"- {field.Label}");
            builder.AppendLine($"  Format: {FormatGuidanceFormat(field.Guidance.Format)}");
            builder.AppendLine($"  Level of detail: {FormatDetailLevel(field.Guidance.LevelOfDetail)}");
            builder.AppendLine($"  Suggested length: {field.Guidance.SuggestedLength.Minimum}-{field.Guidance.SuggestedLength.Maximum} {FormatLengthMeasure(field.Guidance.Format)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatGuidanceFormat(StoryFieldGuidanceFormat guidanceFormat) => guidanceFormat switch
    {
        StoryFieldGuidanceFormat.Words => "list of words",
        StoryFieldGuidanceFormat.Sentences => "sentences",
        StoryFieldGuidanceFormat.Paragraphs => "paragraphs",
        StoryFieldGuidanceFormat.BulletList => "bullet list of items",
        _ => guidanceFormat.ToString()
    };

    private static string FormatDetailLevel(StoryFieldGuidanceDetailLevel detailLevel) => detailLevel switch
    {
        StoryFieldGuidanceDetailLevel.Simple => "simple",
        StoryFieldGuidanceDetailLevel.Medium => "medium",
        StoryFieldGuidanceDetailLevel.Detailed => "detailed",
        _ => detailLevel.ToString()
    };

    private static string FormatLengthMeasure(StoryFieldGuidanceFormat guidanceFormat) => guidanceFormat switch
    {
        StoryFieldGuidanceFormat.Words => "words",
        StoryFieldGuidanceFormat.Sentences => "sentences",
        StoryFieldGuidanceFormat.Paragraphs => "paragraphs",
        StoryFieldGuidanceFormat.BulletList => "items",
        _ => guidanceFormat.ToString().ToLowerInvariant()
    };

    private async Task<StorySnapshot> LoadStorySnapshotAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Generating the AI draft failed because the selected story could not be found.");

        return new StorySnapshot(
            story.ChatThreadId,
            story.Scene,
            story.Characters.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.Locations.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.Items.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.History);
    }

    private static void ValidateSession(Guid threadId, StoryEntityAiDraftSessionView session)
    {
        if (session.ThreadId != threadId)
            throw new InvalidOperationException("The AI draft session does not belong to the selected story.");

        if (session.Draft.EntityKind != session.EntityKind)
            throw new InvalidOperationException("The AI draft kind does not match the requested story entity kind.");
    }

    private static StoryEntityAiDraftSessionView CreateSession(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId,
        string prompt,
        IReadOnlyList<string> promptHistory,
        StoryEntityAiDraftView draft,
        string? reviewSummary,
        string displayName) =>
        new(
            Guid.NewGuid(),
            snapshot.ThreadId,
            entityKind,
            entityId,
            !entityId.HasValue,
            BuildReviewSummary(reviewSummary, displayName),
            prompt,
            promptHistory,
            draft);

    private static IReadOnlyList<StoryEntityReferenceView> ResolveReferences<TDocument>(
        IReadOnlyList<TDocument> documents,
        IReadOnlyList<string>? names,
        Func<TDocument, Guid> idSelector,
        Func<TDocument, string> nameSelector)
    {
        if (names is null || names.Count == 0)
            return [];

        var resolved = new List<StoryEntityReferenceView>();
        foreach (var name in names)
        {
            var match = ResolveEntity(documents, name, idSelector, nameSelector);
            if (match is null || resolved.Any(x => x.Id == match.Value.Id))
                continue;

            resolved.Add(new StoryEntityReferenceView(match.Value.Id, match.Value.Name));
        }

        return resolved;
    }

    private static StoryEntityReferenceView? ResolveOptionalReference(
        IReadOnlyList<StoryEntityReferenceView> references,
        string? requestedName,
        Guid? fallbackId)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return null;

        var match = references.FirstOrDefault(x =>
            x.Name.Equals(requestedName.Trim(), StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains(requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        return fallbackId.HasValue
            ? references.FirstOrDefault(x => x.Id == fallbackId.Value)
            : null;
    }

    private static (Guid Id, string Name)? ResolveEntity<TDocument>(
        IReadOnlyList<TDocument> documents,
        string? name,
        Func<TDocument, Guid> idSelector,
        Func<TDocument, string> nameSelector)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim();
        var exactMatch = documents.FirstOrDefault(x => string.Equals(nameSelector(x), normalized, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
            return (idSelector(exactMatch), nameSelector(exactMatch));

        var partialMatch = documents.FirstOrDefault(x => nameSelector(x).Contains(normalized, StringComparison.OrdinalIgnoreCase));
        return partialMatch is null
            ? null
            : (idSelector(partialMatch), nameSelector(partialMatch));
    }

    private static string RequireText(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        throw new InvalidOperationException($"Generating the AI draft failed because the model returned an empty {fieldName}.");
    }

    private static string BuildReviewSummary(string? reviewSummary, string displayName)
    {
        var trimmed = reviewSummary?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"Review the proposed updates for '{displayName}'."
            : trimmed;
    }

    private static int GetNextTimelineSortOrder(StorySnapshot snapshot) =>
        snapshot.History.TimelineEntries.Count == 0
            ? 1
            : snapshot.History.TimelineEntries.Max(x => x.SortOrder) + 1;

    private sealed class StorySnapshot(
        Guid threadId,
        ChatStorySceneDocument scene,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyList<StoryLocationDocument> locations,
        IReadOnlyList<StoryItemDocument> items,
        ChatStoryHistoryDocument history)
    {
        public Guid ThreadId { get; } = threadId;

        public ChatStorySceneDocument Scene { get; } = scene;

        public IReadOnlyList<StoryCharacterDocument> Characters { get; } = characters;

        public IReadOnlyList<StoryLocationDocument> Locations { get; } = locations;

        public IReadOnlyList<StoryItemDocument> Items { get; } = items;

        public ChatStoryHistoryDocument History { get; } = history;

        public string BuildOverview(StoryEntityKind entityKind, Guid? entityId)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Story context summary:");
            builder.AppendLine($"Setting / scene: {BuildSceneSummary()}");
            builder.AppendLine($"Characters: {BuildCharacterSummary()}");
            builder.AppendLine($"Locations: {BuildLocationSummary()}");
            builder.AppendLine($"Items: {BuildItemSummary()}");
            builder.AppendLine($"History summary: {BuildHistorySummary()}");
            builder.AppendLine();
            builder.AppendLine(entityId.HasValue
                ? $"Full details for the {BuildEntityLabel(entityKind)} being modified:"
                : $"This is a new {BuildEntityLabel(entityKind)}.");

            if (entityId.HasValue)
                builder.AppendLine(BuildEntityDetails(entityKind, entityId.Value));

            var relatedContext = BuildRelatedEntityContext(entityKind);
            if (!string.IsNullOrWhiteSpace(relatedContext))
            {
                builder.AppendLine();
                builder.AppendLine(relatedContext);
            }

            return builder.ToString().TrimEnd();
        }

        public string BuildCurrentDraftSummary(StoryEntityAiDraftSessionView session) => session.Draft switch
        {
            CharacterAiDraftView draft =>
                $$"""
                Name: {{draft.Name}}
                Summary: {{draft.Summary}}
                General appearance: {{draft.GeneralAppearance}}
                Core personality: {{draft.CorePersonality}}
                Relationships: {{draft.Relationships}}
                Preferences / beliefs: {{draft.PreferencesBeliefs}}
                Present in scene: {{draft.IsPresentInScene}}
                """,
            ItemAiDraftView draft =>
                $$"""
                Name: {{draft.Name}}
                Summary: {{draft.Summary}}
                Details: {{draft.Details}}
                Owner: {{draft.OwnerCharacterName ?? "None"}}
                Stored location: {{draft.LocationName ?? "None"}}
                Present in scene: {{draft.IsPresentInScene}}
                """,
            LocationAiDraftView draft =>
                $$"""
                Name: {{draft.Name}}
                Summary: {{draft.Summary}}
                Details: {{draft.Details}}
                Current location: {{draft.IsCurrent}}
                """,
            HistoryFactAiDraftView draft =>
                $$"""
                Title: {{draft.Title}}
                Summary: {{draft.Summary}}
                Details: {{draft.Details}}
                Characters: {{FormatReferenceNames(draft.Characters)}}
                Locations: {{FormatReferenceNames(draft.Locations)}}
                Items: {{FormatReferenceNames(draft.Items)}}
                """,
            TimelineEntryAiDraftView draft =>
                $$"""
                Sort order: {{draft.SortOrder}}
                When: {{draft.WhenText}}
                Title: {{draft.Title}}
                Summary: {{draft.Summary}}
                Details: {{draft.Details}}
                Characters: {{FormatReferenceNames(draft.Characters)}}
                Locations: {{FormatReferenceNames(draft.Locations)}}
                Items: {{FormatReferenceNames(draft.Items)}}
                """,
            _ => "No prior draft was available."
        };

        public string BuildSceneToolDetails()
        {
            var builder = new StringBuilder();
            builder.AppendLine(BuildSceneSummary());
            builder.AppendLine($"Present characters: {string.Join(", ", Characters.Where(x => Scene.PresentCharacterIds.Contains(x.Id)).Select(x => x.Name))}");
            builder.AppendLine($"Present items: {string.Join(", ", Items.Where(x => Scene.PresentItemIds.Contains(x.Id)).Select(x => x.Name))}");
            return builder.ToString().TrimEnd();
        }

        public string BuildCharacterToolDetails(string characterName)
        {
            var character = Characters.FirstOrDefault(x => MatchesName(x.Name, characterName));
            return character is null ? $"No character matched '{characterName}'." : BuildCharacterDetails(character.Id);
        }

        public string BuildLocationToolDetails(string locationName)
        {
            var location = Locations.FirstOrDefault(x => MatchesName(x.Name, locationName));
            return location is null ? $"No location matched '{locationName}'." : BuildLocationDetails(location.Id);
        }

        public string BuildItemToolDetails(string itemName)
        {
            var item = Items.FirstOrDefault(x => MatchesName(x.Name, itemName));
            return item is null ? $"No item matched '{itemName}'." : BuildItemDetails(item.Id);
        }

        public string BuildHistoryToolDetails(string topic)
        {
            var normalized = topic.Trim();
            var facts = History.Facts
                .Where(x => string.IsNullOrWhiteSpace(normalized)
                    || x.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || x.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || x.Details.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"Fact: {x.Title} | {x.Summary} | {x.Details} | Links: {BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds)}");
            var timeline = History.TimelineEntries
                .Where(x => string.IsNullOrWhiteSpace(normalized)
                    || x.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || x.Summary.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || x.Details.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || (x.WhenText?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
                    || BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"Timeline: {x.WhenText} | {x.Title} | {x.Summary} | {x.Details} | Links: {BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds)}");
            var combined = facts.Concat(timeline).Take(12).ToList();
            return combined.Count == 0
                ? $"No story history matched '{topic}'."
                : string.Join(Environment.NewLine, combined);
        }

        private string BuildSceneSummary()
        {
            var currentLocation = Scene.CurrentLocationId.HasValue
                ? Locations.FirstOrDefault(x => x.Id == Scene.CurrentLocationId.Value)?.Name
                : null;
            var notes = new[]
            {
                currentLocation is null ? null : $"Current location: {currentLocation}",
                Scene.DerivedContextSummary,
                Scene.ManualContextNotes
            }.Where(x => !string.IsNullOrWhiteSpace(x));

            var text = string.Join(" | ", notes);
            return string.IsNullOrWhiteSpace(text) ? "No explicit setting notes yet." : text;
        }

        private string BuildRelatedEntityContext(StoryEntityKind entityKind) => entityKind switch
        {
            StoryEntityKind.Character => BuildReferenceCatalog(),
            StoryEntityKind.Item => BuildReferenceCatalog(),
            StoryEntityKind.HistoryFact => BuildReferenceCatalog(),
            StoryEntityKind.TimelineEntry => BuildReferenceCatalog(),
            _ => string.Empty
        };

        private string BuildCharacterSummary() =>
            Characters.Count == 0
                ? "None yet."
                : string.Join("; ", Characters.Select(x => $"{x.Name} [{x.Id}] - {x.Summary}"));

        private string BuildLocationSummary() =>
            Locations.Count == 0
                ? "None yet."
                : string.Join("; ", Locations.Select(x => $"{x.Name} [{x.Id}] - {x.Summary}"));

        private string BuildItemSummary() =>
            Items.Count == 0
                ? "None yet."
                : string.Join("; ", Items.Select(x => $"{x.Name} [{x.Id}] - {x.Summary}"));

        private string BuildHistorySummary()
        {
            var facts = History.Facts.Take(3).Select(x => $"{x.Title}: {x.Summary}");
            var timeline = History.TimelineEntries.Take(3).Select(x => $"{x.Title}: {x.Summary}");
            var combined = facts.Concat(timeline).ToList();
            return combined.Count == 0 ? "No history yet." : string.Join(" | ", combined);
        }

        private string BuildEntityDetails(StoryEntityKind entityKind, Guid entityId) => entityKind switch
        {
            StoryEntityKind.Character => BuildCharacterDetails(entityId),
            StoryEntityKind.Item => BuildItemDetails(entityId),
            StoryEntityKind.Location => BuildLocationDetails(entityId),
            StoryEntityKind.HistoryFact => BuildHistoryFactDetails(entityId),
            StoryEntityKind.TimelineEntry => BuildTimelineEntryDetails(entityId),
            _ => "No entity details were available."
        };

        private string BuildReferenceCatalog()
        {
            var builder = new StringBuilder();
            builder.AppendLine("Available link targets:");
            builder.AppendLine($"Characters: {FormatNameList(Characters.Select(x => x.Name))}");
            builder.AppendLine($"Locations: {FormatNameList(Locations.Select(x => x.Name))}");
            builder.AppendLine($"Items: {FormatNameList(Items.Select(x => x.Name))}");
            return builder.ToString().TrimEnd();
        }

        private string BuildCharacterDetails(Guid entityId)
        {
            var character = Characters.FirstOrDefault(x => x.Id == entityId);
            return character is null
                ? "Character not found."
                : $$"""
                Name: {{character.Name}}
                Summary: {{character.Summary}}
                General appearance: {{character.GeneralAppearance}}
                Core personality: {{character.CorePersonality}}
                Relationships: {{character.Relationships}}
                Preferences / beliefs: {{character.PreferencesBeliefs}}
                Private motivations: {{character.PrivateMotivations}}
                Present in scene: {{Scene.PresentCharacterIds.Contains(character.Id)}}
                """;
        }

        private string BuildItemDetails(Guid entityId)
        {
            var item = Items.FirstOrDefault(x => x.Id == entityId);
            if (item is null)
                return "Item not found.";

            var owner = item.OwnerCharacterId.HasValue
                ? Characters.FirstOrDefault(x => x.Id == item.OwnerCharacterId.Value)?.Name
                : null;
            var location = item.LocationId.HasValue
                ? Locations.FirstOrDefault(x => x.Id == item.LocationId.Value)?.Name
                : null;

            return $$"""
            Name: {{item.Name}}
            Summary: {{item.Summary}}
            Details: {{item.Details}}
            Owner: {{owner ?? "None"}}
            Stored location: {{location ?? "None"}}
            Present in scene: {{Scene.PresentItemIds.Contains(item.Id)}}
            """;
        }

        private string BuildLocationDetails(Guid entityId)
        {
            var location = Locations.FirstOrDefault(x => x.Id == entityId);
            return location is null
                ? "Location not found."
                : $$"""
                Name: {{location.Name}}
                Summary: {{location.Summary}}
                Details: {{location.Details}}
                Current location: {{Scene.CurrentLocationId == location.Id}}
                """;
        }

        private string BuildHistoryFactDetails(Guid entityId)
        {
            var fact = History.Facts.FirstOrDefault(x => x.Id == entityId);
            return fact is null
                ? "History fact not found."
                : $$"""
                Title: {{fact.Title}}
                Summary: {{fact.Summary}}
                Details: {{fact.Details}}
                Characters: {{ResolveLinkedNames(fact.CharacterIds, Characters)}}
                Locations: {{ResolveLinkedNames(fact.LocationIds, Locations)}}
                Items: {{ResolveLinkedNames(fact.ItemIds, Items)}}
                """;
        }

        private string BuildTimelineEntryDetails(Guid entityId)
        {
            var entry = History.TimelineEntries.FirstOrDefault(x => x.Id == entityId);
            return entry is null
                ? "Timeline entry not found."
                : $$"""
                Sort order: {{entry.SortOrder}}
                When: {{entry.WhenText ?? "None"}}
                Title: {{entry.Title}}
                Summary: {{entry.Summary}}
                Details: {{entry.Details}}
                Characters: {{ResolveLinkedNames(entry.CharacterIds, Characters)}}
                Locations: {{ResolveLinkedNames(entry.LocationIds, Locations)}}
                Items: {{ResolveLinkedNames(entry.ItemIds, Items)}}
                """;
        }

        private string BuildLinkedNames(
            IReadOnlyList<Guid> characterIds,
            IReadOnlyList<Guid> locationIds,
            IReadOnlyList<Guid> itemIds) =>
            string.Join(
                "; ",
                new[]
                {
                    $"Characters: {ResolveLinkedNames(characterIds, Characters)}",
                    $"Locations: {ResolveLinkedNames(locationIds, Locations)}",
                    $"Items: {ResolveLinkedNames(itemIds, Items)}"
                });

        private static string FormatNameList(IEnumerable<string> names)
        {
            var values = names.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return values.Count == 0 ? "None available." : string.Join(", ", values);
        }

        private static string FormatReferenceNames(IReadOnlyList<StoryEntityReferenceView> references) =>
            references.Count == 0 ? "None" : string.Join(", ", references.Select(x => x.Name));

        private static string ResolveLinkedNames<TDocument>(
            IReadOnlyList<Guid> ids,
            IReadOnlyList<TDocument> documents) where TDocument : notnull
        {
            if (ids.Count == 0)
                return "None";

            var names = documents
                .Select(document => document switch
                {
                    StoryCharacterDocument character when ids.Contains(character.Id) => character.Name,
                    StoryLocationDocument location when ids.Contains(location.Id) => location.Name,
                    StoryItemDocument item when ids.Contains(item.Id) => item.Name,
                    _ => null
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            return names.Count == 0 ? "None" : string.Join(", ", names);
        }

        private static string BuildEntityLabel(StoryEntityKind entityKind) => entityKind switch
        {
            StoryEntityKind.Character => "character",
            StoryEntityKind.Item => "item",
            StoryEntityKind.Location => "location",
            StoryEntityKind.HistoryFact => "history fact",
            StoryEntityKind.TimelineEntry => "timeline entry",
            _ => "entity"
        };

        private static bool MatchesName(string value, string requestedName) =>
            value.Equals(requestedName.Trim(), StringComparison.OrdinalIgnoreCase)
            || value.Contains(requestedName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTargetSubject(
        StorySnapshot snapshot,
        StoryEntityKind entityKind,
        Guid? entityId)
    {
        if (!entityId.HasValue)
            return entityKind switch
            {
                StoryEntityKind.Character => "the new character being drafted",
                StoryEntityKind.Item => "the new item being drafted",
                StoryEntityKind.Location => "the new location being drafted",
                StoryEntityKind.HistoryFact => "the new history fact being drafted",
                StoryEntityKind.TimelineEntry => "the new timeline entry being drafted",
                _ => "the entity being drafted"
            };

        return entityKind switch
        {
            StoryEntityKind.Character => snapshot.Characters.FirstOrDefault(x => x.Id == entityId.Value)?.Name is { Length: > 0 } name
                ? $"character '{name}'"
                : "the character being edited",
            StoryEntityKind.Item => snapshot.Items.FirstOrDefault(x => x.Id == entityId.Value)?.Name is { Length: > 0 } name
                ? $"item '{name}'"
                : "the item being edited",
            StoryEntityKind.Location => snapshot.Locations.FirstOrDefault(x => x.Id == entityId.Value)?.Name is { Length: > 0 } name
                ? $"location '{name}'"
                : "the location being edited",
            StoryEntityKind.HistoryFact => snapshot.History.Facts.FirstOrDefault(x => x.Id == entityId.Value)?.Title is { Length: > 0 } title
                ? $"history fact '{title}'"
                : "the history fact being edited",
            StoryEntityKind.TimelineEntry => snapshot.History.TimelineEntries.FirstOrDefault(x => x.Id == entityId.Value)?.Title is { Length: > 0 } title
                ? $"timeline entry '{title}'"
                : "the timeline entry being edited",
            _ => "the entity being edited"
        };
    }

    private sealed record CharacterAiResponse(
        string ReviewSummary,
        string Name,
        string? Summary,
        string? GeneralAppearance,
        string? CorePersonality,
        string? Relationships,
        string? PreferencesBeliefs,
        bool IsPresentInScene);

    private sealed record ItemAiResponse(
        string ReviewSummary,
        string Name,
        string? Summary,
        string? Details,
        string? OwnerCharacterName,
        string? LocationName,
        bool IsPresentInScene);

    private sealed record LocationAiResponse(
        string ReviewSummary,
        string Name,
        string? Summary,
        string? Details,
        bool IsCurrent);

    private sealed record HistoryFactAiResponse(
        string ReviewSummary,
        string Title,
        string? Summary,
        string? Details,
        IReadOnlyList<string>? CharacterNames,
        IReadOnlyList<string>? LocationNames,
        IReadOnlyList<string>? ItemNames);

    private sealed record TimelineEntryAiResponse(
        string ReviewSummary,
        string? WhenText,
        string Title,
        string? Summary,
        string? Details,
        IReadOnlyList<string>? CharacterNames,
        IReadOnlyList<string>? LocationNames,
        IReadOnlyList<string>? ItemNames);
}
