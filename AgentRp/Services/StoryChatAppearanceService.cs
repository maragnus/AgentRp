using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DbAppContext = AgentRp.Data.AppContext;
using DbChatMessage = AgentRp.Data.ChatMessage;

namespace AgentRp.Services;

public sealed class StoryChatAppearanceService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IThreadAgentService threadAgentService) : IStoryChatAppearanceService
{

    public async Task<StorySceneAppearanceResolution> ResolveLatestAppearanceAsync(
        Guid threadId,
        IReadOnlyList<DbChatMessage> selectedPath,
        ChatStory story,
        bool writeChanges,
        CancellationToken cancellationToken)
    {
        if (selectedPath.Count == 0)
            return new StorySceneAppearanceResolution(null, BuildEffectiveCharacters(story, null), []);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var latestEntry = await GetLatestMatchingEntryAsync(dbContext, threadId, selectedPath, cancellationToken);
        var latestEffectiveCharacters = BuildEffectiveCharacters(story, latestEntry);
        var transcriptSinceLatestEntry = BuildTranscriptSinceLatestEntry(selectedPath, latestEntry, story.Characters.Entries);

        if (!writeChanges || transcriptSinceLatestEntry.Count == 0 || story.Scene.PresentCharacterIds.Count == 0)
            return new StorySceneAppearanceResolution(
                MapEntry(latestEntry, story, latestEntry is not null, latestEffectiveCharacters),
                latestEffectiveCharacters,
                transcriptSinceLatestEntry);

        var agent = await threadAgentService.GetSelectedAgentAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("Resolving current appearance failed because no AI provider is configured for this chat.");

        var response = await agent.ChatClient.GetResponseAsync<AppearanceStageResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, StorySceneAppearancePromptBuilder.BuildSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    StorySceneAppearancePromptBuilder.BuildUserPrompt(
                        BuildPromptCharacters(story, latestEffectiveCharacters),
                        transcriptSinceLatestEntry,
                        story.StoryContext.ExplicitContent,
                        story.StoryContext.ViolentContent))
            ],
            options: new ChatOptions { Temperature = 0.2f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);

        var resolvedCharacters = ResolveCharactersFromResponse(story, latestEffectiveCharacters, response.Result.Characters);
        if (AreEquivalent(latestEffectiveCharacters, resolvedCharacters))
        {
            if (latestEntry is null)
            {
                var createdEntry = await CreateEntryAsync(
                    dbContext,
                    threadId,
                    selectedPath[^1],
                    resolvedCharacters,
                    response.Result.Summary,
                    cancellationToken);

                return new StorySceneAppearanceResolution(
                    MapEntry(createdEntry, story, true, resolvedCharacters),
                    resolvedCharacters,
                    transcriptSinceLatestEntry);
            }

            return new StorySceneAppearanceResolution(
                MapEntry(latestEntry, story, latestEntry is not null, latestEffectiveCharacters),
                latestEffectiveCharacters,
                transcriptSinceLatestEntry);
        }

        var entry = await CreateEntryAsync(
            dbContext,
            threadId,
            selectedPath[^1],
            resolvedCharacters,
            response.Result.Summary,
            cancellationToken);

        return new StorySceneAppearanceResolution(
            MapEntry(entry, story, true, resolvedCharacters),
            resolvedCharacters,
            transcriptSinceLatestEntry);
    }

    public async Task<IReadOnlyList<StorySceneAppearanceEntryView>> GetEntriesForPathAsync(
        Guid threadId,
        IReadOnlyList<DbChatMessage> selectedPath,
        ChatStory story,
        CancellationToken cancellationToken)
    {
        if (selectedPath.Count == 0)
            return [];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var selectedPathIds = selectedPath.Select(x => x.Id).ToHashSet();
        var entries = await dbContext.StoryChatAppearanceEntries
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        var matchingEntries = entries
            .Where(x => selectedPathIds.Contains(x.SelectedLeafMessageId) && selectedPathIds.Contains(x.CoveredThroughMessageId))
            .ToList();
        var latestEntryId = matchingEntries.LastOrDefault()?.Id;

        var views = new List<StorySceneAppearanceEntryView>(matchingEntries.Count);
        foreach (var entry in matchingEntries)
            views.Add(MapEntry(
                entry,
                story,
                latestEntryId == entry.Id,
                latestEntryId == entry.Id ? BuildEffectiveCharacters(story, entry) : null)
                ?? throw new InvalidOperationException("Building appearance history failed because a matching appearance entry could not be mapped."));

        return views;
    }

    public async Task<StorySceneAppearanceEntryView> UpdateLatestEntryAsync(
        UpdateStorySceneAppearanceEntry request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Saving the latest character appearance block failed because the selected chat could not be found.");
        var story = await dbContext.ChatStories
            .FirstOrDefaultAsync(x => x.ChatThreadId == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Saving the latest character appearance block failed because the selected story could not be found.");
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == request.ThreadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(messages, thread.ActiveLeafMessageId);
        if (!selectedLeafMessageId.HasValue)
            throw new InvalidOperationException("Saving the latest character appearance block failed because the active branch could not be found.");

        var selectedPath = BuildSelectedPath(messages, selectedLeafMessageId.Value);
        var latestEntry = await GetLatestMatchingEntryAsync(dbContext, request.ThreadId, selectedPath, cancellationToken)
            ?? throw new InvalidOperationException("Saving the latest character appearance block failed because no editable appearance block exists on the active branch.");
        if (latestEntry.Id != request.AppearanceEntryId)
            throw new InvalidOperationException("Saving the latest character appearance block failed because only the latest appearance block on the active branch can be edited.");

        var normalizedCharacters = BuildUpdatedCharacters(story, request.Characters);
        latestEntry.AppearanceJson = ChatStoryJson.Serialize(StoryChatAppearanceDocumentNormalizer.Normalize(new StoryChatAppearanceDocument(
            normalizedCharacters
                .Select(x => new StoryChatCharacterAppearanceDocument(x.CharacterId, x.CurrentAppearance))
                .ToList())));
        latestEntry.Summary = BuildEntrySummary(null, normalizedCharacters);
        latestEntry.UpdatedUtc = DateTime.UtcNow;
        thread.UpdatedUtc = latestEntry.UpdatedUtc;

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapEntry(latestEntry, story, true, normalizedCharacters)
            ?? throw new InvalidOperationException("Saving the latest character appearance block failed because the updated appearance entry could not be mapped.");
    }

    private static IReadOnlyList<StorySceneCharacterAppearanceView> BuildEffectiveCharacters(
        ChatStory story,
        StoryChatAppearanceEntry? latestEntry)
    {
        return BuildCharacterViews(story, BuildAppearanceLookup(latestEntry));
    }

    private static IReadOnlyList<StorySceneCharacterAppearanceView> BuildUpdatedCharacters(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> requestCharacters)
    {
        var requestedLookup = requestCharacters.ToDictionary(x => x.CharacterId, x => NormalizeAppearanceText(x.CurrentAppearance));
        return BuildCharacterViews(story, requestedLookup);
    }

    private static IReadOnlyList<StorySceneCharacterAppearanceView> ResolveCharactersFromResponse(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> fallbackCharacters,
        IReadOnlyList<AppearanceStageCharacterResponse>? responseCharacters)
    {
        var mappedCharacters = responseCharacters?
            .Where(x => !string.IsNullOrWhiteSpace(x.CharacterName))
            .Select(x => ResolveCharacterFromResponse(story, x))
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => x.CharacterId)
            .ToDictionary(x => x.Key, x => x.Last())
        ?? [];

        var resolvedCharacters = fallbackCharacters.ToDictionary(x => x.CharacterId, x => NormalizeAppearanceText(x.CurrentAppearance));
        foreach (var mappedCharacter in mappedCharacters.Values)
        {
            resolvedCharacters[mappedCharacter.CharacterId] = NormalizeAppearanceText(mappedCharacter.CurrentAppearance);
        }

        return BuildCharacterViews(story, resolvedCharacters);
    }

    private static StorySceneCharacterAppearanceView? ResolveCharacterFromResponse(
        ChatStory story,
        AppearanceStageCharacterResponse response)
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

    private static IReadOnlyList<StorySceneTranscriptMessage> BuildTranscriptSinceLatestEntry(
        IReadOnlyList<DbChatMessage> selectedPath,
        StoryChatAppearanceEntry? latestEntry,
        IReadOnlyList<StoryCharacterDocument> characters)
    {
        var messages = latestEntry is null
            ? selectedPath
            : selectedPath.Where(x => x.CreatedUtc > latestEntry.CoveredThroughUtc).ToList();

        return messages
            .Select(message => new StorySceneTranscriptMessage(
                message.Id,
                message.CreatedUtc,
                ResolveSpeakerName(message, characters),
                message.MessageKind == ChatMessageKind.Narration,
                message.Content))
            .ToList();
    }

    private static StorySceneAppearanceEntryView? MapEntry(
        StoryChatAppearanceEntry? entry,
        ChatStory story,
        bool canEdit,
        IReadOnlyList<StorySceneCharacterAppearanceView>? effectiveCharactersOverride = null)
    {
        if (entry is null)
            return null;

        var characters = effectiveCharactersOverride
            ?? BuildCharacterViews(story, BuildAppearanceLookup(entry));

        return new StorySceneAppearanceEntryView(
            entry.Id,
            entry.CoveredThroughMessageId,
            entry.CreatedUtc,
            entry.Summary,
            canEdit,
            characters);
    }

    private static bool AreEquivalent(
        IReadOnlyList<StorySceneCharacterAppearanceView> left,
        IReadOnlyList<StorySceneCharacterAppearanceView> right)
    {
        if (left.Count != right.Count)
            return false;

        var leftLookup = left.ToDictionary(x => x.CharacterId, x => CollapseWhitespace(x.CurrentAppearance));
        foreach (var character in right)
        {
            if (!leftLookup.TryGetValue(character.CharacterId, out var currentAppearance))
                return false;
            if (!string.Equals(currentAppearance, CollapseWhitespace(character.CurrentAppearance), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string BuildEntrySummary(
        string? suggestedSummary,
        IReadOnlyList<StorySceneCharacterAppearanceView> characters)
    {
        var namedCharacters = characters
            .Where(x => !string.IsNullOrWhiteSpace(x.CurrentAppearance))
            .Select(x => x.CharacterName)
            .ToList();
        var trimmedSummary = suggestedSummary?.Trim();
        if (namedCharacters.Count > 0 && !string.IsNullOrWhiteSpace(trimmedSummary))
            return trimmedSummary;

        if (namedCharacters.Count == 0)
            return "No current physical details are captured for the active scene cast.";
        if (namedCharacters.Count == 1)
            return $"Current physical details updated for {namedCharacters[0]}.";

        return $"Current physical details updated for {string.Join(", ", namedCharacters)}.";
    }

    private static IReadOnlyList<StoryCharacterDocument> GetPresentCharacters(ChatStory story) =>
        story.Characters.Entries
            .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<Guid, string> BuildAppearanceLookup(StoryChatAppearanceEntry? entry) =>
        entry is null
            ? new Dictionary<Guid, string>()
            : StoryChatAppearanceDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(entry.AppearanceJson, StoryChatAppearanceDocument.Empty)).Characters
                .ToDictionary(x => x.CharacterId, x => NormalizeAppearanceText(x.CurrentAppearance));

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

    private static string NormalizeAppearanceText(string? value) => value?.Trim() ?? string.Empty;

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

    private static async Task<StoryChatAppearanceEntry> CreateEntryAsync(
        DbAppContext dbContext,
        Guid threadId,
        DbChatMessage coveredThroughMessage,
        IReadOnlyList<StorySceneCharacterAppearanceView> characters,
        string? suggestedSummary,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var entry = new StoryChatAppearanceEntry
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            SelectedLeafMessageId = coveredThroughMessage.Id,
            CoveredThroughMessageId = coveredThroughMessage.Id,
            CoveredThroughUtc = coveredThroughMessage.CreatedUtc,
            Summary = BuildEntrySummary(suggestedSummary, characters),
            AppearanceJson = ChatStoryJson.Serialize(StoryChatAppearanceDocumentNormalizer.Normalize(new StoryChatAppearanceDocument(
                characters
                    .Select(x => new StoryChatCharacterAppearanceDocument(x.CharacterId, x.CurrentAppearance))
                    .ToList()))),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.StoryChatAppearanceEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entry;
    }

    private static string ResolveSpeakerName(DbChatMessage message, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (message.MessageKind == ChatMessageKind.Narration)
            return "Narrator";

        if (message.SpeakerCharacterId.HasValue)
            return characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)?.Name ?? "Unknown Character";

        return "Unknown Character";
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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

    private static async Task<StoryChatAppearanceEntry?> GetLatestMatchingEntryAsync(
        DbAppContext dbContext,
        Guid threadId,
        IReadOnlyList<DbChatMessage> selectedPath,
        CancellationToken cancellationToken)
    {
        var selectedPathIds = selectedPath.Select(x => x.Id).ToHashSet();
        var entries = await dbContext.StoryChatAppearanceEntries
            .Where(x => x.ThreadId == threadId)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return entries.FirstOrDefault(x =>
            selectedPathIds.Contains(x.SelectedLeafMessageId)
            && selectedPathIds.Contains(x.CoveredThroughMessageId));
    }

    private sealed record AppearanceStageResponse(
        string? Summary,
        IReadOnlyList<AppearanceStageCharacterResponse>? Characters);

    private sealed record AppearanceStageCharacterResponse(
        string CharacterName,
        bool HasCurrentSceneState,
        string? CurrentAppearance);
}
