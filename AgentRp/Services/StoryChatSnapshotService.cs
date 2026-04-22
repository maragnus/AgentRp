using System.Text;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DbAppContext = AgentRp.Data.AppContext;
using DbChatMessage = AgentRp.Data.ChatMessage;

namespace AgentRp.Services;

public sealed class StoryChatSnapshotService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IThreadAgentService threadAgentService) : IStoryChatSnapshotService
{

    public async Task<StoryChatSnapshotSummaryView?> GetLatestSnapshotAsync(Guid threadId, Guid selectedLeafMessageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return null;

        var selectedPath = BuildSelectedPath(messages, selectedLeafMessageId);
        if (selectedPath.Count == 0)
            return null;

        var latestMatchingSnapshot = await GetLatestMatchingSnapshotAsync(dbContext, threadId, selectedPath, cancellationToken);
        return latestMatchingSnapshot is null ? null : MapSnapshot(latestMatchingSnapshot);
    }

    public async Task<IReadOnlyList<StorySceneSnapshotView>> GetSnapshotsForPathAsync(Guid threadId, Guid selectedLeafMessageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? new ChatStory
            {
                ChatThreadId = threadId,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var selectedPath = BuildSelectedPath(messages, selectedLeafMessageId);
        if (selectedPath.Count == 0)
            return [];

        var selectedPathIds = selectedPath.Select(x => x.Id).ToHashSet();
        var snapshots = await dbContext.StoryChatSnapshots
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return snapshots
            .Where(x => selectedPathIds.Contains(x.CoveredThroughMessageId))
            .Select(x => MapTranscriptSnapshot(x, story))
            .ToList();
    }

    public async Task<StoryChatSnapshotDraftView> CreateDraftAsync(CreateStoryChatSnapshotDraft request, CancellationToken cancellationToken)
    {
        var agent = await threadAgentService.GetSelectedAgentAsync(request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Creating the snapshot draft failed because no AI provider is configured for this chat.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Creating the snapshot draft failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == request.ThreadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        var selectedPath = BuildSelectedPath(messages, request.ThroughMessageId);
        if (selectedPath.Count == 0)
            throw new InvalidOperationException("Creating the snapshot draft failed because the selected message branch could not be found.");

        var latestSnapshot = await GetLatestMatchingSnapshotAsync(dbContext, request.ThreadId, selectedPath, cancellationToken);
        var candidateMessages = GetCandidateMessages(selectedPath, latestSnapshot);
        if (candidateMessages.Count == 0)
            throw new InvalidOperationException("Creating the snapshot draft failed because the selected message is already covered by a committed snapshot.");

        var response = await agent.ChatClient.GetResponseAsync<StoryChatSnapshotAiResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildSnapshotSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildSnapshotUserPrompt(thread, story, candidateMessages))
            ],
            options: new ChatOptions { Temperature = 0.3f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var draft = response.Result;

        return new StoryChatSnapshotDraftView(
            request.ThreadId,
            request.ThroughMessageId,
            latestSnapshot is null ? null : MapSnapshot(latestSnapshot),
            candidateMessages.Select(message => new StoryChatSnapshotMessageSelectionView(
                    message.Id,
                    ResolveSpeakerName(message, story.Characters.Entries),
                    message.CreatedUtc,
                    message.Content,
                    true))
                .ToList(),
            draft.NarrativeSummary?.Trim() ?? string.Empty,
            NormalizeFactDrafts(draft.Facts, story),
            NormalizeTimelineDrafts(draft.TimelineEntries, story),
            story.Characters.Entries
                .Where(x => !x.IsArchived)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new StoryChatSnapshotReferenceView(x.Id, x.Name))
                .ToList(),
            story.Locations.Entries
                .Where(x => !x.IsArchived)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new StoryChatSnapshotReferenceView(x.Id, x.Name))
                .ToList(),
            story.Items.Entries
                .Where(x => !x.IsArchived)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new StoryChatSnapshotReferenceView(x.Id, x.Name))
                .ToList());
    }

    public async Task<StoryChatSnapshotSummaryView> CommitDraftAsync(CommitStoryChatSnapshotDraft request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Committing the snapshot failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == request.ThreadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        var selectedPath = BuildSelectedPath(messages, request.ThroughMessageId);
        if (selectedPath.Count == 0)
            throw new InvalidOperationException("Committing the snapshot failed because the selected message branch could not be found.");

        var latestSnapshot = await GetLatestMatchingSnapshotAsync(dbContext, request.ThreadId, selectedPath, cancellationToken);
        var candidateMessages = GetCandidateMessages(selectedPath, latestSnapshot);
        if (candidateMessages.Count == 0)
            throw new InvalidOperationException("Committing the snapshot failed because the selected message is already covered by a committed snapshot.");

        var normalizedSummary = request.NarrativeSummary.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSummary))
            throw new InvalidOperationException("Committing the snapshot failed because the narrative summary was empty.");

        var normalizedFacts = NormalizeCommittedFactDocuments(request.Facts, story);
        var normalizedTimelineEntries = NormalizeCommittedTimelineDocuments(request.TimelineEntries, story);
        var snapshotDocument = new StoryChatSnapshotDocument(normalizedSummary, normalizedFacts, normalizedTimelineEntries);
        var coveredThroughMessage = candidateMessages[^1];
        var snapshot = new StoryChatSnapshot
        {
            Id = Guid.NewGuid(),
            ThreadId = request.ThreadId,
            SelectedLeafMessageId = request.ThroughMessageId,
            CoveredThroughMessageId = coveredThroughMessage.Id,
            CoveredThroughUtc = coveredThroughMessage.CreatedUtc,
            Summary = normalizedSummary,
            SnapshotJson = ChatStoryJson.Serialize(snapshotDocument),
            IncludedMessageIdsJson = ChatStoryJson.Serialize(candidateMessages.Select(x => x.Id).ToList()),
            CreatedUtc = DateTime.UtcNow
        };

        dbContext.StoryChatSnapshots.Add(snapshot);
        story.History = AppendSnapshotHistory(story.History, normalizedFacts, normalizedTimelineEntries);
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapSnapshot(snapshot);
    }

    public static IReadOnlyList<Guid> GetSnapshotCandidateMessageIds(
        IReadOnlyList<DbChatMessage> messages,
        StoryChatSnapshotSummaryView? latestSnapshot) =>
        GetCandidateMessages(messages, latestSnapshot).Select(x => x.Id).ToList();

    public static IReadOnlyList<Guid> GetSnapshotCandidateMessageIdsThrough(
        IReadOnlyList<DbChatMessage> selectedPath,
        StoryChatSnapshotSummaryView? latestSnapshot,
        Guid throughMessageId)
    {
        var pathThroughMessage = selectedPath
            .TakeWhile(x => x.Id != throughMessageId)
            .Concat(selectedPath.Where(x => x.Id == throughMessageId).Take(1))
            .ToList();
        return GetCandidateMessages(pathThroughMessage, latestSnapshot).Select(x => x.Id).ToList();
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

    private static StorySceneSnapshotView MapTranscriptSnapshot(StoryChatSnapshot snapshot, ChatStory story)
    {
        var document = ChatStoryJson.Deserialize(snapshot.SnapshotJson, StoryChatSnapshotDocument.Empty);
        var includedMessageIds = ChatStoryJson.Deserialize(snapshot.IncludedMessageIdsJson, new List<Guid>());

        return new StorySceneSnapshotView(
            snapshot.Id,
            snapshot.CoveredThroughMessageId,
            snapshot.CreatedUtc,
            snapshot.Summary,
            includedMessageIds.Count,
            document.Facts.Count,
            document.TimelineEntries.Count,
            document.Facts.Select(x => new StorySceneSnapshotFactView(
                    x.Title,
                    x.Summary,
                    x.Details,
                    ResolveReferenceNames(story.Characters.Entries, x.CharacterIds, y => y.Id, y => y.Name),
                    ResolveReferenceNames(story.Locations.Entries, x.LocationIds, y => y.Id, y => y.Name),
                    ResolveReferenceNames(story.Items.Entries, x.ItemIds, y => y.Id, y => y.Name)))
                .ToList(),
            document.TimelineEntries.Select(x => new StorySceneSnapshotTimelineEntryView(
                    x.WhenText,
                    x.Title,
                    x.Summary,
                    x.Details,
                    ResolveReferenceNames(story.Characters.Entries, x.CharacterIds, y => y.Id, y => y.Name),
                    ResolveReferenceNames(story.Locations.Entries, x.LocationIds, y => y.Id, y => y.Name),
                    ResolveReferenceNames(story.Items.Entries, x.ItemIds, y => y.Id, y => y.Name)))
                .ToList());
    }

    private static IReadOnlyList<StoryChatSnapshotFactDraftView> NormalizeFactDrafts(
        IReadOnlyList<StoryChatSnapshotAiFact>? facts,
        ChatStory story) =>
        facts?
            .Select(fact => new StoryChatSnapshotFactDraftView(
                fact.Title?.Trim() ?? string.Empty,
                fact.Summary?.Trim() ?? string.Empty,
                fact.Details?.Trim() ?? string.Empty,
                ResolveReferenceIds(story.Characters.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), fact.CharacterNames),
                ResolveReferenceIds(story.Locations.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), fact.LocationNames),
                ResolveReferenceIds(story.Items.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), fact.ItemNames)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryChatSnapshotTimelineDraftView> NormalizeTimelineDrafts(
        IReadOnlyList<StoryChatSnapshotAiTimelineEntry>? timelineEntries,
        ChatStory story) =>
        timelineEntries?
            .Select(entry => new StoryChatSnapshotTimelineDraftView(
                string.IsNullOrWhiteSpace(entry.WhenText) ? null : entry.WhenText.Trim(),
                entry.Title?.Trim() ?? string.Empty,
                entry.Summary?.Trim() ?? string.Empty,
                entry.Details?.Trim() ?? string.Empty,
                ResolveReferenceIds(story.Characters.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), entry.CharacterNames),
                ResolveReferenceIds(story.Locations.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), entry.LocationNames),
                ResolveReferenceIds(story.Items.Entries.Where(x => !x.IsArchived).Select(x => (x.Id, x.Name)).ToList(), entry.ItemNames)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryChatSnapshotFactDocument> NormalizeCommittedFactDocuments(
        IReadOnlyList<StoryChatSnapshotFactDraftView> facts,
        ChatStory story) =>
        facts
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .Select(x => new StoryChatSnapshotFactDocument(
                x.Title.Trim(),
                x.Summary.Trim(),
                x.Details.Trim(),
                NormalizeReferenceIds(x.CharacterIds, story.Characters.Entries.Select(y => y.Id)),
                NormalizeReferenceIds(x.LocationIds, story.Locations.Entries.Select(y => y.Id)),
                NormalizeReferenceIds(x.ItemIds, story.Items.Entries.Select(y => y.Id))))
            .ToList();

    private static IReadOnlyList<StoryChatSnapshotTimelineEntryDocument> NormalizeCommittedTimelineDocuments(
        IReadOnlyList<StoryChatSnapshotTimelineDraftView> timelineEntries,
        ChatStory story) =>
        timelineEntries
            .Where(x => !string.IsNullOrWhiteSpace(x.Title))
            .Select(x => new StoryChatSnapshotTimelineEntryDocument(
                string.IsNullOrWhiteSpace(x.WhenText) ? null : x.WhenText.Trim(),
                x.Title.Trim(),
                x.Summary.Trim(),
                x.Details.Trim(),
                NormalizeReferenceIds(x.CharacterIds, story.Characters.Entries.Select(y => y.Id)),
                NormalizeReferenceIds(x.LocationIds, story.Locations.Entries.Select(y => y.Id)),
                NormalizeReferenceIds(x.ItemIds, story.Items.Entries.Select(y => y.Id))))
            .ToList();

    private static ChatStoryHistoryDocument AppendSnapshotHistory(
        ChatStoryHistoryDocument history,
        IReadOnlyList<StoryChatSnapshotFactDocument> facts,
        IReadOnlyList<StoryChatSnapshotTimelineEntryDocument> timelineEntries)
    {
        var nextFactSortOrder = history.Facts.Count == 0 ? 1 : history.Facts.Max(x => x.SortOrder) + 1;
        var nextTimelineSortOrder = history.TimelineEntries.Count == 0 ? 1 : history.TimelineEntries.Max(x => x.SortOrder) + 1;

        var appendedFacts = facts
            .Select((fact, index) => new StoryHistoryFactDocument(
                Guid.NewGuid(),
                nextFactSortOrder + index,
                fact.Title,
                fact.Summary,
                fact.Details,
                fact.CharacterIds,
                fact.LocationIds,
                fact.ItemIds))
            .ToList();
        var appendedTimelineEntries = timelineEntries
            .Select((entry, index) => new StoryTimelineEntryDocument(
                Guid.NewGuid(),
                nextTimelineSortOrder + index,
                entry.WhenText,
                entry.Title,
                entry.Summary,
                entry.Details,
                entry.CharacterIds,
                entry.LocationIds,
                entry.ItemIds))
            .ToList();

        return new ChatStoryHistoryDocument(
            history.Facts.Concat(appendedFacts).ToList(),
            history.TimelineEntries.Concat(appendedTimelineEntries).ToList());
    }

    private static IReadOnlyList<Guid> ResolveReferenceIds(
        IReadOnlyList<(Guid Id, string Name)> references,
        IReadOnlyList<string>? requestedNames)
    {
        if (requestedNames is null || requestedNames.Count == 0)
            return [];

        var lookup = references.ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
        return requestedNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(lookup.ContainsKey)
            .Select(x => lookup[x])
            .ToList();
    }

    private static IReadOnlyList<Guid> NormalizeReferenceIds(IEnumerable<Guid> requestedIds, IEnumerable<Guid> validIds)
    {
        var validIdSet = validIds.ToHashSet();
        return requestedIds
            .Distinct()
            .Where(validIdSet.Contains)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveReferenceNames<T>(
        IEnumerable<T> entries,
        IEnumerable<Guid> ids,
        Func<T, Guid> getId,
        Func<T, string> getName)
    {
        var idSet = ids.ToHashSet();
        return entries
            .Where(x => idSet.Contains(getId(x)))
            .Select(getName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildSnapshotSystemPrompt() =>
        """
        You create structured story snapshots from a selected branch transcript.
        Summarize only what is supported by the included messages and supplied story state.
        Return a concise narrative summary, then propose canonical facts and timeline entries that should be saved.
        Prefer durable developments over throwaway phrasing.
        Do not invent names, references, or events that are not grounded in the provided material.
        """;

    private static string BuildSnapshotUserPrompt(
        ChatThread thread,
        ChatStory story,
        IReadOnlyList<DbChatMessage> includedMessages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Thread title: {thread.Title}");

        var currentLocationName = story.Scene.CurrentLocationId.HasValue
            ? story.Locations.Entries.FirstOrDefault(x => x.Id == story.Scene.CurrentLocationId.Value)?.Name
            : null;
        builder.AppendLine($"Current location: {currentLocationName ?? "None"}");
        builder.AppendLine($"Characters in story: {FormatReferenceNames(story.Characters.Entries.Where(x => !x.IsArchived).Select(x => x.Name))}");
        builder.AppendLine($"Locations in story: {FormatReferenceNames(story.Locations.Entries.Where(x => !x.IsArchived).Select(x => x.Name))}");
        builder.AppendLine($"Items in story: {FormatReferenceNames(story.Items.Entries.Where(x => !x.IsArchived).Select(x => x.Name))}");
        builder.AppendLine($"Existing canonical history: {BuildHistorySummary(story.History)}");
        builder.AppendLine();
        builder.AppendLine("Included branch messages:");

        foreach (var message in includedMessages)
        {
            builder.Append("- ");
            builder.Append(ResolveSpeakerName(message, story.Characters.Entries));
            builder.Append(" (");
            builder.Append(message.CreatedUtc.ToString("u"));
            builder.Append("): ");
            builder.AppendLine(CollapseWhitespace(message.Content));
        }

        builder.AppendLine();
        builder.AppendLine("Return:");
        builder.AppendLine("1. A narrative summary of what has happened so far in this included range.");
        builder.AppendLine("2. Proposed facts that should be canonized.");
        builder.AppendLine("3. Proposed timeline entries that should be added.");
        builder.AppendLine("For characterNames, locationNames, and itemNames, only use names from the provided catalogs.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildHistorySummary(ChatStoryHistoryDocument history)
    {
        var facts = history.Facts.Take(3).Select(x => $"{x.Title}: {x.Summary}");
        var timeline = history.TimelineEntries.Take(3).Select(x => $"{x.Title}: {x.Summary}");
        var combined = facts.Concat(timeline).ToList();
        return combined.Count == 0 ? "None" : string.Join(" | ", combined);
    }

    private static string FormatReferenceNames(IEnumerable<string> names)
    {
        var values = names
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count == 0 ? "None" : string.Join(", ", values);
    }

    private static List<DbChatMessage> BuildSelectedPath(IReadOnlyList<DbChatMessage> messages, Guid selectedLeafMessageId)
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

    private static List<DbChatMessage> GetCandidateMessages(
        IReadOnlyList<DbChatMessage> selectedPath,
        StoryChatSnapshot? latestSnapshot)
    {
        if (latestSnapshot is null)
            return selectedPath.ToList();

        return selectedPath
            .Where(x => x.CreatedUtc > latestSnapshot.CoveredThroughUtc)
            .ToList();
    }

    private static List<DbChatMessage> GetCandidateMessages(
        IReadOnlyList<DbChatMessage> selectedPath,
        StoryChatSnapshotSummaryView? latestSnapshot)
    {
        if (latestSnapshot is null)
            return selectedPath.ToList();

        return selectedPath
            .Where(x => x.CreatedUtc > latestSnapshot.CoveredThroughUtc)
            .ToList();
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string ResolveSpeakerName(DbChatMessage message, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (message.MessageKind == ChatMessageKind.Narration)
            return "Narrator";

        if (message.SpeakerCharacterId.HasValue)
            return characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)?.Name ?? "Unknown Character";

        return "Unknown Character";
    }

    private static async Task<StoryChatSnapshot?> GetLatestMatchingSnapshotAsync(
        DbAppContext dbContext,
        Guid threadId,
        IReadOnlyList<DbChatMessage> selectedPath,
        CancellationToken cancellationToken)
    {
        var selectedPathIds = selectedPath.Select(x => x.Id).ToHashSet();
        var snapshots = await dbContext.StoryChatSnapshots
            .Where(x => x.ThreadId == threadId)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return snapshots.FirstOrDefault(x =>
            selectedPathIds.Contains(x.SelectedLeafMessageId)
            && selectedPathIds.Contains(x.CoveredThroughMessageId));
    }

    private static async Task<ChatStory> GetOrCreateStoryAsync(DbAppContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        var story = await dbContext.ChatStories.FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken);
        if (story is not null)
            return story;

        story = new ChatStory
        {
            ChatThreadId = threadId,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.ChatStories.Add(story);
        await dbContext.SaveChangesAsync(cancellationToken);
        return story;
    }

    private sealed record StoryChatSnapshotAiResponse(
        string? NarrativeSummary,
        IReadOnlyList<StoryChatSnapshotAiFact>? Facts,
        IReadOnlyList<StoryChatSnapshotAiTimelineEntry>? TimelineEntries);

    private sealed record StoryChatSnapshotAiFact(
        string? Title,
        string? Summary,
        string? Details,
        IReadOnlyList<string>? CharacterNames,
        IReadOnlyList<string>? LocationNames,
        IReadOnlyList<string>? ItemNames);

    private sealed record StoryChatSnapshotAiTimelineEntry(
        string? WhenText,
        string? Title,
        string? Summary,
        string? Details,
        IReadOnlyList<string>? CharacterNames,
        IReadOnlyList<string>? LocationNames,
        IReadOnlyList<string>? ItemNames);
}
