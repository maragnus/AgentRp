using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Services;

public sealed class StoryImageService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IAgentCatalog agentCatalog,
    IThreadAgentService threadAgentService,
    IStoryImageOptimizationService imageOptimizationService,
    IActivityNotifier activityNotifier,
    ILogger<StoryImageService> logger) : IStoryImageService
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int TransientImageLifetimeHours = 24;
    private const string ImageSettingsKey = "story-image-generation-settings";
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp"
    };
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<StoryImageGenerationDialogView> GetGenerationDialogAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, threadId, cancellationToken);
        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);
        var entities = BuildEntityOptions(story, primaryImages);
        var referenceImages = await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId && !x.IsTransient)
            .Include(x => x.Links)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(48)
            .ToListAsync(cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);

        return new StoryImageGenerationDialogView(
            threadId,
            settings,
            agentCatalog.GetEnabledImageModels(),
            entities,
            referenceImages.Select(x => MapImage(x, false, BuildLinkedEntities(x.Links))).ToList());
    }

    public async Task<StoryImageGalleryView> GetGalleryAsync(Guid threadId, StoryEntityKind entityKind, Guid entityId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, threadId, cancellationToken);
        var primaryImageId = ResolvePrimaryImageId(story, entityKind, entityId);
        var dataKind = MapEntityKind(entityKind);
        var images = await dbContext.StoryImageLinks
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId && x.EntityKind == dataKind && x.EntityId == entityId)
            .Include(x => x.Image)
            .OrderByDescending(x => x.Image.CreatedUtc)
            .Select(x => x.Image)
            .ToListAsync(cancellationToken);

        return new StoryImageGalleryView(
            threadId,
            entityKind,
            entityId,
            primaryImageId,
            images.Select(x => MapImage(x, primaryImageId == x.Id)).ToList());
    }

    public async Task<StoryImageCatalogView> GetCatalogAsync(Guid threadId, string? searchText, int take, CancellationToken cancellationToken)
    {
        var normalizedSearch = NormalizeOptional(searchText);
        var limit = Math.Clamp(take, 1, 100);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, threadId, cancellationToken);
        IQueryable<StoryImageAsset> imageQuery = dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId && !x.IsTransient)
            .OrderByDescending(x => x.CreatedUtc);
        if (normalizedSearch is null)
            imageQuery = imageQuery.Take(limit);

        var images = await imageQuery.ToListAsync(cancellationToken);

        if (normalizedSearch is not null)
        {
            var matchingEntityImageIds = await GetSearchMatchingImageIdsAsync(dbContext, story, threadId, normalizedSearch, cancellationToken);
            images = images
                .Where(x => MatchesImageSearch(x, normalizedSearch) || matchingEntityImageIds.Contains(x.Id))
                .Take(limit)
                .ToList();
        }

        return new StoryImageCatalogView(threadId, normalizedSearch ?? string.Empty, limit, images.Select(x => MapImage(x, false)).ToList());
    }

    public async Task<StoryImageReferenceView?> GetImageAsync(Guid threadId, Guid imageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var image = await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId && x.Id == imageId)
            .FirstOrDefaultAsync(cancellationToken);

        return image is null ? null : MapImage(image, false);
    }

    public async Task<StoryImageReferenceView> UploadAsync(UploadStoryImage request, CancellationToken cancellationToken)
    {
        ValidateImageBytes(request.ContentType, request.Bytes, request.FileName);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, request.ThreadId, cancellationToken);
        EnsureEntityExists(story, request.EntityKind, request.EntityId);

        var dimensions = StoryImageDimensions.TryRead(request.Bytes, request.ContentType);
        var now = DateTime.UtcNow;
        var image = new StoryImageAsset
        {
            Id = Guid.NewGuid(),
            ThreadId = request.ThreadId,
            Bytes = request.Bytes,
            ContentType = request.ContentType,
            FileName = NormalizeOptional(request.FileName),
            Title = BuildImageTitle(request.FileName, request.EntityKind, story, request.EntityId),
            Width = dimensions?.Width,
            Height = dimensions?.Height,
            SourceKind = StoryImageSourceKind.Uploaded,
            CreatedUtc = now
        };

        dbContext.StoryImageAssets.Add(image);
        AddLink(dbContext, request.ThreadId, image.Id, request.EntityKind, request.EntityId, StoryImageLinkPurpose.Gallery, now);

        if (request.SetPrimary)
            SetPrimaryImage(story, request.EntityKind, request.EntityId, image.Id);

        await SaveStoryAndImagesAsync(dbContext, story, cancellationToken);
        await imageOptimizationService.QueueImageAsync(image.Id, cancellationToken);
        PublishRefresh(request.ThreadId);
        return MapImage(image, request.SetPrimary);
    }

    public async Task SetPrimaryImageAsync(SetStoryEntityPrimaryImage request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, request.ThreadId, cancellationToken);
        EnsureEntityExists(story, request.EntityKind, request.EntityId);

        if (request.ImageId.HasValue)
        {
            var dataKind = MapEntityKind(request.EntityKind);
            var linked = await dbContext.StoryImageLinks.AnyAsync(
                x => x.ThreadId == request.ThreadId
                    && x.EntityKind == dataKind
                    && x.EntityId == request.EntityId
                    && x.ImageId == request.ImageId.Value,
                cancellationToken);
            if (!linked)
            {
                var assetExists = await dbContext.StoryImageAssets.AnyAsync(x => x.ThreadId == request.ThreadId && x.Id == request.ImageId.Value, cancellationToken);
                if (!assetExists)
                    throw new InvalidOperationException("Selecting the primary image failed because the image could not be found in this chat.");

                AddLink(dbContext, request.ThreadId, request.ImageId.Value, request.EntityKind, request.EntityId, StoryImageLinkPurpose.Gallery, DateTime.UtcNow);
            }
        }

        SetPrimaryImage(story, request.EntityKind, request.EntityId, request.ImageId);
        await SaveStoryAndImagesAsync(dbContext, story, cancellationToken);
        if (request.ImageId.HasValue)
            await imageOptimizationService.QueueImageAsync(request.ImageId.Value, cancellationToken);

        PublishRefresh(request.ThreadId);
    }

    public async Task<StoryImageReferenceView> UpdateCropAsync(UpdateStoryImageCrop request, CancellationToken cancellationToken)
    {
        var crop = NormalizeCrop(request.FocusXPercent, request.FocusYPercent, request.ZoomPercent);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var image = await dbContext.StoryImageAssets
            .FirstOrDefaultAsync(x => x.ThreadId == request.ThreadId && x.Id == request.ImageId, cancellationToken)
            ?? throw new InvalidOperationException("Saving the image crop failed because the image could not be found in this chat.");

        image.AvatarFocusXPercent = crop.FocusXPercent;
        image.AvatarFocusYPercent = crop.FocusYPercent;
        image.AvatarZoomPercent = crop.ZoomPercent;
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh(request.ThreadId);
        return MapImage(image, false);
    }

    public async Task RemoveLinkAsync(RemoveStoryImageLink request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, request.ThreadId, cancellationToken);
        var dataKind = MapEntityKind(request.EntityKind);
        var links = await dbContext.StoryImageLinks
            .Where(x => x.ThreadId == request.ThreadId && x.EntityKind == dataKind && x.EntityId == request.EntityId && x.ImageId == request.ImageId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return;

        dbContext.StoryImageLinks.RemoveRange(links);
        if (ResolvePrimaryImageId(story, request.EntityKind, request.EntityId) == request.ImageId)
            SetPrimaryImage(story, request.EntityKind, request.EntityId, null);

        await SaveStoryAndImagesAsync(dbContext, story, cancellationToken);
        PublishRefresh(request.ThreadId);
    }

    public async Task<StoryImageGenerationResult> GenerateAsync(GenerateStoryImage request, CancellationToken cancellationToken)
    {
        var transientSessionId = Guid.NewGuid();
        var preview = await GeneratePreviewAsync(
            new GenerateStoryImagePreview(
                request.ThreadId,
                transientSessionId,
                request.UserPrompt,
                null,
                request.Entities,
                request.ReferenceImageIds,
                request.Size,
                request.Quality,
                request.ReferenceFidelity),
            null,
            cancellationToken);
        var saved = await SaveGeneratedImageAsync(
            new SaveGeneratedStoryImage(
                request.ThreadId,
                preview.ImageId,
                transientSessionId,
                request.Entities,
                request.SetPrimaryForFirstEntity),
            cancellationToken);

        return new StoryImageGenerationResult(saved.ImageId, saved.ImageUrl, saved.FinalPrompt ?? preview.FinalPrompt, saved.GenerationRationale ?? preview.Rationale);
    }

    public async Task<StoryImagePreviewResult> GeneratePreviewAsync(
        GenerateStoryImagePreview request,
        IProgress<StoryImageGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedPrompt = request.UserPrompt.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
            throw new InvalidOperationException("Generating an image failed because the prompt was empty.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, request.ThreadId, cancellationToken);
        var entitySelections = NormalizeEntitySelections(story, request.Entities);
        var referenceImages = await LoadReferenceImagesAsync(dbContext, request.ThreadId, request.ReferenceImageIds, cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        var imageModelId = settings.SelectedModelId
            ?? throw new InvalidOperationException("Generating an image failed because no image generation model is selected.");
        var model = await LoadImageModelAsync(dbContext, imageModelId, cancellationToken);

        progress?.Report(new StoryImageGenerationProgress("Preparing the image prompt...", null, null, null, null));
        var prompt = BuildPrompt(request.ReviewedFinalPrompt, normalizedPrompt, request.Entities.Count > 0)
            ?? await ComposePromptAsync(request.ThreadId, story, normalizedPrompt, entitySelections, cancellationToken);
        var client = CreateImageClient(model);
        GeneratedImage generated;
        try
        {
            progress?.Report(new StoryImageGenerationProgress("Sending the prompt to the image model...", null, null, prompt.FinalPrompt, prompt.Rationale));
            generated = await client.GenerateAsync(
                new ImageGenerationRequest(
                    prompt.FinalPrompt,
                    request.Size,
                    request.Quality,
                    request.ReferenceFidelity,
                    referenceImages),
                progress,
                cancellationToken);
        }
        catch (ExternalServiceFailureException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            logger.LogError(exception, "Image generation bad request for chat {ThreadId} with model {ModelId}.", request.ThreadId, model.ModelId);
            throw new StoryImageGenerationBadRequestException(
                UserFacingErrorMessageBuilder.Build($"Generating an image with '{model.DisplayName}' failed.", exception),
                normalizedPrompt,
                prompt.FinalPrompt,
                exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Image generation failed for chat {ThreadId} with model {ModelId}.", request.ThreadId, model.ModelId);
            throw new InvalidOperationException(UserFacingErrorMessageBuilder.Build($"Generating an image with '{model.DisplayName}' failed.", exception), exception);
        }

        ValidateImageBytes(generated.ContentType, generated.Bytes, "generated image");
        var dimensions = StoryImageDimensions.TryRead(generated.Bytes, generated.ContentType);
        var now = DateTime.UtcNow;
        var image = new StoryImageAsset
        {
            Id = Guid.NewGuid(),
            ThreadId = request.ThreadId,
            Bytes = generated.Bytes,
            ContentType = generated.ContentType,
            FileName = generated.FileName ?? "generated-image.png",
            Title = BuildGeneratedTitle(entitySelections, story),
            Width = dimensions?.Width,
            Height = dimensions?.Height,
            SourceKind = StoryImageSourceKind.Generated,
            UserPrompt = normalizedPrompt,
            FinalPrompt = prompt.FinalPrompt,
            GenerationRationale = prompt.Rationale,
            AiProviderId = model.ProviderId,
            AiProviderKind = model.ProviderKind,
            AiProviderName = model.ProviderName,
            AiModelId = model.ModelId,
            AiModelName = model.DisplayName,
            ProviderModelId = model.ProviderModelId,
            IsTransient = true,
            TransientSessionId = request.TransientSessionId,
            TransientExpiresUtc = now.AddHours(TransientImageLifetimeHours),
            CreatedUtc = now
        };
        dbContext.StoryImageAssets.Add(image);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new StoryImagePreviewResult(
            image.Id,
            StoryImageUrlBuilder.Build(image.Id),
            normalizedPrompt,
            prompt.FinalPrompt,
            prompt.Rationale);
    }

    public async Task<StoryImageReferenceView> SaveGeneratedImageAsync(SaveGeneratedStoryImage request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await LoadStoryAsync(dbContext, request.ThreadId, cancellationToken);
        var entitySelections = NormalizeEntitySelections(story, request.Entities);
        var now = DateTime.UtcNow;
        var image = await dbContext.StoryImageAssets
            .FirstOrDefaultAsync(
                x => x.ThreadId == request.ThreadId
                    && x.Id == request.ImageId
                    && x.IsTransient
                    && x.TransientSessionId == request.TransientSessionId,
                cancellationToken)
            ?? throw new InvalidOperationException("Saving the generated image failed because the selected preview could not be found.");

        image.IsTransient = false;
        image.TransientSessionId = null;
        image.TransientExpiresUtc = null;
        image.Title = BuildGeneratedTitle(entitySelections, story);

        foreach (var entity in entitySelections)
            AddLink(dbContext, request.ThreadId, image.Id, entity.EntityKind, entity.EntityId, StoryImageLinkPurpose.Gallery, now);

        if (request.SetPrimaryForFirstEntity && entitySelections.Count > 0)
        {
            var first = entitySelections[0];
            SetPrimaryImage(story, first.EntityKind, first.EntityId, image.Id);
        }

        await DeleteTransientImagesAsync(dbContext, request.ThreadId, request.TransientSessionId, image.Id, cancellationToken);
        await SaveStoryAndImagesAsync(dbContext, story, cancellationToken);
        await imageOptimizationService.QueueImageAsync(image.Id, cancellationToken);
        PublishRefresh(request.ThreadId);
        return MapImage(image, request.SetPrimaryForFirstEntity);
    }

    public async Task DiscardTransientImagesAsync(Guid threadId, Guid transientSessionId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await DeleteTransientImagesAsync(dbContext, threadId, transientSessionId, null, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImageGenerationModelSettingsView> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var imageModels = agentCatalog.GetEnabledImageModels();
        var setting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == ImageSettingsKey, cancellationToken);
        var saved = ChatStoryJson.Deserialize(setting?.JsonValue, StoryImageGenerationSettingsDocument.Default);
        var selectedModelId = saved.SelectedModelId.HasValue && imageModels.Any(x => x.ModelId == saved.SelectedModelId.Value)
            ? saved.SelectedModelId
            : imageModels.FirstOrDefault()?.ModelId;

        return new ImageGenerationModelSettingsView(
            selectedModelId,
            NormalizeOption(saved.Size, StoryImageGenerationSettingsDocument.Default.Size),
            NormalizeOption(saved.Quality, StoryImageGenerationSettingsDocument.Default.Quality),
            NormalizeOption(saved.ReferenceFidelity, StoryImageGenerationSettingsDocument.Default.ReferenceFidelity));
    }

    public async Task SaveSettingsAsync(ImageGenerationModelSettingsView settings, CancellationToken cancellationToken)
    {
        var imageModels = agentCatalog.GetEnabledImageModels();
        if (settings.SelectedModelId.HasValue && imageModels.All(x => x.ModelId != settings.SelectedModelId.Value))
            throw new InvalidOperationException("Saving image settings failed because the selected image model is not enabled.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(x => x.Key == ImageSettingsKey, cancellationToken);
        if (setting is null)
        {
            setting = new AppSetting { Key = ImageSettingsKey, JsonValue = string.Empty, UpdatedUtc = DateTime.UtcNow };
            dbContext.AppSettings.Add(setting);
        }

        setting.JsonValue = ChatStoryJson.Serialize(new StoryImageGenerationSettingsDocument(
            settings.SelectedModelId,
            NormalizeOption(settings.Size, StoryImageGenerationSettingsDocument.Default.Size),
            NormalizeOption(settings.Quality, StoryImageGenerationSettingsDocument.Default.Quality),
            NormalizeOption(settings.ReferenceFidelity, StoryImageGenerationSettingsDocument.Default.ReferenceFidelity)));
        setting.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh(null);
    }

    private async Task<ComposedImagePrompt> ComposePromptAsync(
        Guid threadId,
        ChatStory story,
        string userPrompt,
        IReadOnlyList<StoryImageEntitySelection> entities,
        CancellationToken cancellationToken)
    {
        var agent = await threadAgentService.GetSelectedAgentAsync(threadId, cancellationToken);
        if (agent is null)
            return new ComposedImagePrompt(userPrompt, "No text model was configured, so the user prompt was used directly.");

        var entityContext = BuildEntityPromptContext(story, entities);
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "You turn roleplaying story context into concise, vivid image generation prompts. Return JSON only."),
            new(Microsoft.Extensions.AI.ChatRole.User, $$"""
            User request:
            {{userPrompt}}

            Story entities:
            {{entityContext}}

            Write a final image prompt that preserves the user's intent and incorporates the selected entity details. Avoid prose explanations inside the final prompt.
            """)
        };
        var response = await agent.ChatClient.GetResponseAsync<ImagePromptResponse>(
            messages,
            options: new ChatOptions { Temperature = 0.4f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);

        var finalPrompt = string.IsNullOrWhiteSpace(response.Result.FinalPrompt)
            ? userPrompt
            : response.Result.FinalPrompt.Trim();
        var rationale = string.IsNullOrWhiteSpace(response.Result.Rationale)
            ? "Composed the prompt from the user request and selected story entities."
            : response.Result.Rationale.Trim();
        return new ComposedImagePrompt(finalPrompt, rationale);
    }

    private static ComposedImagePrompt? BuildPrompt(string? reviewedFinalPrompt, string userPrompt, bool hasEntityContext)
    {
        var trimmed = reviewedFinalPrompt?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var rationale = hasEntityContext
            ? "Used the reviewed prompt directly with the selected reference context."
            : "Used the reviewed prompt directly.";
        return new ComposedImagePrompt(trimmed, rationale);
    }

    private IImageGenerationClient CreateImageClient(ImageModelRow model) =>
        model.ProviderKind switch
        {
            AiProviderKind.OpenAI => new OpenAiImageGenerationClient(httpClientFactory, model),
            AiProviderKind.Grok => new GrokImageGenerationClient(httpClientFactory, model),
            _ => throw new InvalidOperationException($"Generating an image failed because '{model.ProviderName}' does not support image generation.")
        };

    private static async Task<ChatStory> LoadStoryAsync(DbAppContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        var threadExists = await dbContext.ChatThreads.AnyAsync(x => x.Id == threadId, cancellationToken);
        if (!threadExists)
            throw new InvalidOperationException("Loading story images failed because the selected chat could not be found.");

        return await dbContext.ChatStories.FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? new ChatStory { ChatThreadId = threadId, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
    }

    private static async Task SaveStoryAndImagesAsync(DbAppContext dbContext, ChatStory story, CancellationToken cancellationToken)
    {
        var trackedStory = await dbContext.ChatStories.FirstOrDefaultAsync(x => x.ChatThreadId == story.ChatThreadId, cancellationToken);
        if (trackedStory is null)
        {
            dbContext.ChatStories.Add(story);
            trackedStory = story;
        }

        trackedStory.Characters = story.Characters;
        trackedStory.Locations = story.Locations;
        trackedStory.Items = story.Items;
        trackedStory.History = story.History;
        trackedStory.StoryContext = story.StoryContext;
        trackedStory.Scene = story.Scene;
        trackedStory.UpdatedUtc = DateTime.UtcNow;
        dbContext.Entry(trackedStory).Property(x => x.CharactersJson).IsModified = true;
        dbContext.Entry(trackedStory).Property(x => x.LocationsJson).IsModified = true;
        dbContext.Entry(trackedStory).Property(x => x.ItemsJson).IsModified = true;

        var thread = await dbContext.ChatThreads.FirstAsync(x => x.Id == story.ChatThreadId, cancellationToken);
        thread.UpdatedUtc = trackedStory.UpdatedUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<StoryImageEntityOptionView> BuildEntityOptions(ChatStory story, IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages)
    {
        var characters = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryImageEntityOptionView(
                StoryEntityKind.Character,
                x.Id,
                x.Name,
                BuildCharacterDescription(x),
                x.PrimaryImageId,
                GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)));
        var locations = story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryImageEntityOptionView(
                StoryEntityKind.Location,
                x.Id,
                x.Name,
                $"{x.Summary}\n{x.Details}".Trim(),
                x.PrimaryImageId,
                GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)));
        var items = story.Items.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryImageEntityOptionView(
                StoryEntityKind.Item,
                x.Id,
                x.Name,
                $"{x.Summary}\n{x.Details}".Trim(),
                x.PrimaryImageId,
                GetPrimaryImageUrl(primaryImages, x.PrimaryImageId),
                GetPrimaryImageCrop(primaryImages, x.PrimaryImageId)));

        return characters.Concat(locations).Concat(items).ToList();
    }

    private static string BuildEntityPromptContext(ChatStory story, IReadOnlyList<StoryImageEntitySelection> entities)
    {
        var builder = new StringBuilder();
        foreach (var entity in entities)
        {
            if (entity.EntityKind == StoryEntityKind.Character)
            {
                var character = story.Characters.Entries.First(x => x.Id == entity.EntityId);
                builder.AppendLine($"Character: {character.Name}");
                builder.AppendLine(BuildCharacterDescription(character));
            }
            else if (entity.EntityKind == StoryEntityKind.Location)
            {
                var location = story.Locations.Entries.First(x => x.Id == entity.EntityId);
                builder.AppendLine($"Location: {location.Name}");
                builder.AppendLine($"{location.Summary}\n{location.Details}".Trim());
            }
            else if (entity.EntityKind == StoryEntityKind.Item)
            {
                var item = story.Items.Entries.First(x => x.Id == entity.EntityId);
                builder.AppendLine($"Item: {item.Name}");
                builder.AppendLine($"{item.Summary}\n{item.Details}".Trim());
            }

            builder.AppendLine();
        }

        return builder.Length == 0 ? "No story entities selected." : builder.ToString().Trim();
    }

    private static string BuildCharacterDescription(StoryCharacterDocument character)
    {
        var user = StoryCharacterModelSheetSupport.GetUserSheet(character);
        var model = StoryCharacterModelSheetSupport.GetModelSheet(character);
        return $$"""
        Summary: {{FirstNonEmpty(model.Summary, user.Summary)}}
        Appearance: {{FirstNonEmpty(model.Appearance, user.GeneralAppearance)}}
        Personality: {{user.CorePersonality}}
        """.Trim();
    }

    private static IReadOnlyList<StoryImageEntitySelection> NormalizeEntitySelections(ChatStory story, IReadOnlyList<StoryImageEntitySelection> entities)
    {
        var normalized = new List<StoryImageEntitySelection>();
        foreach (var entity in entities.Distinct())
        {
            EnsureEntityExists(story, entity.EntityKind, entity.EntityId);
            normalized.Add(entity);
        }

        return normalized;
    }

    private static void EnsureEntityExists(ChatStory story, StoryEntityKind entityKind, Guid entityId)
    {
        var exists = entityKind switch
        {
            StoryEntityKind.Character => story.Characters.Entries.Any(x => !x.IsArchived && x.Id == entityId),
            StoryEntityKind.Location => story.Locations.Entries.Any(x => !x.IsArchived && x.Id == entityId),
            StoryEntityKind.Item => story.Items.Entries.Any(x => !x.IsArchived && x.Id == entityId),
            _ => false
        };
        if (!exists)
            throw new InvalidOperationException("The selected story entity could not be found.");
    }

    private static Guid? ResolvePrimaryImageId(ChatStory story, StoryEntityKind entityKind, Guid entityId) =>
        entityKind switch
        {
            StoryEntityKind.Character => story.Characters.Entries.FirstOrDefault(x => x.Id == entityId)?.PrimaryImageId,
            StoryEntityKind.Location => story.Locations.Entries.FirstOrDefault(x => x.Id == entityId)?.PrimaryImageId,
            StoryEntityKind.Item => story.Items.Entries.FirstOrDefault(x => x.Id == entityId)?.PrimaryImageId,
            _ => null
        };

    private static void SetPrimaryImage(ChatStory story, StoryEntityKind entityKind, Guid entityId, Guid? imageId)
    {
        if (entityKind == StoryEntityKind.Character)
        {
            story.Characters = new ChatStoryCharactersDocument(story.Characters.Entries
                .Select(x => x.Id == entityId ? x with { PrimaryImageId = imageId } : x)
                .ToList());
        }
        else if (entityKind == StoryEntityKind.Location)
        {
            story.Locations = new ChatStoryLocationsDocument(story.Locations.Entries
                .Select(x => x.Id == entityId ? x with { PrimaryImageId = imageId } : x)
                .ToList());
        }
        else if (entityKind == StoryEntityKind.Item)
        {
            story.Items = new ChatStoryItemsDocument(story.Items.Entries
                .Select(x => x.Id == entityId ? x with { PrimaryImageId = imageId } : x)
                .ToList());
        }
    }

    private static void AddLink(DbAppContext dbContext, Guid threadId, Guid imageId, StoryEntityKind entityKind, Guid entityId, StoryImageLinkPurpose purpose, DateTime now)
    {
        var dataKind = MapEntityKind(entityKind);
        var exists = dbContext.StoryImageLinks.Local.Any(x => x.ThreadId == threadId && x.ImageId == imageId && x.EntityKind == dataKind && x.EntityId == entityId && x.Purpose == purpose);
        if (exists)
            return;

        dbContext.StoryImageLinks.Add(new StoryImageLink
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            ImageId = imageId,
            EntityKind = dataKind,
            EntityId = entityId,
            Purpose = purpose,
            CreatedUtc = now
        });
    }

    private static StoryImageEntityKind MapEntityKind(StoryEntityKind kind) => kind switch
    {
        StoryEntityKind.Character => StoryImageEntityKind.Character,
        StoryEntityKind.Location => StoryImageEntityKind.Location,
        StoryEntityKind.Item => StoryImageEntityKind.Item,
        _ => throw new InvalidOperationException("Story images can only be linked to characters, locations, or items.")
    };

    private static StoryEntityKind MapEntityKind(StoryImageEntityKind kind) => kind switch
    {
        StoryImageEntityKind.Character => StoryEntityKind.Character,
        StoryImageEntityKind.Location => StoryEntityKind.Location,
        StoryImageEntityKind.Item => StoryEntityKind.Item,
        _ => throw new InvalidOperationException("Unsupported story image entity kind.")
    };

    private async Task<IReadOnlyList<ImageReferenceInput>> LoadReferenceImagesAsync(DbAppContext dbContext, Guid threadId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken)
    {
        if (imageIds.Count == 0)
            return [];

        var uniqueIds = imageIds.Distinct().ToList();
        var images = await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId && uniqueIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        return images.Select(x => new ImageReferenceInput(x.Id, x.Bytes, x.ContentType, x.FileName)).ToList();
    }

    private static async Task<Dictionary<Guid, PrimaryImageData>> LoadPrimaryImagesAsync(DbAppContext dbContext, ChatStory story, CancellationToken cancellationToken)
    {
        var ids = story.Characters.Entries.Select(x => x.PrimaryImageId)
            .Concat(story.Locations.Entries.Select(x => x.PrimaryImageId))
            .Concat(story.Items.Entries.Select(x => x.PrimaryImageId))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        return await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(
                x => x.Id,
                x => new PrimaryImageData(StoryImageUrlBuilder.Build(x.Id), BuildCrop(x)),
                cancellationToken);
    }

    private async Task<ImageModelRow> LoadImageModelAsync(DbAppContext dbContext, Guid modelId, CancellationToken cancellationToken)
    {
        var model = await dbContext.AiModels
            .AsNoTracking()
            .Include(x => x.Provider)
            .Where(x => x.Id == modelId && x.IsEnabled && x.IsImageModelEnabled && x.Provider.IsEnabled)
            .Select(x => new ImageModelRow(
                x.Id,
                x.ProviderId,
                x.Provider.Name,
                x.Provider.ProviderKind,
                x.Endpoint ?? x.Provider.BaseEndpoint,
                x.Provider.ApiKey,
                x.DisplayName,
                x.ProviderModelId))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Generating an image failed because the selected image model is not enabled.");

        if (!model.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !model.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Generating an image with '{model.DisplayName}' failed because the endpoint must start with http:// or https://.");

        return model;
    }

    private static StoryImageReferenceView MapImage(
        StoryImageAsset image,
        bool isPrimary,
        IReadOnlyList<StoryImageEntitySelection>? linkedEntities = null) => new(
        image.Id,
        string.IsNullOrWhiteSpace(image.Title) ? image.FileName ?? "Story image" : image.Title,
        image.ContentType,
        StoryImageUrlBuilder.Build(image.Id),
        image.Width,
        image.Height,
        image.SourceKind,
        image.CreatedUtc,
        isPrimary,
        image.UserPrompt,
        image.FinalPrompt,
        image.GenerationRationale,
        BuildCrop(image),
        linkedEntities ?? []);

    private static IReadOnlyList<StoryImageEntitySelection> BuildLinkedEntities(IEnumerable<StoryImageLink> links) =>
        links
            .Select(x => new StoryImageEntitySelection(MapEntityKind(x.EntityKind), x.EntityId))
            .Distinct()
            .ToList();

    private static async Task<HashSet<Guid>> GetSearchMatchingImageIdsAsync(
        DbAppContext dbContext,
        ChatStory story,
        Guid threadId,
        string searchText,
        CancellationToken cancellationToken)
    {
        var matchingEntityIds = story.Characters.Entries
            .Where(x => ContainsSearch(x.Name, searchText))
            .Select(x => (Kind: StoryImageEntityKind.Character, x.Id))
            .Concat(story.Locations.Entries
                .Where(x => ContainsSearch(x.Name, searchText))
                .Select(x => (Kind: StoryImageEntityKind.Location, x.Id)))
            .Concat(story.Items.Entries
                .Where(x => ContainsSearch(x.Name, searchText))
                .Select(x => (Kind: StoryImageEntityKind.Item, x.Id)))
            .ToList();
        if (matchingEntityIds.Count == 0)
            return [];

        var result = new HashSet<Guid>();
        foreach (var group in matchingEntityIds.GroupBy(x => x.Kind))
        {
            var ids = group.Select(x => x.Id).ToList();
            var imageIds = await dbContext.StoryImageLinks
                .AsNoTracking()
                .Where(x => x.ThreadId == threadId && x.EntityKind == group.Key && ids.Contains(x.EntityId))
                .Select(x => x.ImageId)
                .ToListAsync(cancellationToken);
            result.UnionWith(imageIds);
        }

        return result;
    }

    private static bool MatchesImageSearch(StoryImageAsset image, string searchText) =>
        ContainsSearch(image.Title, searchText)
        || ContainsSearch(image.FileName, searchText)
        || ContainsSearch(image.UserPrompt, searchText)
        || ContainsSearch(image.FinalPrompt, searchText);

    private static bool ContainsSearch(string? value, string searchText) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private static void ValidateImageBytes(string contentType, byte[] bytes, string displayName)
    {
        if (!AllowedContentTypes.Contains(contentType))
            throw new InvalidOperationException($"Adding image '{displayName}' failed because only PNG, JPEG, and WebP images are supported.");

        if (bytes.Length == 0)
            throw new InvalidOperationException($"Adding image '{displayName}' failed because the file was empty.");

        if (bytes.Length > MaxImageBytes)
            throw new InvalidOperationException($"Adding image '{displayName}' failed because images must be 10 MB or smaller.");
    }

    private static string BuildImageTitle(string fileName, StoryEntityKind entityKind, ChatStory story, Guid entityId)
    {
        var entityName = ResolveEntityName(story, entityKind, entityId);
        if (!string.IsNullOrWhiteSpace(fileName))
            return $"{entityName} - {Path.GetFileName(fileName)}";

        return entityName;
    }

    private static string BuildGeneratedTitle(IReadOnlyList<StoryImageEntitySelection> entities, ChatStory story)
    {
        if (entities.Count == 0)
            return "Generated story image";

        return string.Join(", ", entities.Take(3).Select(x => ResolveEntityName(story, x.EntityKind, x.EntityId)));
    }

    private static string ResolveEntityName(ChatStory story, StoryEntityKind entityKind, Guid entityId) =>
        entityKind switch
        {
            StoryEntityKind.Character => story.Characters.Entries.First(x => x.Id == entityId).Name,
            StoryEntityKind.Location => story.Locations.Entries.First(x => x.Id == entityId).Name,
            StoryEntityKind.Item => story.Items.Entries.First(x => x.Id == entityId).Name,
            _ => "Story image"
        };

    private static string? GetPrimaryImageUrl(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.ImageUrl : null;

    private static StoryImageAvatarCropView GetPrimaryImageCrop(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.Crop : StoryImageAvatarCropView.Default;

    private static StoryImageAvatarCropView BuildCrop(StoryImageAsset image) =>
        NormalizeCrop(
            image.AvatarFocusXPercent ?? StoryImageAvatarCropView.Default.FocusXPercent,
            image.AvatarFocusYPercent ?? StoryImageAvatarCropView.Default.FocusYPercent,
            image.AvatarZoomPercent ?? StoryImageAvatarCropView.Default.ZoomPercent);

    private static StoryImageAvatarCropView NormalizeCrop(int focusXPercent, int focusYPercent, int zoomPercent) =>
        new(
            Math.Clamp(focusXPercent, 0, 100),
            Math.Clamp(focusYPercent, 0, 100),
            Math.Clamp(zoomPercent, 100, 300));

    private static string BuildProviderDataUrl(string contentType, byte[] bytes) =>
        $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

    private static async Task DeleteTransientImagesAsync(
        DbAppContext dbContext,
        Guid threadId,
        Guid transientSessionId,
        Guid? exceptImageId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.StoryImageAssets
            .Where(x => x.ThreadId == threadId && x.IsTransient && x.TransientSessionId == transientSessionId);
        if (exceptImageId.HasValue)
            query = query.Where(x => x.Id != exceptImageId.Value);

        var images = await query.ToListAsync(cancellationToken);
        dbContext.StoryImageAssets.RemoveRange(images);
    }

    private static string NormalizeOption(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private void PublishRefresh(Guid? threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, occurredUtc));
    }

    private sealed record ImagePromptResponse
    {
        [Description("The final image generation prompt.")]
        public string FinalPrompt { get; init; } = string.Empty;

        [Description("One short sentence explaining how the entity context was incorporated.")]
        public string Rationale { get; init; } = string.Empty;
    }

    private sealed record ComposedImagePrompt(string FinalPrompt, string Rationale);

    private sealed record ImageModelRow(
        Guid ModelId,
        Guid ProviderId,
        string ProviderName,
        AiProviderKind ProviderKind,
        string Endpoint,
        string ApiKey,
        string DisplayName,
        string ProviderModelId);

    private sealed record ImageReferenceInput(Guid ImageId, byte[] Bytes, string ContentType, string? FileName);

    private sealed record ImageGenerationRequest(
        string Prompt,
        string Size,
        string Quality,
        string ReferenceFidelity,
        IReadOnlyList<ImageReferenceInput> References);

    private sealed record GeneratedImage(byte[] Bytes, string ContentType, string? FileName);

    private sealed record PrimaryImageData(string ImageUrl, StoryImageAvatarCropView Crop);

    private interface IImageGenerationClient
    {
        Task<GeneratedImage> GenerateAsync(
            ImageGenerationRequest request,
            IProgress<StoryImageGenerationProgress>? progress,
            CancellationToken cancellationToken);
    }

    private sealed class OpenAiImageGenerationClient(IHttpClientFactory httpClientFactory, ImageModelRow model) : IImageGenerationClient
    {
        public async Task<GeneratedImage> GenerateAsync(
            ImageGenerationRequest request,
            IProgress<StoryImageGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var client = CreateClient(httpClientFactory, model.ApiKey);
            if (SupportsStreaming(model.ProviderModelId))
                return request.References.Count == 0
                    ? await GenerateWithoutReferencesStreamingAsync(client, request, progress, cancellationToken)
                    : await GenerateWithReferencesStreamingAsync(client, request, progress, cancellationToken);

            using HttpResponseMessage response = request.References.Count == 0
                ? await GenerateWithoutReferencesAsync(client, request, cancellationToken)
                : await GenerateWithReferencesAsync(client, request, cancellationToken);
            var json = await ReadJsonAsync(response, $"Generating an OpenAI image with '{model.DisplayName}'", cancellationToken);
            var b64 = json["data"]?.AsArray().FirstOrDefault()?["b64_json"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(b64))
                throw new InvalidOperationException("OpenAI did not return image bytes.");

            return new GeneratedImage(Convert.FromBase64String(b64), "image/png", "openai-image.png");
        }

        private async Task<GeneratedImage> GenerateWithoutReferencesStreamingAsync(
            HttpClient client,
            ImageGenerationRequest request,
            IProgress<StoryImageGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            var body = new
            {
                model = model.ProviderModelId,
                prompt = request.Prompt,
                size = request.Size,
                quality = request.Quality,
                n = 1,
                stream = true,
                partial_images = 2
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(model.Endpoint), "images/generations"))
            {
                Content = JsonContent.Create(body, options: JsonSerializerOptions)
            };
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return await ReadStreamingImageAsync(response, $"Generating an OpenAI image with '{model.DisplayName}'", progress, cancellationToken);
        }

        private async Task<HttpResponseMessage> GenerateWithoutReferencesAsync(HttpClient client, ImageGenerationRequest request, CancellationToken cancellationToken)
        {
            var body = new
            {
                model = model.ProviderModelId,
                prompt = request.Prompt,
                size = request.Size,
                quality = request.Quality,
                n = 1
            };
            return await client.PostAsJsonAsync(new Uri(new Uri(model.Endpoint), "images/generations"), body, JsonSerializerOptions, cancellationToken);
        }

        private async Task<HttpResponseMessage> GenerateWithReferencesAsync(HttpClient client, ImageGenerationRequest request, CancellationToken cancellationToken)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(model.ProviderModelId), "model" },
                { new StringContent(request.Prompt), "prompt" },
                { new StringContent(request.Size), "size" },
                { new StringContent(request.Quality), "quality" },
                { new StringContent(request.ReferenceFidelity), "input_fidelity" }
            };

            foreach (var reference in request.References.Take(16))
            {
                var content = new ByteArrayContent(reference.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
                form.Add(content, "image[]", reference.FileName ?? $"{reference.ImageId}.png");
            }

            return await client.PostAsync(new Uri(new Uri(model.Endpoint), "images/edits"), form, cancellationToken);
        }

        private async Task<GeneratedImage> GenerateWithReferencesStreamingAsync(
            HttpClient client,
            ImageGenerationRequest request,
            IProgress<StoryImageGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(model.ProviderModelId), "model" },
                { new StringContent(request.Prompt), "prompt" },
                { new StringContent(request.Size), "size" },
                { new StringContent(request.Quality), "quality" },
                { new StringContent(request.ReferenceFidelity), "input_fidelity" },
                { new StringContent("true"), "stream" },
                { new StringContent("2"), "partial_images" }
            };

            foreach (var reference in request.References.Take(16))
            {
                var content = new ByteArrayContent(reference.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
                form.Add(content, "image[]", reference.FileName ?? $"{reference.ImageId}.png");
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(model.Endpoint), "images/edits"))
            {
                Content = form
            };
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return await ReadStreamingImageAsync(response, $"Generating an OpenAI image with '{model.DisplayName}'", progress, cancellationToken);
        }

        private static bool SupportsStreaming(string providerModelId) =>
            providerModelId.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase)
            || providerModelId.StartsWith("chatgpt-image-", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GrokImageGenerationClient(IHttpClientFactory httpClientFactory, ImageModelRow model) : IImageGenerationClient
    {
        public async Task<GeneratedImage> GenerateAsync(
            ImageGenerationRequest request,
            IProgress<StoryImageGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var client = CreateClient(httpClientFactory, model.ApiKey);
            var body = new JsonObject
            {
                ["model"] = model.ProviderModelId,
                ["prompt"] = request.Prompt
            };
            if (!string.IsNullOrWhiteSpace(request.Size))
                body["size"] = request.Size;
            if (request.References.Count > 0)
                body["image_url"] = BuildProviderDataUrl(request.References[0].ContentType, request.References[0].Bytes);

            using var response = await client.PostAsJsonAsync(new Uri(new Uri(model.Endpoint), "images/generations"), body, JsonSerializerOptions, cancellationToken);
            var json = await ReadJsonAsync(response, $"Generating a Grok image with '{model.DisplayName}'", cancellationToken);
            var data = json["data"]?.AsArray().FirstOrDefault();
            var b64 = data?["b64_json"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(b64))
                return new GeneratedImage(Convert.FromBase64String(b64), "image/png", "grok-image.png");

            var url = data?["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Grok did not return an image URL or image bytes.");

            using var imageResponse = await client.GetAsync(url, cancellationToken);
            if (!imageResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Downloading the generated Grok image failed because the image endpoint returned {(int)imageResponse.StatusCode} ({imageResponse.StatusCode}).");

            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "image/png";
            return new GeneratedImage(bytes, contentType, "grok-image.png");
        }
    }

    private static HttpClient CreateClient(IHttpClientFactory httpClientFactory, string apiKey)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return client;
    }

    private static async Task<GeneratedImage> ReadStreamingImageAsync(
        HttpResponseMessage response,
        string operation,
        IProgress<StoryImageGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalServiceFailureException(
                UserFacingErrorMessageBuilder.BuildExternalHttpFailure(operation, response.StatusCode, body),
                response.StatusCode,
                body);
        }

        string? finalImageBase64 = null;
        var partialImageCount = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                continue;

            var json = JsonNode.Parse(data);
            var b64 = ExtractStreamingImageBase64(json);
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            finalImageBase64 = b64;
            var partialImageIndex = GetStreamingPartialImageIndex(json) ?? partialImageCount;
            progress?.Report(new StoryImageGenerationProgress(
                $"Received image preview {partialImageIndex + 1}.",
                $"data:image/png;base64,{b64}",
                partialImageIndex,
                null,
                null));
            partialImageCount++;
        }

        if (string.IsNullOrWhiteSpace(finalImageBase64))
            throw new InvalidOperationException("OpenAI did not return image bytes.");

        return new GeneratedImage(Convert.FromBase64String(finalImageBase64), "image/png", "openai-image.png");
    }

    private static string? ExtractStreamingImageBase64(JsonNode? json) =>
        json?["b64_json"]?.GetValue<string>()
        ?? json?["partial_image_b64"]?.GetValue<string>()
        ?? json?["data"]?.AsArray().FirstOrDefault()?["b64_json"]?.GetValue<string>();

    private static int? GetStreamingPartialImageIndex(JsonNode? json)
    {
        var value = json?["partial_image_index"];
        return value is null ? null : value.GetValue<int>();
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<JsonNode>(JsonSerializerOptions, cancellationToken)
                ?? new JsonObject();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ExternalServiceFailureException(
            UserFacingErrorMessageBuilder.BuildExternalHttpFailure(operation, response.StatusCode, body),
            response.StatusCode,
            body);
    }

}
