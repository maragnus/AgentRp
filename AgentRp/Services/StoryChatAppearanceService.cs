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
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, StoryChatAppearancePromptBuilder.BuildSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    StoryChatAppearancePromptBuilder.BuildUserPrompt(
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

    private static IReadOnlyList<StoryChatAppearancePromptCharacter> BuildPromptCharacters(
        ChatStory story,
        IReadOnlyList<StorySceneCharacterAppearanceView> latestCharacters)
    {
        var latestCharactersById = latestCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);

        return GetPresentCharacters(story)
            .Select(character => new StoryChatAppearancePromptCharacter(
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

internal sealed record StoryChatAppearancePromptCharacter(
    string Name,
    string CurrentAppearance);

internal static class StoryChatAppearancePromptBuilder
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

        The summary must mention only characters with hasCurrentSceneState true.
        The summary must describe each character as they appear now.
        Respect the supplied explicit-content and violent-content guidance.
        """;

    internal static string BuildUserPrompt(
        IReadOnlyList<StoryChatAppearancePromptCharacter> characters,
        IReadOnlyList<StorySceneTranscriptMessage> transcriptSinceLatestEntry,
        StoryContentIntensity explicitContent,
        StoryContentIntensity violentContent)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Content guidance:");
        builder.AppendLine($"- Explicit content: {FormatContentIntensity(explicitContent)}");
        builder.AppendLine($"- Violent content: {FormatContentIntensity(violentContent)}");
        builder.AppendLine();
        builder.AppendLine("Characters in the scene with initial appearance:");

        foreach (var character in characters)
        {
            builder.Append($"- **{character.Name}:** ");
            builder.AppendLine($"{PromptInlineText(character.CurrentAppearance, "None")}");
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

    private static string FormatContentIntensity(StoryContentIntensity intensity) => intensity switch
    {
        StoryContentIntensity.Forbidden => "Forbidden",
        StoryContentIntensity.Encouraged => "Encouraged",
        _ => "Allowed"
    };

    private static string PromptInlineText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
