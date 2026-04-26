using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DbAppContext = AgentRp.Data.AppContext;
using DbChatMessage = AgentRp.Data.ChatMessage;

namespace AgentRp.Services;

public sealed class StoryChatAppearanceReplayService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IThreadAgentService threadAgentService,
    IStoryScenePromptLibraryService promptLibraryService) : IStoryChatAppearanceReplayService
{
    public async Task<StoryChatAppearanceReplayView> CreateReplayAsync(
        CreateStoryChatAppearanceReplay request,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(request.ThreadId, cancellationToken);
        var savedAppearanceByMessageId = context.MatchingAppearanceEntries
            .GroupBy(x => x.CoveredThroughMessageId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.CreatedUtc).First());

        return new StoryChatAppearanceReplayView(
            request.ThreadId,
            context.SelectedPath.Select(message =>
                {
                    savedAppearanceByMessageId.TryGetValue(message.Id, out var savedAppearance);
                    return new StoryChatAppearanceReplayTranscriptItemView(
                        message.Id,
                        message.CreatedUtc,
                        ResolveSpeakerName(message, context.Story.Characters.Entries),
                        message.Content,
                        MapEntry(savedAppearance, context.Story, true));
                })
                .ToList());
    }

    public async Task<StoryChatAppearanceReplayGeneratedStepView> GenerateReplayStepAsync(
        GenerateStoryChatAppearanceReplayStep request,
        IReadOnlyList<StorySceneCharacterAppearanceView>? priorOverride,
        CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(request.ThreadId, cancellationToken);
        var selectedMessage = context.SelectedPath.FirstOrDefault(x => x.Id == request.MessageId)
            ?? throw new InvalidOperationException("Generating the appearance replay step failed because the selected message is not on the active branch.");
        var savedAppearance = context.MatchingAppearanceEntries
            .Where(x => x.CoveredThroughMessageId == request.MessageId)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefault();
        var baselineEntry = request.UseEntireTranscript
            ? null
            : priorOverride is null
            ? context.MatchingAppearanceEntries
                .Where(x => x.CoveredThroughUtc < selectedMessage.CreatedUtc)
                .OrderByDescending(x => x.CoveredThroughUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefault()
            : null;
        var previousCharacters = !request.UseEntireTranscript && priorOverride is not null
            ? BuildCharacterViews(context.Story, priorOverride.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance?.Trim() ?? string.Empty))
            : BuildCharacterViews(context.Story, BuildAppearanceLookup(baselineEntry));
        var promptMessages = request.UseEntireTranscript
            ? context.SelectedPath
            : priorOverride is not null
            ? [selectedMessage]
            : context.SelectedPath
                .Where(x => baselineEntry is null || x.CreatedUtc > baselineEntry.CoveredThroughUtc)
                .Where(x => x.CreatedUtc <= selectedMessage.CreatedUtc)
                .ToList();

        var agent = await threadAgentService.GetSelectedAgentAsync(request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Generating the appearance replay step failed because no AI provider is configured for this chat.");
        var transcriptMessages = promptMessages
            .Select(message => new StorySceneTranscriptMessage(
                message.Id,
                message.CreatedUtc,
                ResolveSpeakerName(message, context.Story.Characters.Entries),
                message.MessageKind == ChatMessageKind.Narration,
                message.Content,
                message.SpeakerCharacterId,
                null))
            .ToList();
        var prompt = await promptLibraryService.RenderAppearancePromptAsync(
            request.ThreadId,
            BuildPromptCharacters(context.Story, previousCharacters),
            transcriptMessages,
            context.Story.StoryContext.ExplicitContent,
            context.Story.StoryContext.ViolentContent,
            cancellationToken);

        var response = await agent.ChatClient.GetResponseAsync<AppearanceReplayResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt.SystemPrompt),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt.UserPrompt)
            ],
            options: new ChatOptions { Temperature = 0.2f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var resolvedCharacters = ResolveCharactersFromResponse(context.Story, previousCharacters, response.Result.Characters);
        var previousLookup = previousCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);

        return new StoryChatAppearanceReplayGeneratedStepView(
            selectedMessage.Id,
            transcriptMessages.FirstOrDefault()?.MessageId,
            selectedMessage.Id,
            MapEntry(savedAppearance, context.Story, true),
            resolvedCharacters.Select(character =>
                {
                    previousLookup.TryGetValue(character.CharacterId, out var previousAppearance);
                    previousAppearance ??= string.Empty;
                    return new StoryChatAppearanceReplayCharacterView(
                        character.CharacterId,
                        character.CharacterName,
                        previousAppearance,
                        character.CurrentAppearance,
                        !string.Equals(CollapseWhitespace(previousAppearance), CollapseWhitespace(character.CurrentAppearance), StringComparison.Ordinal));
                })
                .ToList(),
            StoryMessageTokenUsageMapper.Map(response.Usage));
    }

    private async Task<AppearanceReplayContext> LoadContextAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading the appearance replay failed because the selected chat could not be found.");
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? new ChatStory
            {
                ChatThreadId = threadId,
                CreatedUtc = thread.CreatedUtc,
                UpdatedUtc = thread.UpdatedUtc
            };
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(messages, thread.ActiveLeafMessageId);
        if (!selectedLeafMessageId.HasValue)
            return new AppearanceReplayContext(story, [], []);

        var selectedPath = BuildSelectedPath(messages, selectedLeafMessageId.Value);
        var selectedPathIds = selectedPath.Select(x => x.Id).ToHashSet();
        var matchingAppearanceEntries = await dbContext.StoryChatAppearanceEntries
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return new AppearanceReplayContext(
            story,
            selectedPath,
            matchingAppearanceEntries
                .Where(x => selectedPathIds.Contains(x.SelectedLeafMessageId) && selectedPathIds.Contains(x.CoveredThroughMessageId))
                .ToList());
    }

    private static IReadOnlyList<StorySceneCharacterAppearanceView> ResolveCharactersFromResponse(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> fallbackCharacters,
        IReadOnlyList<AppearanceReplayCharacterResponse>? responseCharacters)
    {
        var mappedCharacters = responseCharacters?
            .Where(x => !string.IsNullOrWhiteSpace(x.CharacterName))
            .Select(x => ResolveCharacterFromResponse(story, x))
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => x.CharacterId)
            .ToDictionary(x => x.Key, x => x.Last())
        ?? [];
        var resolvedCharacters = fallbackCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance?.Trim() ?? string.Empty);
        foreach (var mappedCharacter in mappedCharacters.Values)
            resolvedCharacters[mappedCharacter.CharacterId] = mappedCharacter.CurrentAppearance.Trim();

        return BuildCharacterViews(story, resolvedCharacters);
    }

    private static StorySceneCharacterAppearanceView? ResolveCharacterFromResponse(
        ChatStory story,
        AppearanceReplayCharacterResponse response)
    {
        var requestedName = response.CharacterName.Trim();
        var character = story.Characters.Entries.FirstOrDefault(x =>
            !x.IsArchived
            && story.Scene.PresentCharacterIds.Contains(x.Id)
            && (x.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
                || x.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase)));
        if (character is null)
            return null;

        var currentAppearance = response.HasCurrentSceneState ? response.CurrentAppearance?.Trim() ?? string.Empty : string.Empty;
        return new StorySceneCharacterAppearanceView(character.Id, character.Name, currentAppearance);
    }

    private static IReadOnlyList<StorySceneAppearancePromptCharacter> BuildPromptCharacters(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> latestCharacters)
    {
        var latestCharactersById = latestCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);
        return GetPresentCharacters(story)
            .Select(character => new StorySceneAppearancePromptCharacter(
                character.Name,
                latestCharactersById.TryGetValue(character.Id, out var currentAppearance) ? currentAppearance : string.Empty))
            .ToList();
    }

    private static IReadOnlyList<StoryCharacterDocument> GetPresentCharacters(ChatStory story) =>
        story.Characters.Entries
            .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<StorySceneCharacterAppearanceView> BuildCharacterViews(
        ChatStory story,
        IReadOnlyDictionary<Guid, string> appearanceLookup)
    {
        var characters = new List<StorySceneCharacterAppearanceView>();
        foreach (var character in GetPresentCharacters(story))
        {
            appearanceLookup.TryGetValue(character.Id, out var currentAppearance);
            characters.Add(new StorySceneCharacterAppearanceView(character.Id, character.Name, currentAppearance ?? string.Empty));
        }

        return characters;
    }

    private static IReadOnlyDictionary<Guid, string> BuildAppearanceLookup(StoryChatAppearanceEntry? entry) =>
        entry is null
            ? new Dictionary<Guid, string>()
            : StoryChatAppearanceDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(entry.AppearanceJson, StoryChatAppearanceDocument.Empty)).Characters
                .ToDictionary(x => x.CharacterId, x => x.CurrentAppearance.Trim());

    private static StorySceneAppearanceEntryView? MapEntry(
        StoryChatAppearanceEntry? entry,
        ChatStory story,
        bool canEdit,
        IReadOnlyList<StorySceneCharacterAppearanceView>? effectiveCharactersOverride = null)
    {
        if (entry is null)
            return null;

        return new StorySceneAppearanceEntryView(
            entry.Id,
            entry.CoveredThroughMessageId,
            entry.CreatedUtc,
            entry.Summary,
            canEdit,
            effectiveCharactersOverride ?? BuildCharacterViews(story, BuildAppearanceLookup(entry)));
    }

    private static StoryChatSnapshotSummaryView MapSnapshot(StoryChatSnapshot snapshot)
    {
        var document = ChatStoryJson.Deserialize(snapshot.SnapshotJson, StoryChatSnapshotDocument.Empty);
        var includedMessageIds = ChatStoryJson.Deserialize(snapshot.IncludedMessageIdsJson, new List<Guid>());

        return new StoryChatSnapshotSummaryView(
            snapshot.Id,
            snapshot.Summary,
            snapshot.CreatedUtc,
            snapshot.SelectedLeafMessageId,
            snapshot.CoveredThroughMessageId,
            snapshot.CoveredThroughUtc,
            includedMessageIds.Count,
            document.Facts.Count,
            document.TimelineEntries.Count);
    }

    private static string ResolveSpeakerName(DbChatMessage message, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (message.MessageKind == ChatMessageKind.Narration)
            return "Narrator";

        if (message.SpeakerCharacterId.HasValue)
            return characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)?.Name ?? "Unknown Character";

        return "Unknown Character";
    }

    private static Guid? ResolveSelectedLeafMessageId(IReadOnlyList<DbChatMessage> messages, Guid? selectedLeafMessageId)
    {
        if (messages.Count == 0)
            return null;

        if (selectedLeafMessageId.HasValue && messages.Any(x => x.Id == selectedLeafMessageId.Value))
            return selectedLeafMessageId.Value;

        return FindLatestLeaf(messages);
    }

    private static IReadOnlyList<DbChatMessage> BuildSelectedPath(IReadOnlyList<DbChatMessage> messages, Guid selectedLeafMessageId)
    {
        var messageMap = messages.ToDictionary(x => x.Id);
        if (!messageMap.ContainsKey(selectedLeafMessageId))
            return [];

        var path = new List<DbChatMessage>();
        Guid? currentId = selectedLeafMessageId;

        while (currentId.HasValue && messageMap.TryGetValue(currentId.Value, out var current))
        {
            path.Add(current);
            currentId = current.ParentMessageId;
        }

        path.Reverse();
        return path;
    }

    private static Guid FindLatestLeaf(IReadOnlyList<DbChatMessage> messages)
    {
        var parentIds = messages
            .Where(x => x.ParentMessageId.HasValue)
            .Select(x => x.ParentMessageId!.Value)
            .ToHashSet();

        return messages
            .Where(x => !parentIds.Contains(x.Id))
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.Id)
            .First();
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record AppearanceReplayResponse(
        string? Summary,
        IReadOnlyList<AppearanceReplayCharacterResponse>? Characters);

    private sealed record AppearanceReplayCharacterResponse(
        string CharacterName,
        bool HasCurrentSceneState,
        string? CurrentAppearance);

    private sealed record AppearanceReplayContext(
        ChatStory Story,
        IReadOnlyList<DbChatMessage> SelectedPath,
        IReadOnlyList<StoryChatAppearanceEntry> MatchingAppearanceEntries);
}
