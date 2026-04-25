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
    private const string ImageSettingsKey = "story-image-generation-settings";

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

        var availableAgents = agentCatalog.GetEnabledAgents();
        var imageModels = agentCatalog.GetEnabledImageModels();
        var selectedModelId = agentCatalog.NormalizeSelectedModelId(thread.SelectedAiModelId);
        var selectedAgent = availableAgents.FirstOrDefault(x => x.ModelId == selectedModelId);
        var selectedProvider = selectedAgent is null
            ? null
            : await LoadSelectedProviderAsync(dbContext, threadId, selectedAgent, cancellationToken);

        IReadOnlyList<AgentEndpointStatusView> managedAgentEndpoints = [];
        string? managedAgentEndpointsErrorMessage = null;
        try
        {
            managedAgentEndpoints = await agentEndpointManagementService.GetStatusesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Loading Hugging Face endpoint status failed for chat {ThreadId}.", threadId);
            managedAgentEndpointsErrorMessage = UserFacingErrorMessageBuilder.Build("Loading Hugging Face endpoint status failed.", exception);
        }

        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        var imageSettings = await LoadImageSettingsAsync(dbContext, imageModels, cancellationToken);

        return MapSidebarView(
            story,
            thread.SelectedSpeakerCharacterId,
            selectedAgent?.Name,
            selectedModelId,
            availableAgents,
            selectedProvider,
            managedAgentEndpoints,
            managedAgentEndpointsErrorMessage,
            imageSettings,
            imageModels,
            primaryImages);
    }

    public async Task<IReadOnlyList<StoryCharacterEditorView>> GetCharactersAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        return MapCharacters(story, primaryImages);
    }

    public async Task<IReadOnlyList<StoryLocationListItemView>> GetLocationsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        return MapLocationList(story, primaryImages);
    }

    public async Task<IReadOnlyList<StoryLocationEditorView>> GetLocationEditorsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        return MapLocationEditors(story, primaryImages);
    }

    public async Task<IReadOnlyList<StoryItemEditorView>> GetItemsAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        return MapItems(story, primaryImages);
    }

    public async Task<StoryContextView?> GetStoryContextAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken);
        if (story is null)
            return null;

        return MapStoryContext(story);
    }

    public async Task<StoryCharacterEditorView> UpsertCharacterAsync(UpsertCharacter command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var name = RequireValue(command.Name, "character name");
        var userSheet = Normalize(command.UserSheet);
        var documents = story.Characters.Entries.ToList();
        var characterId = command.CharacterId ?? Guid.NewGuid();
        var existingDocument = documents.FirstOrDefault(x => x.Id == characterId);
        var userSheetChanged = existingDocument is null
            || !AreEquivalent(StoryCharacterModelSheetSupport.GetUserSheet(existingDocument), userSheet)
            || !string.Equals(existingDocument.Name, name, StringComparison.Ordinal);
        var nextUserSheetRevision = userSheetChanged
            ? Math.Max(existingDocument?.UserSheetRevision ?? 0, 0) + 1
            : existingDocument?.UserSheetRevision ?? 0;
        var document = new StoryCharacterDocument(
            characterId,
            name,
            MapUserSheetDocument(userSheet),
            existingDocument?.ModelSheet ?? StoryCharacterModelSheetDocument.Empty,
            nextUserSheetRevision,
            existingDocument?.ModelSheetReviewedAgainstRevision,
            command.IsArchived,
            existingDocument?.PrimaryImageId);

        ReplaceOrAdd(documents, document, x => x.Id);
        story.Characters = new ChatStoryCharactersDocument(documents);
        story.Scene = command.IsPresentInScene
            ? story.Scene with { PresentCharacterIds = AddId(story.Scene.PresentCharacterIds, characterId) }
            : story.Scene with { PresentCharacterIds = RemoveId(story.Scene.PresentCharacterIds, characterId) };

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapCharacter(story, document, new Dictionary<Guid, PrimaryImageData>());
    }

    public async Task<StoryCharacterEditorView> SaveCharacterModelSheetAsync(SaveStoryCharacterModelSheet command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        var documents = story.Characters.Entries.ToList();
        var existingDocument = documents.FirstOrDefault(x => x.Id == command.CharacterId)
            ?? throw new InvalidOperationException("Saving the model-ready character sheet failed because the selected character could not be found.");
        var modelSheet = Normalize(command.ModelSheet);
        if (IsEmpty(modelSheet))
            throw new InvalidOperationException($"Saving the model-ready character sheet failed because '{existingDocument.Name}' did not have any model-ready content to save.");

        var updatedDocument = existingDocument with
        {
            ModelSheet = MapModelSheetDocument(modelSheet),
            ModelSheetReviewedAgainstRevision = existingDocument.UserSheetRevision
        };

        ReplaceOrAdd(documents, updatedDocument, x => x.Id);
        story.Characters = new ChatStoryCharactersDocument(documents);

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapCharacter(story, updatedDocument, new Dictionary<Guid, PrimaryImageData>());
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
        var documents = story.Locations.Entries.ToList();
        var existingDocument = documents.FirstOrDefault(x => x.Id == locationId);
        var document = new StoryLocationDocument(locationId, name, summary, details, command.IsArchived, existingDocument?.PrimaryImageId);
        ReplaceOrAdd(documents, document, x => x.Id);
        story.Locations = new ChatStoryLocationsDocument(documents);

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapLocation(story, document, new Dictionary<Guid, PrimaryImageData>());
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
        var documents = story.Items.Entries.ToList();
        var existingDocument = documents.FirstOrDefault(x => x.Id == itemId);
        var document = new StoryItemDocument(itemId, name, summary, details, ownerCharacterId, locationId, command.IsArchived, existingDocument?.PrimaryImageId);
        ReplaceOrAdd(documents, document, x => x.Id);
        story.Items = new ChatStoryItemsDocument(documents);
        story.Scene = command.IsPresentInScene
            ? story.Scene with { PresentItemIds = AddId(story.Scene.PresentItemIds, itemId) }
            : story.Scene with { PresentItemIds = RemoveId(story.Scene.PresentItemIds, itemId) };

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapItem(story, document, new Dictionary<Guid, PrimaryImageData>());
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

    public async Task<StoryNarrativeSettingsView> UpdateStoryNarrativeSettingsAsync(UpdateStoryNarrativeSettings command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await GetOrCreateStoryAsync(dbContext, command.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat story could not be found.");

        story.StoryContext = new ChatStoryContextDocument(
            NormalizeOptionalValue(command.Genre),
            NormalizeOptionalValue(command.Setting),
            NormalizeOptionalValue(command.Tone),
            NormalizeOptionalValue(command.StoryDirection),
            command.ExplicitContent,
            command.ViolentContent);

        await SaveStoryAsync(dbContext, story, cancellationToken);
        return MapNarrativeSettings(story.StoryContext);
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
        story.StoryContext = story.StoryContext;
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
        Guid? selectedModelId,
        IReadOnlyList<AgentProviderOptionView> availableAgents,
        SelectedAiProviderSidebarView? selectedProvider,
        IReadOnlyList<AgentEndpointStatusView> managedAgentEndpoints,
        string? managedAgentEndpointsErrorMessage,
        ImageGenerationModelSettingsView imageGenerationSettings,
        IReadOnlyList<AgentProviderOptionView> imageModels,
        IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages)
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
        var currentLocation = story.Scene.CurrentLocationId.HasValue
            && locations.TryGetValue(story.Scene.CurrentLocationId.Value, out var resolvedCurrentLocation)
                ? resolvedCurrentLocation
                : null;
        var currentLocationName = currentLocation?.Name;
        var currentLocationPrimaryImageId = currentLocation?.PrimaryImageId;
        var sceneItemIds = story.Scene.PresentItemIds.ToHashSet();
        var items = story.Items.Entries
            .Where(x => !x.IsArchived && sceneItemIds.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryItemListItemView(
                x.Id,
                x.Name,
                x.Summary,
                x.PrimaryImageId,
                GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)))
            .ToList();

        return new ChatStorySidebarView(
            story.ChatThreadId,
            currentLocation?.Id,
            currentLocationName,
            currentLocationPrimaryImageId,
            GetPrimaryImageUrl(primaryImages, currentLocationPrimaryImageId),
            GetPrimaryImageCrop(primaryImages, currentLocationPrimaryImageId),
            selectedSpeakerId.HasValue
                ? characters.First(x => x.Id == selectedSpeakerId.Value).Name
                : "Narrator",
            BuildSidebarSpeakers(story, characters, selectedSpeakerId, primaryImages),
            characters
                .Select(x => new StoryCharacterListItemView(
                    x.Id,
                    x.Name,
                    StoryCharacterModelSheetSupport.GetUserSheet(x).Summary,
                    story.Scene.PresentCharacterIds.Contains(x.Id),
                    selectedSpeakerId == x.Id,
                    x.PrimaryImageId,
                    GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                    GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)))
                .ToList(),
            items,
            story.History.Facts.Count,
            story.History.TimelineEntries.Count,
            selectedAgentName,
            selectedModelId,
            availableAgents,
            availableAgents.Count > 0,
            selectedProvider,
            managedAgentEndpoints,
            managedAgentEndpointsErrorMessage,
            imageGenerationSettings,
            imageModels);
    }

    private static async Task<SelectedAiProviderSidebarView?> LoadSelectedProviderAsync(
        AgentRp.Data.AppContext dbContext,
        Guid threadId,
        AgentProviderOptionView selectedAgent,
        CancellationToken cancellationToken)
    {
        var provider = await dbContext.AiProviders
            .AsNoTracking()
            .Include(x => x.Metrics)
            .FirstOrDefaultAsync(x => x.Id == selectedAgent.ProviderId, cancellationToken);
        if (provider is null)
            return null;

        var tokenUsage = await dbContext.ProcessSteps
            .AsNoTracking()
            .Where(x => x.Run.ThreadId == threadId && x.Run.AiProviderId == selectedAgent.ProviderId)
            .GroupBy(x => 1)
            .Select(x => new
            {
                InputTokenCount = x.Sum(step => step.InputTokenCount ?? 0),
                OutputTokenCount = x.Sum(step => step.OutputTokenCount ?? 0),
                TotalTokenCount = x.Sum(step => step.TotalTokenCount ?? 0)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new SelectedAiProviderSidebarView(
            provider.Id,
            provider.Name,
            provider.ProviderKind,
            provider.LastMetricsRefreshUtc,
            provider.LastMetricsError,
            provider.Metrics
                .Where(IsSidebarProviderMetric)
                .OrderBy(x => x.Label)
                .Select(x => new AiProviderMetricView(x.MetricKind, x.Label, x.Value, x.Detail, x.RefreshedUtc))
                .ToList(),
            new AiProviderTokenUsageView(
                tokenUsage?.InputTokenCount ?? 0,
                tokenUsage?.OutputTokenCount ?? 0,
                tokenUsage?.TotalTokenCount ?? 0));
    }

    private static async Task<Dictionary<Guid, PrimaryImageData>> LoadPrimaryImagesAsync(
        AgentRp.Data.AppContext dbContext,
        ChatStory story,
        CancellationToken cancellationToken)
    {
        var imageIds = story.Characters.Entries.Select(x => x.PrimaryImageId)
            .Concat(story.Locations.Entries.Select(x => x.PrimaryImageId))
            .Concat(story.Items.Entries.Select(x => x.PrimaryImageId))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (imageIds.Count == 0)
            return [];

        return await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => imageIds.Contains(x.Id))
            .ToDictionaryAsync(
                x => x.Id,
                x => new PrimaryImageData(StoryImageUrlBuilder.Build(x.Id), BuildCrop(x)),
                cancellationToken);
    }

    private static async Task<ImageGenerationModelSettingsView> LoadImageSettingsAsync(
        AgentRp.Data.AppContext dbContext,
        IReadOnlyList<AgentProviderOptionView> imageModels,
        CancellationToken cancellationToken)
    {
        var setting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == ImageSettingsKey, cancellationToken);
        var saved = ChatStoryJson.Deserialize(setting?.JsonValue, StoryImageGenerationSettingsDocument.Default);
        var selectedModelId = saved.SelectedModelId.HasValue && imageModels.Any(x => x.ModelId == saved.SelectedModelId.Value)
            ? saved.SelectedModelId
            : imageModels.FirstOrDefault()?.ModelId;

        return new ImageGenerationModelSettingsView(
            selectedModelId,
            string.IsNullOrWhiteSpace(saved.Size) ? StoryImageGenerationSettingsDocument.Default.Size : saved.Size,
            string.IsNullOrWhiteSpace(saved.Quality) ? StoryImageGenerationSettingsDocument.Default.Quality : saved.Quality,
            string.IsNullOrWhiteSpace(saved.ReferenceFidelity) ? StoryImageGenerationSettingsDocument.Default.ReferenceFidelity : saved.ReferenceFidelity);
    }

    private static string? GetPrimaryImageUrl(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.ImageUrl : null;

    private static StoryImageAvatarCropView GetPrimaryImageCrop(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.Crop : StoryImageAvatarCropView.Default;

    private static StoryImageAvatarCropView BuildCrop(StoryImageAsset image) =>
        new(
            Math.Clamp(image.AvatarFocusXPercent ?? StoryImageAvatarCropView.Default.FocusXPercent, 0, 100),
            Math.Clamp(image.AvatarFocusYPercent ?? StoryImageAvatarCropView.Default.FocusYPercent, 0, 100),
            Math.Clamp(image.AvatarZoomPercent ?? StoryImageAvatarCropView.Default.ZoomPercent, 100, 300));

    private static bool IsSidebarProviderMetric(AiProviderMetric metric) =>
        !string.Equals(metric.MetricKind, "models", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(metric.MetricKind, "connection", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<StorySidebarSpeakerView> BuildSidebarSpeakers(
        ChatStory story,
        IReadOnlyList<StoryCharacterDocument> characters,
        Guid? selectedSpeakerId,
        IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages)
    {
        var speakers = new List<StorySidebarSpeakerView>
        {
            new(
                null,
                "Narrator",
                "Injects scene facts and framing details.",
                true,
                true,
                !selectedSpeakerId.HasValue,
                null,
                null,
                StoryImageAvatarCropView.Default)
        };

        speakers.AddRange(characters.Select(character => new StorySidebarSpeakerView(
            character.Id,
            character.Name,
            StoryCharacterModelSheetSupport.GetUserSheet(character).Summary,
            false,
            story.Scene.PresentCharacterIds.Contains(character.Id),
            selectedSpeakerId == character.Id,
            character.PrimaryImageId,
            GetPrimaryImageUrl(primaryImages, character.PrimaryImageId),
            GetPrimaryImageCrop(primaryImages, character.PrimaryImageId))));

        return speakers;
    }

    private static IReadOnlyList<StoryCharacterEditorView> MapCharacters(ChatStory story, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) =>
        story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapCharacter(story, x, primaryImages))
            .ToList();

    private static StoryCharacterEditorView MapCharacter(ChatStory story, StoryCharacterDocument document, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) => new(
        document.Id,
        document.Name,
        MapUserSheetView(StoryCharacterModelSheetSupport.GetUserSheet(document)),
        MapModelSheetView(StoryCharacterModelSheetSupport.GetModelSheet(document)),
        StoryCharacterModelSheetSupport.GetStatus(document),
        StoryCharacterModelSheetSupport.IsReady(document),
        story.Scene.PresentCharacterIds.Contains(document.Id),
        document.PrimaryImageId,
        GetPrimaryImageUrl(primaryImages, document.PrimaryImageId),
        GetPrimaryImageCrop(primaryImages, document.PrimaryImageId));

    private static IReadOnlyList<StoryLocationListItemView> MapLocationList(ChatStory story, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) =>
        story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryLocationListItemView(
                x.Id,
                x.Name,
                x.Summary,
                story.Scene.CurrentLocationId == x.Id,
                x.PrimaryImageId,
                GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)))
            .ToList();

    private static IReadOnlyList<StoryLocationEditorView> MapLocationEditors(ChatStory story, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) =>
        story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapLocation(story, x, primaryImages))
            .ToList();

    private static StoryLocationEditorView MapLocation(ChatStory story, StoryLocationDocument document, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) => new(
        document.Id,
        document.Name,
        document.Summary,
        document.Details,
        story.Scene.CurrentLocationId == document.Id,
        document.PrimaryImageId,
        GetPrimaryImageUrl(primaryImages, document.PrimaryImageId),
        GetPrimaryImageCrop(primaryImages, document.PrimaryImageId));

    private static IReadOnlyList<StoryItemEditorView> MapItems(ChatStory story, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) =>
        story.Items.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => MapItem(story, x, primaryImages))
            .ToList();

    private static StoryItemEditorView MapItem(ChatStory story, StoryItemDocument document, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages) => new(
        document.Id,
        document.Name,
        document.Summary,
        document.Details,
        document.OwnerCharacterId,
        document.LocationId,
        story.Scene.PresentItemIds.Contains(document.Id),
        document.PrimaryImageId,
        GetPrimaryImageUrl(primaryImages, document.PrimaryImageId),
        GetPrimaryImageCrop(primaryImages, document.PrimaryImageId));

    private static StoryContextView MapStoryContext(ChatStory story) => new(
        MapNarrativeSettings(story.StoryContext),
        story.History.Facts
            .OrderBy(x => x.SortOrder)
            .Select(MapFact)
            .ToList(),
        story.History.TimelineEntries
            .OrderBy(x => x.SortOrder)
            .Select(MapTimelineEntry)
            .ToList());

    private static StoryNarrativeSettingsView MapNarrativeSettings(ChatStoryContextDocument document) => new(
        document.Genre,
        document.Setting,
        document.Tone,
        document.StoryDirection,
        document.ExplicitContent,
        document.ViolentContent);

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

    private static StoryCharacterUserSheetView Normalize(StoryCharacterUserSheetView view) => new(
        NormalizeOptionalValue(view.Summary),
        NormalizeOptionalValue(view.GeneralAppearance),
        NormalizeOptionalValue(view.CorePersonality),
        NormalizeOptionalValue(view.Relationships),
        NormalizeOptionalValue(view.PreferencesBeliefs),
        NormalizeOptionalValue(view.PrivateMotivations));

    private static StoryCharacterModelSheetView Normalize(StoryCharacterModelSheetView view) => new(
        NormalizeOptionalValue(view.Summary),
        NormalizeOptionalValue(view.Appearance),
        NormalizeOptionalValue(view.Voice),
        NormalizeOptionalValue(view.Hides),
        NormalizeOptionalValue(view.Tendency),
        NormalizeOptionalValue(view.Constraint),
        NormalizeOptionalValue(view.Relationships),
        NormalizeOptionalValue(view.LikesBeliefs),
        NormalizeOptionalValue(view.PrivateMotivations));

    private static StoryCharacterUserSheetDocument MapUserSheetDocument(StoryCharacterUserSheetView view) => new(
        view.Summary,
        view.GeneralAppearance,
        view.CorePersonality,
        view.Relationships,
        view.PreferencesBeliefs,
        view.PrivateMotivations);

    private static StoryCharacterModelSheetDocument MapModelSheetDocument(StoryCharacterModelSheetView view) => new(
        view.Summary,
        view.Appearance,
        view.Voice,
        view.Hides,
        view.Tendency,
        view.Constraint,
        view.Relationships,
        view.LikesBeliefs,
        view.PrivateMotivations);

    private static StoryCharacterUserSheetView MapUserSheetView(StoryCharacterUserSheetDocument document) => new(
        document.Summary,
        document.GeneralAppearance,
        document.CorePersonality,
        document.Relationships,
        document.PreferencesBeliefs,
        document.PrivateMotivations);

    private static StoryCharacterModelSheetView MapModelSheetView(StoryCharacterModelSheetDocument document) => new(
        document.Summary,
        document.Appearance,
        document.Voice,
        document.Hides,
        document.Tendency,
        document.Constraint,
        document.Relationships,
        document.LikesBeliefs,
        document.PrivateMotivations);

    private static StoryCharacterModelSheetStatus GetModelSheetStatus(StoryCharacterDocument document)
    {
        if (IsEmpty(StoryCharacterModelSheetSupport.GetModelSheet(document)))
            return StoryCharacterModelSheetStatus.Missing;

        return document.ModelSheetReviewedAgainstRevision == document.UserSheetRevision
            ? StoryCharacterModelSheetStatus.Ready
            : StoryCharacterModelSheetStatus.Stale;
    }

    private static bool AreEquivalent(StoryCharacterUserSheetDocument left, StoryCharacterUserSheetView right) =>
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
        && string.Equals(left.GeneralAppearance, right.GeneralAppearance, StringComparison.Ordinal)
        && string.Equals(left.CorePersonality, right.CorePersonality, StringComparison.Ordinal)
        && string.Equals(left.Relationships, right.Relationships, StringComparison.Ordinal)
        && string.Equals(left.PreferencesBeliefs, right.PreferencesBeliefs, StringComparison.Ordinal)
        && string.Equals(left.PrivateMotivations, right.PrivateMotivations, StringComparison.Ordinal);

    private static bool IsEmpty(StoryCharacterModelSheetView view) =>
        string.IsNullOrWhiteSpace(view.Summary)
        && string.IsNullOrWhiteSpace(view.Appearance)
        && string.IsNullOrWhiteSpace(view.Voice)
        && string.IsNullOrWhiteSpace(view.Hides)
        && string.IsNullOrWhiteSpace(view.Tendency)
        && string.IsNullOrWhiteSpace(view.Constraint)
        && string.IsNullOrWhiteSpace(view.Relationships)
        && string.IsNullOrWhiteSpace(view.LikesBeliefs)
        && string.IsNullOrWhiteSpace(view.PrivateMotivations);

    private static bool IsEmpty(StoryCharacterModelSheetDocument document) =>
        string.IsNullOrWhiteSpace(document.Summary)
        && string.IsNullOrWhiteSpace(document.Appearance)
        && string.IsNullOrWhiteSpace(document.Voice)
        && string.IsNullOrWhiteSpace(document.Hides)
        && string.IsNullOrWhiteSpace(document.Tendency)
        && string.IsNullOrWhiteSpace(document.Constraint)
        && string.IsNullOrWhiteSpace(document.Relationships)
        && string.IsNullOrWhiteSpace(document.LikesBeliefs)
        && string.IsNullOrWhiteSpace(document.PrivateMotivations);

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

    private sealed record PrimaryImageData(string ImageUrl, StoryImageAvatarCropView Crop);
}
