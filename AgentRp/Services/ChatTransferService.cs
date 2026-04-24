using System.Text;
using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed class ChatTransferService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IAgentCatalog agentCatalog) : IChatTransferService
{
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ChatTransferSourceView GetSourceView(string title) => new(
        string.IsNullOrWhiteSpace(title) ? "Untitled Chat" : title.Trim(),
        ChatTransferSelection.All);

    public ChatTransferSelection NormalizeSelection(ChatTransferSelection selection, ChatTransferSelection availableSections)
    {
        var normalized = Intersect(selection, availableSections);

        if (!normalized.Messages)
            return normalized with
            {
                Snapshots = false,
                CurrentAppearanceBlocks = false
            };

        return normalized;
    }

    public ChatTransferSelection GetLockedSections(ChatTransferSelection selection, ChatTransferSelection availableSections)
    {
        var normalized = NormalizeSelection(selection, availableSections);

        return new ChatTransferSelection(
            false,
            availableSections.Snapshots && !normalized.Messages,
            availableSections.CurrentAppearanceBlocks && !normalized.Messages,
            false,
            false,
            false,
            false,
            false);
    }

    public async Task<ChatTransferPackage> BuildPackageAsync(Guid threadId, ChatTransferSelection selection, CancellationToken cancellationToken)
    {
        var normalizedSelection = NormalizeSelection(selection, ChatTransferSelection.All);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Building the chat transfer package failed because the selected chat could not be found.");
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? new ChatStory
            {
                ChatThreadId = threadId,
                CreatedUtc = thread.CreatedUtc,
                UpdatedUtc = thread.UpdatedUtc
            };

        IReadOnlyList<ChatMessage>? messages = null;
        IReadOnlyList<StoryChatSnapshot>? snapshots = null;
        IReadOnlyList<StoryChatAppearanceEntry>? currentAppearanceBlocks = null;

        if (normalizedSelection.Messages)
        {
            messages = await dbContext.ChatMessages
                .AsNoTracking()
                .Where(x => x.ThreadId == threadId)
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync(cancellationToken);
        }

        if (normalizedSelection.Snapshots)
        {
            snapshots = await dbContext.StoryChatSnapshots
                .AsNoTracking()
                .Where(x => x.ThreadId == threadId)
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync(cancellationToken);
        }

        if (normalizedSelection.CurrentAppearanceBlocks)
        {
            currentAppearanceBlocks = await dbContext.StoryChatAppearanceEntries
                .AsNoTracking()
                .Where(x => x.ThreadId == threadId)
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync(cancellationToken);
        }

        return new ChatTransferPackage(
            CurrentSchemaVersion,
            DateTime.UtcNow,
            new ChatTransferSourceInfo(thread.Title),
            new ChatTransferPayload(
                normalizedSelection.Messages ? thread.ActiveLeafMessageId : null,
                normalizedSelection.SceneState ? thread.SelectedSpeakerCharacterId : null,
                normalizedSelection.SceneState ? story.Scene : null,
                normalizedSelection.Characters ? story.Characters : null,
                normalizedSelection.Locations ? story.Locations : null,
                normalizedSelection.Items ? story.Items : null,
                normalizedSelection.StoryContext ? story.StoryContext : null,
                normalizedSelection.StoryContext ? story.History : null,
                normalizedSelection.Messages
                    ? messages?.Select(message => new ChatTransferMessageRecord(
                            message.Id,
                            message.Role,
                            message.MessageKind,
                            message.Content,
                            message.CreatedUtc,
                            message.SpeakerCharacterId,
                            message.GenerationMode,
                            message.ParentMessageId,
                            message.EditedFromMessageId))
                        .ToList()
                    : null,
                normalizedSelection.Snapshots
                    ? snapshots?.Select(snapshot => new ChatTransferSnapshotRecord(
                            snapshot.Id,
                            snapshot.SelectedLeafMessageId,
                            snapshot.CoveredThroughMessageId,
                            snapshot.CoveredThroughUtc,
                            snapshot.Summary,
                            ChatStoryJson.Deserialize(snapshot.SnapshotJson, StoryChatSnapshotDocument.Empty),
                            ChatStoryJson.Deserialize(snapshot.IncludedMessageIdsJson, new List<Guid>()),
                            snapshot.CreatedUtc))
                        .ToList()
                    : null,
                normalizedSelection.CurrentAppearanceBlocks
                    ? currentAppearanceBlocks?.Select(entry => new ChatTransferAppearanceRecord(
                            entry.Id,
                            entry.SelectedLeafMessageId,
                            entry.CoveredThroughMessageId,
                            entry.CoveredThroughUtc,
                            entry.Summary,
                            StoryChatAppearanceDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(entry.AppearanceJson, StoryChatAppearanceDocument.Empty)),
                            entry.CreatedUtc,
                            entry.UpdatedUtc))
                        .ToList()
                    : null));
    }

    public string SerializePackage(ChatTransferPackage package) => JsonSerializer.Serialize(package, SerializerOptions);

    public string BuildExportFileName(ChatTransferPackage package)
    {
        var slug = BuildSlug(package.Source.Title);
        var dateStamp = package.ExportedUtc.ToUniversalTime().ToString("yyyy-MM-dd");
        return $"{slug}-{dateStamp}.agentrp-chat.json";
    }

    public ChatTransferInspectionView InspectPackage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Reading the chat export failed because the selected file was empty.");

        ChatTransferPackage package;

        try
        {
            package = JsonSerializer.Deserialize<ChatTransferPackage>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Reading the chat export failed because the file did not contain a chat package.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Reading the chat export failed because the file was not valid JSON.", exception);
        }

        if (package.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Reading the chat export failed because schema version {package.SchemaVersion} is not supported.");

        var availableSections = GetAvailableSections(package.Payload);
        return new ChatTransferInspectionView(package, new ChatTransferSourceView(package.Source.Title, availableSections));
    }

    public async Task<ChatTransferApplyResult> ApplyPackageAsync(
        ChatTransferPackage package,
        ChatTransferSelection selection,
        ChatTransferApplyMode mode,
        CancellationToken cancellationToken)
    {
        if (package.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Applying the chat transfer failed because schema version {package.SchemaVersion} is not supported.");

        var availableSections = GetAvailableSections(package.Payload);
        var normalizedSelection = NormalizeSelection(selection, availableSections);
        var now = DateTime.UtcNow;
        ChatStoryCharactersDocument characterDocuments;
        Dictionary<Guid, Guid> characterMap;

        if (normalizedSelection.Characters && package.Payload.Characters is not null)
        {
            var clonedCharacters = CloneCharacters(package.Payload.Characters);
            characterDocuments = clonedCharacters.Document;
            characterMap = clonedCharacters.Map;
        }
        else
        {
            characterDocuments = new ChatStoryCharactersDocument([]);
            characterMap = [];
        }

        ChatStoryLocationsDocument locationDocuments;
        Dictionary<Guid, Guid> locationMap;

        if (normalizedSelection.Locations && package.Payload.Locations is not null)
        {
            var clonedLocations = CloneLocations(package.Payload.Locations);
            locationDocuments = clonedLocations.Document;
            locationMap = clonedLocations.Map;
        }
        else
        {
            locationDocuments = new ChatStoryLocationsDocument([]);
            locationMap = [];
        }

        ChatStoryItemsDocument itemDocuments;
        Dictionary<Guid, Guid> itemMap;

        if (normalizedSelection.Items && package.Payload.Items is not null)
        {
            var clonedItems = CloneItems(package.Payload.Items, characterMap, locationMap);
            itemDocuments = clonedItems.Document;
            itemMap = clonedItems.Map;
        }
        else
        {
            itemDocuments = new ChatStoryItemsDocument([]);
            itemMap = [];
        }

        var storyContext = normalizedSelection.StoryContext && package.Payload.StoryContext is not null
            ? CloneStoryContext(package.Payload.StoryContext)
            : ChatStoryContextDocument.Empty;
        var history = normalizedSelection.StoryContext && package.Payload.History is not null
            ? CloneHistory(package.Payload.History, characterMap, locationMap, itemMap)
            : ChatStoryHistoryDocument.Empty;
        var scene = normalizedSelection.SceneState && package.Payload.SceneState is not null
            ? CloneScene(package.Payload.SceneState, characterMap, locationMap, itemMap)
            : ChatStorySceneDocument.Empty;

        List<ChatMessage> clonedMessages;
        Dictionary<Guid, Guid> messageMap;

        if (normalizedSelection.Messages && package.Payload.Messages is not null)
        {
            var messageClone = CloneMessages(package.Payload.Messages, characterMap);
            clonedMessages = messageClone.Messages;
            messageMap = messageClone.Map;
        }
        else
        {
            clonedMessages = [];
            messageMap = [];
        }

        var snapshotClone = normalizedSelection.Snapshots && package.Payload.Snapshots is not null
            ? CloneSnapshots(package.Payload.Snapshots, messageMap, characterMap, locationMap, itemMap)
            : [];
        var appearanceClone = normalizedSelection.CurrentAppearanceBlocks && package.Payload.CurrentAppearanceBlocks is not null
            ? CloneCurrentAppearanceBlocks(package.Payload.CurrentAppearanceBlocks, messageMap, characterMap)
            : [];
        var title = mode == ChatTransferApplyMode.Duplicate
            ? BuildCopyTitle(package.Source.Title)
            : BuildImportedTitle(package.Source.Title);

        var thread = new ChatThread
        {
            Id = Guid.NewGuid(),
            Title = title,
            SelectedAgentName = agentCatalog.GetDefaultAgentName() ?? string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now,
            ActiveLeafMessageId = normalizedSelection.Messages
                ? RemapOptionalId(package.Payload.ActiveLeafMessageId, messageMap) ?? ResolveLatestLeafMessageId(clonedMessages)
                : null,
            SelectedSpeakerCharacterId = normalizedSelection.SceneState
                ? RemapOptionalId(package.Payload.SelectedSpeakerCharacterId, characterMap)
                : null
        };
        var story = new ChatStory
        {
            ChatThreadId = thread.Id,
            Thread = thread,
            CreatedUtc = now,
            UpdatedUtc = now,
            Characters = characterDocuments,
            Locations = locationDocuments,
            Items = itemDocuments,
            StoryContext = storyContext,
            History = history,
            Scene = SanitizeScene(new ChatStory
            {
                Characters = characterDocuments,
                Locations = locationDocuments,
                Items = itemDocuments,
                StoryContext = storyContext,
                History = history,
                Scene = scene
            })
        };

        foreach (var message in clonedMessages)
        {
            message.ThreadId = thread.Id;
            message.Thread = thread;
        }

        foreach (var snapshot in snapshotClone)
        {
            snapshot.ThreadId = thread.Id;
            snapshot.Thread = thread;
        }

        foreach (var appearanceEntry in appearanceClone)
        {
            appearanceEntry.ThreadId = thread.Id;
            appearanceEntry.Thread = thread;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.ChatThreads.Add(thread);
        dbContext.ChatStories.Add(story);

        if (clonedMessages.Count > 0)
            dbContext.ChatMessages.AddRange(clonedMessages);

        if (snapshotClone.Count > 0)
            dbContext.StoryChatSnapshots.AddRange(snapshotClone);

        if (appearanceClone.Count > 0)
            dbContext.StoryChatAppearanceEntries.AddRange(appearanceClone);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh(thread.Id);
        return new ChatTransferApplyResult(thread.Id, thread.Title);
    }

    private void PublishRefresh(Guid threadId)
    {
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, DateTime.UtcNow));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, DateTime.UtcNow));
    }

    private static ChatTransferSelection Intersect(ChatTransferSelection selection, ChatTransferSelection availableSections) => new(
        availableSections.Messages && selection.Messages,
        availableSections.Snapshots && selection.Snapshots,
        availableSections.CurrentAppearanceBlocks && selection.CurrentAppearanceBlocks,
        availableSections.Characters && selection.Characters,
        availableSections.Locations && selection.Locations,
        availableSections.Items && selection.Items,
        availableSections.StoryContext && selection.StoryContext,
        availableSections.SceneState && selection.SceneState);

    private static ChatTransferSelection GetAvailableSections(ChatTransferPayload payload) => new(
        payload.Messages is not null,
        payload.Snapshots is not null,
        payload.CurrentAppearanceBlocks is not null,
        payload.Characters is not null,
        payload.Locations is not null,
        payload.Items is not null,
        payload.StoryContext is not null && payload.History is not null,
        payload.SceneState is not null);

    private static (ChatStoryCharactersDocument Document, Dictionary<Guid, Guid> Map) CloneCharacters(ChatStoryCharactersDocument document)
    {
        var map = document.Entries.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var clonedEntries = document.Entries
            .Select(entry => entry with { Id = map[entry.Id] })
            .ToList();

        return (new ChatStoryCharactersDocument(clonedEntries), map);
    }

    private static (ChatStoryLocationsDocument Document, Dictionary<Guid, Guid> Map) CloneLocations(ChatStoryLocationsDocument document)
    {
        var map = document.Entries.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var clonedEntries = document.Entries
            .Select(entry => entry with { Id = map[entry.Id] })
            .ToList();

        return (new ChatStoryLocationsDocument(clonedEntries), map);
    }

    private static (ChatStoryItemsDocument Document, Dictionary<Guid, Guid> Map) CloneItems(
        ChatStoryItemsDocument document,
        IReadOnlyDictionary<Guid, Guid> characterMap,
        IReadOnlyDictionary<Guid, Guid> locationMap)
    {
        var map = document.Entries.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var clonedEntries = document.Entries
            .Select(entry => entry with
            {
                Id = map[entry.Id],
                OwnerCharacterId = RemapOptionalId(entry.OwnerCharacterId, characterMap),
                LocationId = RemapOptionalId(entry.LocationId, locationMap)
            })
            .ToList();

        return (new ChatStoryItemsDocument(clonedEntries), map);
    }

    private static ChatStoryHistoryDocument CloneHistory(
        ChatStoryHistoryDocument document,
        IReadOnlyDictionary<Guid, Guid> characterMap,
        IReadOnlyDictionary<Guid, Guid> locationMap,
        IReadOnlyDictionary<Guid, Guid> itemMap) => new(
        document.Facts
            .OrderBy(x => x.SortOrder)
            .Select((fact, index) => fact with
            {
                Id = Guid.NewGuid(),
                SortOrder = index + 1,
                CharacterIds = RemapIds(fact.CharacterIds, characterMap),
                LocationIds = RemapIds(fact.LocationIds, locationMap),
                ItemIds = RemapIds(fact.ItemIds, itemMap)
            })
            .ToList(),
        document.TimelineEntries
            .OrderBy(x => x.SortOrder)
            .Select((entry, index) => entry with
            {
                Id = Guid.NewGuid(),
                SortOrder = index + 1,
                CharacterIds = RemapIds(entry.CharacterIds, characterMap),
                LocationIds = RemapIds(entry.LocationIds, locationMap),
                ItemIds = RemapIds(entry.ItemIds, itemMap)
            })
            .ToList());

    private static ChatStoryContextDocument CloneStoryContext(ChatStoryContextDocument document) => new(
        document.Genre,
        document.Setting,
        document.Tone,
        document.StoryDirection,
        document.ExplicitContent,
        document.ViolentContent);

    private static ChatStorySceneDocument CloneScene(
        ChatStorySceneDocument document,
        IReadOnlyDictionary<Guid, Guid> characterMap,
        IReadOnlyDictionary<Guid, Guid> locationMap,
        IReadOnlyDictionary<Guid, Guid> itemMap) => new(
        RemapOptionalId(document.CurrentLocationId, locationMap),
        RemapIds(document.PresentCharacterIds, characterMap),
        RemapIds(document.PresentItemIds, itemMap),
        document.DerivedContextSummary,
        document.ManualContextNotes);

    private static (List<ChatMessage> Messages, Dictionary<Guid, Guid> Map) CloneMessages(
        IReadOnlyList<ChatTransferMessageRecord> records,
        IReadOnlyDictionary<Guid, Guid> characterMap)
    {
        var orderedRecords = records
            .OrderBy(x => x.CreatedUtc)
            .ToList();
        var map = orderedRecords.ToDictionary(x => x.Id, _ => Guid.NewGuid());
        var messages = orderedRecords
            .Select(record => new ChatMessage
            {
                Id = map[record.Id],
                ThreadId = Guid.Empty,
                Thread = null!,
                Role = record.Role,
                MessageKind = record.MessageKind,
                Content = record.Content,
                CreatedUtc = record.CreatedUtc,
                SpeakerCharacterId = RemapOptionalId(record.SpeakerCharacterId, characterMap),
                GenerationMode = record.GenerationMode,
                SourceProcessRunId = null,
                ParentMessageId = RemapOptionalId(record.ParentMessageId, map),
                EditedFromMessageId = RemapOptionalId(record.EditedFromMessageId, map)
            })
            .ToList();

        return (messages, map);
    }

    private static IReadOnlyList<StoryChatSnapshot> CloneSnapshots(
        IReadOnlyList<ChatTransferSnapshotRecord> records,
        IReadOnlyDictionary<Guid, Guid> messageMap,
        IReadOnlyDictionary<Guid, Guid> characterMap,
        IReadOnlyDictionary<Guid, Guid> locationMap,
        IReadOnlyDictionary<Guid, Guid> itemMap) =>
        records
            .OrderBy(x => x.CreatedUtc)
            .Select(record => TryCloneSnapshot(record, messageMap, characterMap, locationMap, itemMap))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

    private static StoryChatSnapshot? TryCloneSnapshot(
        ChatTransferSnapshotRecord record,
        IReadOnlyDictionary<Guid, Guid> messageMap,
        IReadOnlyDictionary<Guid, Guid> characterMap,
        IReadOnlyDictionary<Guid, Guid> locationMap,
        IReadOnlyDictionary<Guid, Guid> itemMap)
    {
        if (!messageMap.TryGetValue(record.SelectedLeafMessageId, out var selectedLeafMessageId))
            return null;

        if (!messageMap.TryGetValue(record.CoveredThroughMessageId, out var coveredThroughMessageId))
            return null;

        var snapshotDocument = new StoryChatSnapshotDocument(
            record.Snapshot.NarrativeSummary,
            record.Snapshot.Facts
                .Select(fact => fact with
                {
                    CharacterIds = RemapIds(fact.CharacterIds, characterMap),
                    LocationIds = RemapIds(fact.LocationIds, locationMap),
                    ItemIds = RemapIds(fact.ItemIds, itemMap)
                })
                .ToList(),
            record.Snapshot.TimelineEntries
                .Select(entry => entry with
                {
                    CharacterIds = RemapIds(entry.CharacterIds, characterMap),
                    LocationIds = RemapIds(entry.LocationIds, locationMap),
                    ItemIds = RemapIds(entry.ItemIds, itemMap)
                })
                .ToList());

        return new StoryChatSnapshot
        {
            Id = Guid.NewGuid(),
            ThreadId = Guid.Empty,
            Thread = null!,
            SelectedLeafMessageId = selectedLeafMessageId,
            CoveredThroughMessageId = coveredThroughMessageId,
            CoveredThroughUtc = record.CoveredThroughUtc,
            Summary = record.Summary,
            SnapshotJson = ChatStoryJson.Serialize(snapshotDocument),
            IncludedMessageIdsJson = ChatStoryJson.Serialize(RemapIds(record.IncludedMessageIds, messageMap)),
            CreatedUtc = record.CreatedUtc
        };
    }

    private static IReadOnlyList<StoryChatAppearanceEntry> CloneCurrentAppearanceBlocks(
        IReadOnlyList<ChatTransferAppearanceRecord> records,
        IReadOnlyDictionary<Guid, Guid> messageMap,
        IReadOnlyDictionary<Guid, Guid> characterMap) =>
        records
            .OrderBy(x => x.CreatedUtc)
            .Select(record => TryCloneCurrentAppearanceBlock(record, messageMap, characterMap))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

    private static StoryChatAppearanceEntry? TryCloneCurrentAppearanceBlock(
        ChatTransferAppearanceRecord record,
        IReadOnlyDictionary<Guid, Guid> messageMap,
        IReadOnlyDictionary<Guid, Guid> characterMap)
    {
        if (!messageMap.TryGetValue(record.SelectedLeafMessageId, out var selectedLeafMessageId))
            return null;

        if (!messageMap.TryGetValue(record.CoveredThroughMessageId, out var coveredThroughMessageId))
            return null;

        var appearanceDocument = new StoryChatAppearanceDocument(
            record.Appearance.Characters
                .Select(character => TryCloneAppearanceCharacter(character, characterMap))
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList());

        return new StoryChatAppearanceEntry
        {
            Id = Guid.NewGuid(),
            ThreadId = Guid.Empty,
            Thread = null!,
            SelectedLeafMessageId = selectedLeafMessageId,
            CoveredThroughMessageId = coveredThroughMessageId,
            CoveredThroughUtc = record.CoveredThroughUtc,
            Summary = record.Summary,
            AppearanceJson = ChatStoryJson.Serialize(StoryChatAppearanceDocumentNormalizer.Normalize(appearanceDocument)),
            CreatedUtc = record.CreatedUtc,
            UpdatedUtc = record.UpdatedUtc
        };
    }

    private static StoryChatCharacterAppearanceDocument? TryCloneAppearanceCharacter(
        StoryChatCharacterAppearanceDocument character,
        IReadOnlyDictionary<Guid, Guid> characterMap) =>
        characterMap.TryGetValue(character.CharacterId, out var mappedCharacterId)
            ? new StoryChatCharacterAppearanceDocument(mappedCharacterId, character.CurrentAppearance)
            : null;

    private static IReadOnlyList<Guid> RemapIds(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, Guid> map) =>
        ids
            .Where(map.ContainsKey)
            .Select(id => map[id])
            .Distinct()
            .ToList();

    private static Guid? RemapOptionalId(Guid? id, IReadOnlyDictionary<Guid, Guid> map) =>
        id.HasValue && map.TryGetValue(id.Value, out var mappedId)
            ? mappedId
            : null;

    private static Guid? ResolveLatestLeafMessageId(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return null;

        var parentIds = messages
            .Where(x => x.ParentMessageId.HasValue)
            .Select(x => x.ParentMessageId!.Value)
            .ToHashSet();

        return messages
            .Where(x => !parentIds.Contains(x.Id))
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefault();
    }

    private static ChatStorySceneDocument SanitizeScene(ChatStory story)
    {
        var validLocationIds = story.Locations.Entries
            .Where(x => !x.IsArchived)
            .Select(x => x.Id)
            .ToHashSet();
        var validCharacterIds = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .Select(x => x.Id)
            .ToHashSet();
        var validItemIds = story.Items.Entries
            .Where(x => !x.IsArchived)
            .Select(x => x.Id)
            .ToHashSet();

        return story.Scene with
        {
            CurrentLocationId = story.Scene.CurrentLocationId.HasValue && validLocationIds.Contains(story.Scene.CurrentLocationId.Value)
                ? story.Scene.CurrentLocationId
                : null,
            PresentCharacterIds = story.Scene.PresentCharacterIds.Where(validCharacterIds.Contains).Distinct().ToList(),
            PresentItemIds = story.Scene.PresentItemIds.Where(validItemIds.Contains).Distinct().ToList()
        };
    }

    private static string BuildImportedTitle(string? title)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Imported Chat" : title.Trim();
        return normalizedTitle.Length <= 200 ? normalizedTitle : normalizedTitle[..200].TrimEnd();
    }

    private static string BuildCopyTitle(string? title)
    {
        var normalizedTitle = BuildImportedTitle(title);
        var copyTitle = $"{normalizedTitle} (Copy)";
        return copyTitle.Length <= 200 ? copyTitle : copyTitle[..200].TrimEnd();
    }

    private static string BuildSlug(string? title)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "chat" : title.Trim().ToLowerInvariant();
        var builder = new StringBuilder();
        var lastWasSeparator = false;

        foreach (var character in normalizedTitle)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
                continue;
            }

            if (lastWasSeparator)
                continue;

            builder.Append('-');
            lastWasSeparator = true;
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "chat" : slug;
    }
}
