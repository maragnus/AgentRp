using System.Text;
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
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildAppearanceSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    BuildAppearanceUserPrompt(story, latestEffectiveCharacters, transcriptSinceLatestEntry))
            ],
            options: new ChatOptions { Temperature = 0.2f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);

        var resolvedCharacters = ResolveCharactersFromResponse(story, latestEffectiveCharacters, response.Result.Characters);
        if (AreEquivalent(latestEffectiveCharacters, resolvedCharacters))
        {
            return new StorySceneAppearanceResolution(
                MapEntry(latestEntry, story, latestEntry is not null, latestEffectiveCharacters),
                latestEffectiveCharacters,
                transcriptSinceLatestEntry);
        }

        var now = DateTime.UtcNow;
        var coveredThroughMessage = selectedPath[^1];
        var entry = new StoryChatAppearanceEntry
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            SelectedLeafMessageId = coveredThroughMessage.Id,
            CoveredThroughMessageId = coveredThroughMessage.Id,
            CoveredThroughUtc = coveredThroughMessage.CreatedUtc,
            Summary = BuildEntrySummary(response.Result.Summary, resolvedCharacters),
            AppearanceJson = ChatStoryJson.Serialize(StoryChatAppearanceDocumentNormalizer.Normalize(new StoryChatAppearanceDocument(
                resolvedCharacters
                    .Select(x => new StoryChatCharacterAppearanceDocument(x.CharacterId, x.CurrentAppearance))
                    .ToList()))),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.StoryChatAppearanceEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new StorySceneAppearanceResolution(
            MapEntry(entry, story, true, resolvedCharacters),
            resolvedCharacters,
            []);
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
        var presentCharacters = story.Characters.Entries
            .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var storedLookup = latestEntry is null
            ? new Dictionary<Guid, string>()
            : StoryChatAppearanceDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(latestEntry.AppearanceJson, StoryChatAppearanceDocument.Empty)).Characters
                .ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);

        var characters = new List<StorySceneCharacterAppearanceView>();
        foreach (var character in presentCharacters)
        {
            if (!storedLookup.TryGetValue(character.Id, out var currentAppearance) || string.IsNullOrWhiteSpace(currentAppearance))
                continue;

            characters.Add(new StorySceneCharacterAppearanceView(character.Id, character.Name, currentAppearance));
        }

        return characters;
    }

    private static IReadOnlyList<StorySceneCharacterAppearanceView> BuildUpdatedCharacters(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> requestCharacters)
    {
        var presentCharacters = story.Characters.Entries
            .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requestedLookup = requestCharacters.ToDictionary(x => x.CharacterId, x => NormalizeAppearanceText(x.CurrentAppearance));

        var characters = new List<StorySceneCharacterAppearanceView>();
        foreach (var character in presentCharacters)
        {
            if (!requestedLookup.TryGetValue(character.Id, out var currentAppearance) || string.IsNullOrWhiteSpace(currentAppearance))
                continue;

            characters.Add(new StorySceneCharacterAppearanceView(character.Id, character.Name, currentAppearance));
        }

        return characters;
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

        var resolvedCharacters = fallbackCharacters.ToDictionary(x => x.CharacterId);
        foreach (var mappedCharacter in mappedCharacters.Values)
        {
            if (string.IsNullOrWhiteSpace(mappedCharacter.CurrentAppearance))
                resolvedCharacters.Remove(mappedCharacter.CharacterId);
            else
                resolvedCharacters[mappedCharacter.CharacterId] = mappedCharacter;
        }

        return story.Characters.Entries
            .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(x => resolvedCharacters.ContainsKey(x.Id))
            .Select(x => resolvedCharacters[x.Id])
            .ToList();
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

        var currentAppearance = response.HasCurrentPhysicalDetails ? response.CurrentAppearance?.Trim() ?? string.Empty : string.Empty;
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

        var document = StoryChatAppearanceDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(entry.AppearanceJson, StoryChatAppearanceDocument.Empty));
        var characters = effectiveCharactersOverride
            ?? document.Characters
                .Where(x => !string.IsNullOrWhiteSpace(x.CurrentAppearance))
                .Select(x => new StorySceneCharacterAppearanceView(
                    x.CharacterId,
                    story.Characters.Entries.FirstOrDefault(character => character.Id == x.CharacterId)?.Name ?? "Unknown Character",
                    x.CurrentAppearance))
                .OrderBy(x => x.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList();

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

    private static string NormalizeAppearanceText(string? value) => value?.Trim() ?? string.Empty;

    private static string BuildAppearanceSystemPrompt() =>
        """
        You resolve current in-scene character appearance and visible physical state from story transcript.
        Return structured output only.
        For each character currently in the scene, decide whether there are supported current physical details.
        Current physical details include clothing, hairstyle, makeup, relative location, body language, and visible physical state such as exhausted, overheated, sweaty, drenched, cold, wounded, or trembling.
        Use only the transcript and prior current appearance as evidence.
        Do not copy, paraphrase, or summarize a character's general appearance as current appearance.
        Preserve prior current appearance and physical state when recent transcript does not contradict them.
        Set hasCurrentPhysicalDetails to false and leave currentAppearance empty when a character has no supported current physical details or state.
        The summary should mention only characters with hasCurrentPhysicalDetails true.
        Use intuition and context to infer changes but DO NOT invent changes that are not grounded in the transcript or provided prior state.
        """;

    private static string BuildAppearanceUserPrompt(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> latestCharacters,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceLatestEntry)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Characters currently in the scene:");

        foreach (var character in story.Characters.Entries
                     .Where(x => !x.IsArchived && story.Scene.PresentCharacterIds.Contains(x.Id))
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var latestCharacter = latestCharacters.FirstOrDefault(x => x.CharacterId == character.Id);
            builder.AppendLine($"- {character.Name}");
            builder.AppendLine($"  Prior current appearance/state: {latestCharacter?.CurrentAppearance ?? "None"}");
        }

        builder.AppendLine();
        builder.AppendLine("Transcript since the last appearance entry:");
        if (transcriptSinceLatestEntry.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in transcriptSinceLatestEntry)
                builder.AppendLine($"- {message.SpeakerName}: {message.Content}");
        }

        builder.AppendLine();
        builder.AppendLine("Return one decision for every character currently in the scene.");
        builder.AppendLine("Only set hasCurrentPhysicalDetails to true when currentAppearance contains specific current appearance or visible physical state supported by the transcript or prior current appearance/state.");
        return builder.ToString().TrimEnd();
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
        bool HasCurrentPhysicalDetails,
        string? CurrentAppearance);
}
