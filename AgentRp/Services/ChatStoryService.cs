using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed class ChatStoryService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IAgentCatalog agentCatalog,
    IAgentEndpointManagementService agentEndpointManagementService,
    ILogger<ChatStoryService> logger) : IChatStoryService
{
    public async Task<ChatStorySidebarView?> GetSidebarAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken);
        if (thread is null)
            return null;

        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken);
        if (story is null)
            return null;

        IReadOnlyList<AgentEndpointStatusView> managedAgentEndpoints = [];
        string? managedAgentEndpointsErrorMessage = null;
        try
        {
            managedAgentEndpoints = await agentEndpointManagementService.GetStatusesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Loading Hugging Face endpoint status failed for chat {ThreadId}.", threadId);
            managedAgentEndpointsErrorMessage = "Hugging Face endpoint status is temporarily unavailable.";
        }

        return MapSidebarView(
            story,
            thread.SelectedSpeakerCharacterId,
            agentCatalog.NormalizeSelectedAgentName(thread.SelectedAgentName),
            agentCatalog.GetEnabledAgents(),
            managedAgentEndpoints,
            managedAgentEndpointsErrorMessage);
    }

    public async Task<IReadOnlyList<StoryCharacterEditorView>> GetCharactersAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        return MapCharacters(story);
    }

    public async Task<IReadOnlyList<StoryLocationListItemView>> GetLocationsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        return MapLocationList(story);
    }

    public async Task<IReadOnlyList<StoryLocationEditorView>> GetLocationEditorsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        return MapLocationEditors(story);
    }

    public async Task<IReadOnlyList<StoryItemEditorView>> GetItemsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        return MapItems(story);
    }

    public async Task<StoryHistoryView?> GetHistoryAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken);
        if (story is null)
            return null;

        return MapHistory(story);
    }

    public async Task<StoryCharacterEditorView> UpsertCharacterAsync(UpsertCharacter command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var name = RequireValue(command.Name, "character name");
        var summary = NormalizeOptionalValue(command.Summary);
        var generalAppearance = NormalizeOptionalValue(command.GeneralAppearance);
        var corePersonality = NormalizeOptionalValue(command.CorePersonality);
        var relationships = NormalizeOptionalValue(command.Relationships);
        var preferencesBeliefs = NormalizeOptionalValue(command.PreferencesBeliefs);
        var privateMotivations = NormalizeOptionalValue(command.PrivateMotivations);
        var documents = story.Characters.Entries.ToList();
        var characterId = command.CharacterId ?? Guid.NewGuid();
        var document = new StoryCharacterDocument(
            characterId,
            name,
            summary,
            generalAppearance,
            corePersonality,
            relationships,
            preferencesBeliefs,
            privateMotivations,
            command.IsArchived);

        ReplaceOrAdd(documents, document, x => x.Id);
        story.Characters = new ChatStoryCharactersDocument(documents);
        story.Scene = command.IsPresentInScene
            ? story.Scene with { PresentCharacterIds = AddId(story.Scene.PresentCharacterIds, characterId) }
            : story.Scene with { PresentCharacterIds = RemoveId(story.Scene.PresentCharacterIds, characterId) };

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapCharacter(story, document);
    }

    public async Task DeleteCharacterAsync(DeleteCharacter command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.Characters = new ChatStoryCharactersDocument(
            story.Characters.Entries
                .Where(x => x.Id != command.CharacterId)
                .ToList());
        story.Items = new ChatStoryItemsDocument(
            story.Items.Entries
                .Select(x => x.OwnerCharacterId == command.CharacterId ? x with { OwnerCharacterId = null } : x)
                .ToList());
        story.History = new ChatStoryHistoryDocument(
            story.History.Facts
                .Select(x => x with { CharacterIds = RemoveId(x.CharacterIds, command.CharacterId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList(),
            story.History.TimelineEntries
                .Select(x => x with { CharacterIds = RemoveId(x.CharacterIds, command.CharacterId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList());
        story.Scene = story.Scene with
        {
            PresentCharacterIds = RemoveId(story.Scene.PresentCharacterIds, command.CharacterId)
        };

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task<StoryLocationEditorView> UpsertLocationAsync(UpsertLocation command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var name = RequireValue(command.Name, "location name");
        var summary = NormalizeOptionalValue(command.Summary);
        var details = NormalizeOptionalValue(command.Details);
        var locationId = command.LocationId ?? Guid.NewGuid();
        var document = new StoryLocationDocument(locationId, name, summary, details, command.IsArchived);
        var documents = story.Locations.Entries.ToList();
        ReplaceOrAdd(documents, document, x => x.Id);
        story.Locations = new ChatStoryLocationsDocument(documents);

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapLocation(story, document);
    }

    public async Task DeleteLocationAsync(DeleteLocation command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.Locations = new ChatStoryLocationsDocument(
            story.Locations.Entries
                .Where(x => x.Id != command.LocationId)
                .ToList());
        story.Items = new ChatStoryItemsDocument(
            story.Items.Entries
                .Select(x => x.LocationId == command.LocationId ? x with { LocationId = null } : x)
                .ToList());
        story.History = new ChatStoryHistoryDocument(
            story.History.Facts
                .Select(x => x with { LocationIds = RemoveId(x.LocationIds, command.LocationId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList(),
            story.History.TimelineEntries
                .Select(x => x with { LocationIds = RemoveId(x.LocationIds, command.LocationId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList());
        story.Scene = story.Scene.CurrentLocationId == command.LocationId
            ? story.Scene with { CurrentLocationId = null }
            : story.Scene;

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task SetCurrentLocationAsync(SetCurrentLocation command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        if (command.LocationId.HasValue
            && story.Locations.Entries.All(x => x.IsArchived || x.Id != command.LocationId.Value))
            throw new InvalidOperationException("The selected location could not be found.");

        story.Scene = story.Scene with { CurrentLocationId = command.LocationId };
        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task<StoryItemEditorView> UpsertItemAsync(UpsertItem command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var name = RequireValue(command.Name, "item name");
        var summary = NormalizeOptionalValue(command.Summary);
        var details = NormalizeOptionalValue(command.Details);
        var ownerCharacterId = NormalizeOptionalReference(command.OwnerCharacterId, story.Characters.Entries.Select(x => x.Id));
        var locationId = NormalizeOptionalReference(command.LocationId, story.Locations.Entries.Select(x => x.Id));
        var itemId = command.ItemId ?? Guid.NewGuid();
        var document = new StoryItemDocument(itemId, name, summary, details, ownerCharacterId, locationId, command.IsArchived);
        var documents = story.Items.Entries.ToList();
        ReplaceOrAdd(documents, document, x => x.Id);
        story.Items = new ChatStoryItemsDocument(documents);
        story.Scene = command.IsPresentInScene
            ? story.Scene with { PresentItemIds = AddId(story.Scene.PresentItemIds, itemId) }
            : story.Scene with { PresentItemIds = RemoveId(story.Scene.PresentItemIds, itemId) };

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapItem(story, document);
    }

    public async Task DeleteItemAsync(DeleteItem command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.Items = new ChatStoryItemsDocument(
            story.Items.Entries
                .Where(x => x.Id != command.ItemId)
                .ToList());
        story.History = new ChatStoryHistoryDocument(
            story.History.Facts
                .Select(x => x with { ItemIds = RemoveId(x.ItemIds, command.ItemId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList(),
            story.History.TimelineEntries
                .Select(x => x with { ItemIds = RemoveId(x.ItemIds, command.ItemId) })
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList());
        story.Scene = story.Scene with
        {
            PresentItemIds = RemoveId(story.Scene.PresentItemIds, command.ItemId)
        };

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task<StoryHistoryFactView> UpsertHistoryFactAsync(UpsertHistoryFact command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var title = RequireValue(command.Title, "fact title");
        var summary = NormalizeOptionalValue(command.Summary);
        var details = NormalizeOptionalValue(command.Details);
        var existing = story.History.Facts.FirstOrDefault(x => x.Id == command.FactId);
        var factId = command.FactId ?? Guid.NewGuid();
        var sortOrder = existing?.SortOrder ?? (story.History.Facts.Count == 0 ? 1 : story.History.Facts.Max(x => x.SortOrder) + 1);
        var document = new StoryHistoryFactDocument(
            factId,
            sortOrder,
            title,
            summary,
            details,
            NormalizeReferenceIds(command.CharacterIds, story.Characters.Entries.Select(x => x.Id)),
            NormalizeReferenceIds(command.LocationIds, story.Locations.Entries.Select(x => x.Id)),
            NormalizeReferenceIds(command.ItemIds, story.Items.Entries.Select(x => x.Id)));
        var facts = story.History.Facts.ToList();
        ReplaceOrAdd(facts, document, x => x.Id);
        story.History = new ChatStoryHistoryDocument(
            facts
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList(),
            story.History.TimelineEntries);

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapFact(story.History.Facts.First(x => x.Id == factId));
    }

    public async Task DeleteHistoryFactAsync(DeleteHistoryFact command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.History = new ChatStoryHistoryDocument(
            story.History.Facts
                .Where(x => x.Id != command.FactId)
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList(),
            story.History.TimelineEntries);

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task<StoryTimelineEntryView> UpsertTimelineEntryAsync(UpsertTimelineEntry command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var title = RequireValue(command.Title, "timeline title");
        var summary = NormalizeOptionalValue(command.Summary);
        var details = NormalizeOptionalValue(command.Details);
        var existing = story.History.TimelineEntries.FirstOrDefault(x => x.Id == command.TimelineEntryId);
        var timelineEntryId = command.TimelineEntryId ?? Guid.NewGuid();
        var sortOrder = command.SortOrder ?? existing?.SortOrder ?? (story.History.TimelineEntries.Count == 0 ? 1 : story.History.TimelineEntries.Max(x => x.SortOrder) + 1);
        var document = new StoryTimelineEntryDocument(
            timelineEntryId,
            sortOrder,
            NormalizeOptionalOptionalValue(command.WhenText),
            title,
            summary,
            details,
            NormalizeReferenceIds(command.CharacterIds, story.Characters.Entries.Select(x => x.Id)),
            NormalizeReferenceIds(command.LocationIds, story.Locations.Entries.Select(x => x.Id)),
            NormalizeReferenceIds(command.ItemIds, story.Items.Entries.Select(x => x.Id)));
        var timelineEntries = story.History.TimelineEntries.ToList();
        ReplaceOrAdd(timelineEntries, document, x => x.Id);
        story.History = new ChatStoryHistoryDocument(
            story.History.Facts,
            timelineEntries
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList());

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapTimelineEntry(story.History.TimelineEntries.First(x => x.Id == timelineEntryId));
    }

    public async Task DeleteTimelineEntryAsync(DeleteTimelineEntry command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.History = new ChatStoryHistoryDocument(
            story.History.Facts,
            story.History.TimelineEntries
                .Where(x => x.Id != command.TimelineEntryId)
                .OrderBy(x => x.SortOrder)
                .Select((x, index) => x with { SortOrder = index + 1 })
                .ToList());

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    public async Task UpdateScenePresenceAsync(UpdateScenePresence command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.Scene = story.Scene with
        {
            PresentCharacterIds = NormalizeReferenceIds(command.PresentCharacterIds, story.Characters.Entries.Select(x => x.Id)),
            PresentItemIds = NormalizeReferenceIds(command.PresentItemIds, story.Items.Entries.Select(x => x.Id))
        };

        await SaveStoryAsync(dbContext, story, cancellationToken);
    }

    private async Task<ChatStory?> GetOrCreateStoryAsync(AgentRp.Data.AppContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken);
        if (thread is null)
            return null;

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
        PublishStoryRefresh(threadId);
        return story;
    }

    private async Task SaveStoryAsync(AgentRp.Data.AppContext dbContext, ChatStory story, CancellationToken cancellationToken)
    {
        story.Characters = story.Characters;
        story.Locations = story.Locations;
        story.Items = story.Items;
        story.History = story.History;
        story.Scene = SanitizeScene(story);
        story.UpdatedUtc = DateTime.UtcNow;

        var thread = await dbContext.ChatThreads.FirstAsync(x => x.Id == story.ChatThreadId, cancellationToken);
        thread.UpdatedUtc = story.UpdatedUtc;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishStoryRefresh(story.ChatThreadId);
    }

    private void PublishStoryRefresh(Guid threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, occurredUtc));
    }

    private static ChatStorySidebarView MapSidebarView(
        ChatStory story,
        Guid? selectedSpeakerCharacterId,
        string? selectedAgentName,
        IReadOnlyList<AgentProviderOptionView> availableAgents,
        IReadOnlyList<AgentEndpointStatusView> managedAgentEndpoints,
        string? managedAgentEndpointsErrorMessage)
    {
        var locations = story.Locations.Entries
            .Where(x => !x.IsArchived)
            .ToDictionary(x => x.Id);
        var characters = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedSpeakerId = selectedSpeakerCharacterId.HasValue && characters.Any(x => x.Id == selectedSpeakerCharacterId.Value)
            ? selectedSpeakerCharacterId
            : null;
        var currentLocationName = story.Scene.CurrentLocationId.HasValue
            && locations.TryGetValue(story.Scene.CurrentLocationId.Value, out var currentLocation)
                ? currentLocation.Name
                : null;
        var sceneItemIds = story.Scene.PresentItemIds.ToHashSet();
        var items = story.Items.Entries
            .Where(x => !x.IsArchived && sceneItemIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryItemListItemView(x.Id, x.Name, x.Summary))
            .ToList();

        return new ChatStorySidebarView(
            story.ChatThreadId,
            currentLocationName,
            selectedSpeakerId.HasValue
                ? characters.First(x => x.Id == selectedSpeakerId.Value).Name
                : "Narrator",
            BuildSidebarSpeakers(story, characters, selectedSpeakerId),
            characters
                .Select(x => new StoryCharacterListItemView(
                    x.Id,
                    x.Name,
                    x.Summary,
                    story.Scene.PresentCharacterIds.Contains(x.Id),
                    selectedSpeakerId == x.Id))
                .ToList(),
            items,
            story.History.Facts.Count,
            story.History.TimelineEntries.Count,
            selectedAgentName,
            availableAgents,
            availableAgents.Count > 0,
            managedAgentEndpoints,
            managedAgentEndpointsErrorMessage);
    }

    private static IReadOnlyList<StorySidebarSpeakerView> BuildSidebarSpeakers(
        ChatStory story,
        IReadOnlyList<StoryCharacterDocument> characters,
        Guid? selectedSpeakerId)
    {
        var speakers = new List<StorySidebarSpeakerView>
        {
            new(
                null,
                "Narrator",
                "Injects scene facts and framing details.",
                true,
                true,
                !selectedSpeakerId.HasValue)
        };

        speakers.AddRange(characters.Select(character => new StorySidebarSpeakerView(
            character.Id,
            character.Name,
            character.Summary,
            false,
            story.Scene.PresentCharacterIds.Contains(character.Id),
            selectedSpeakerId == character.Id)));

        return speakers;
    }

    private static IReadOnlyList<StoryCharacterEditorView> MapCharacters(ChatStory story) =>
        story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapCharacter(story, x))
            .ToList();

    private static StoryCharacterEditorView MapCharacter(ChatStory story, StoryCharacterDocument document) => new(
        document.Id,
        document.Name,
        document.Summary,
        document.GeneralAppearance,
        document.CorePersonality,
        document.Relationships,
        document.PreferencesBeliefs,
        document.PrivateMotivations,
        story.Scene.PresentCharacterIds.Contains(document.Id));

    private static IReadOnlyList<StoryLocationListItemView> MapLocationList(ChatStory story) =>
        story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryLocationListItemView(
                x.Id,
                x.Name,
                x.Summary,
                story.Scene.CurrentLocationId == x.Id))
            .ToList();

    private static IReadOnlyList<StoryLocationEditorView> MapLocationEditors(ChatStory story) =>
        story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapLocation(story, x))
            .ToList();

    private static StoryLocationEditorView MapLocation(ChatStory story, StoryLocationDocument document) => new(
        document.Id,
        document.Name,
        document.Summary,
        document.Details,
        story.Scene.CurrentLocationId == document.Id);

    private static IReadOnlyList<StoryItemEditorView> MapItems(ChatStory story) =>
        story.Items.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapItem(story, x))
            .ToList();

    private static StoryItemEditorView MapItem(ChatStory story, StoryItemDocument document) => new(
        document.Id,
        document.Name,
        document.Summary,
        document.Details,
        document.OwnerCharacterId,
        document.LocationId,
        story.Scene.PresentItemIds.Contains(document.Id));

    private static StoryHistoryView MapHistory(ChatStory story) => new(
        story.History.Facts
            .OrderBy(x => x.SortOrder)
            .Select(MapFact)
            .ToList(),
        story.History.TimelineEntries
            .OrderBy(x => x.SortOrder)
            .Select(MapTimelineEntry)
            .ToList());

    private static StoryHistoryFactView MapFact(StoryHistoryFactDocument document) => new(
        document.Id,
        document.SortOrder,
        document.Title,
        document.Summary,
        document.Details,
        document.CharacterIds,
        document.LocationIds,
        document.ItemIds);

    private static StoryTimelineEntryView MapTimelineEntry(StoryTimelineEntryDocument document) => new(
        document.Id,
        document.SortOrder,
        document.WhenText,
        document.Title,
        document.Summary,
        document.Details,
        document.CharacterIds,
        document.LocationIds,
        document.ItemIds);

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

    private static string RequireValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"A {fieldName} is required.");

        return value.Trim();
    }

    private static string NormalizeOptionalValue(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptionalOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? NormalizeOptionalReference(Guid? id, IEnumerable<Guid> validIds)
    {
        if (!id.HasValue)
            return null;

        return validIds.Contains(id.Value) ? id : null;
    }

    private static IReadOnlyList<Guid> NormalizeReferenceIds(IEnumerable<Guid>? ids, IEnumerable<Guid> validIds)
    {
        if (ids is null)
            return [];

        var validIdSet = validIds.ToHashSet();
        return ids
            .Where(validIdSet.Contains)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<Guid> AddId(IReadOnlyList<Guid> ids, Guid id) =>
        ids.Contains(id)
            ? ids
            : ids.Concat([id]).ToList();

    private static IReadOnlyList<Guid> RemoveId(IReadOnlyList<Guid> ids, Guid id) =>
        ids.Where(x => x != id).ToList();

    private static void ReplaceOrAdd<TDocument, TKey>(
        List<TDocument> documents,
        TDocument document,
        Func<TDocument, TKey> keySelector)
        where TKey : notnull
    {
        var key = keySelector(document);
        var index = documents.FindIndex(x => EqualityComparer<TKey>.Default.Equals(keySelector(x), key));
        if (index >= 0)
            documents[index] = document;
        else
            documents.Add(document);
    }
}
