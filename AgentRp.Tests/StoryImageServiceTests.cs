using AgentRp.Data;
using AgentRp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Tests;

public sealed class StoryImageServiceTests
{
    [Fact]
    public async Task UploadAsync_CreatesAssetLinkAndPrimaryImage()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var service = CreateService(factory);

        var image = await service.UploadAsync(
            new UploadStoryImage(threadId, StoryEntityKind.Character, characterId, "ava.png", "image/png", Png1x1, true),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var asset = await dbContext.StoryImageAssets.SingleAsync();
        var link = await dbContext.StoryImageLinks.SingleAsync();
        var story = await dbContext.ChatStories.SingleAsync();

        Assert.Equal(asset.Id, image.ImageId);
        Assert.Equal($"/story-images/{asset.Id}", image.ImageUrl);
        Assert.Equal(threadId, asset.ThreadId);
        Assert.False(asset.IsTransient);
        Assert.Equal(asset.Id, link.ImageId);
        Assert.Equal(StoryImageEntityKind.Character, link.EntityKind);
        Assert.Equal(characterId, link.EntityId);
        Assert.Equal(asset.Id, story.Characters.Entries.Single().PrimaryImageId);
    }

    [Fact]
    public async Task GetGalleryAsync_ReturnsOnlyImagesForSelectedEntity()
    {
        var factory = CreateDbFactory();
        var (threadId, firstCharacterId) = await SeedCharacterAsync(factory, "Ava");
        var secondCharacterId = Guid.NewGuid();
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            var story = await dbContext.ChatStories.SingleAsync();
            story.Characters = new ChatStoryCharactersDocument(story.Characters.Entries
                .Append(new StoryCharacterDocument(secondCharacterId, "Bea", StoryCharacterUserSheetDocument.Empty, StoryCharacterModelSheetDocument.Empty, 1, null, false))
                .ToList());
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService(factory);
        await service.UploadAsync(new UploadStoryImage(threadId, StoryEntityKind.Character, firstCharacterId, "ava.png", "image/png", Png1x1, false), CancellationToken.None);
        await service.UploadAsync(new UploadStoryImage(threadId, StoryEntityKind.Character, secondCharacterId, "bea.png", "image/png", Png1x1, false), CancellationToken.None);

        var gallery = await service.GetGalleryAsync(threadId, StoryEntityKind.Character, firstCharacterId, CancellationToken.None);

        Assert.Single(gallery.Images);
        Assert.Contains("ava.png", gallery.Images.Single().Title);
    }

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("Nyx", "NY")]
    [InlineData("", "?")]
    public void StoryAvatarInitials_BuildsExpectedFallback(string name, string expected)
    {
        Assert.Equal(expected, StoryAvatarInitials.Build(name));
    }

    [Fact]
    public async Task GetCatalogAsync_ReturnsCurrentChatImagesMostRecentFirstAndLimited()
    {
        var factory = CreateDbFactory();
        var (threadId, _) = await SeedCharacterAsync(factory, "Ava");
        var (otherThreadId, _) = await SeedCharacterAsync(factory, "Bea");
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 21; index++)
            {
                dbContext.StoryImageAssets.Add(new StoryImageAsset
                {
                    Id = Guid.NewGuid(),
                    ThreadId = threadId,
                    Bytes = Png1x1,
                    ContentType = "image/png",
                    Title = $"Image {index:00}",
                    SourceKind = StoryImageSourceKind.Uploaded,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(index)
                });
            }

            dbContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = Guid.NewGuid(),
                ThreadId = otherThreadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Other chat image",
                SourceKind = StoryImageSourceKind.Uploaded,
                CreatedUtc = DateTime.UtcNow.AddHours(1)
            });
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService(factory);
        var catalog = await service.GetCatalogAsync(threadId, null, 20, CancellationToken.None);

        Assert.Equal(20, catalog.Images.Count);
        Assert.DoesNotContain(catalog.Images, x => x.Title == "Other chat image");
        Assert.Equal("Image 20", catalog.Images.First().Title);
        Assert.Equal("Image 01", catalog.Images.Last().Title);
    }

    [Fact]
    public async Task GetCatalogAsync_SearchesPromptAndLinkedEntityNames()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava Star");
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            var promptImageId = Guid.NewGuid();
            dbContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = promptImageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Generated",
                UserPrompt = "blue crystal crown",
                SourceKind = StoryImageSourceKind.Generated,
                CreatedUtc = DateTime.UtcNow
            });

            var entityImageId = Guid.NewGuid();
            dbContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = entityImageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Portrait",
                SourceKind = StoryImageSourceKind.Uploaded,
                CreatedUtc = DateTime.UtcNow.AddMinutes(1)
            });
            dbContext.StoryImageLinks.Add(new StoryImageLink
            {
                Id = Guid.NewGuid(),
                ThreadId = threadId,
                ImageId = entityImageId,
                EntityKind = StoryImageEntityKind.Character,
                EntityId = characterId,
                Purpose = StoryImageLinkPurpose.Gallery,
                CreatedUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService(factory);
        var promptCatalog = await service.GetCatalogAsync(threadId, "crystal", 20, CancellationToken.None);
        var entityCatalog = await service.GetCatalogAsync(threadId, "star", 20, CancellationToken.None);

        Assert.Single(promptCatalog.Images);
        Assert.Equal("Generated", promptCatalog.Images.Single().Title);
        Assert.Single(entityCatalog.Images);
        Assert.Equal("Portrait", entityCatalog.Images.Single().Title);
    }

    [Fact]
    public async Task SetPrimaryImageAsync_LinksCatalogImageToEntity()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var imageId = Guid.NewGuid();
        await using (var seedContext = await factory.CreateDbContextAsync())
        {
            seedContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = imageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Catalog image",
                SourceKind = StoryImageSourceKind.Uploaded,
                CreatedUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var service = CreateService(factory);
        await service.SetPrimaryImageAsync(new SetStoryEntityPrimaryImage(threadId, StoryEntityKind.Character, characterId, imageId), CancellationToken.None);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var story = await verifyContext.ChatStories.SingleAsync();
        var link = await verifyContext.StoryImageLinks.SingleAsync();
        Assert.Equal(imageId, story.Characters.Entries.Single().PrimaryImageId);
        Assert.Equal(imageId, link.ImageId);
        Assert.Equal(characterId, link.EntityId);
    }

    [Fact]
    public async Task UpdateCropAsync_PersistsCropOnImageAsset()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var service = CreateService(factory);
        var image = await service.UploadAsync(
            new UploadStoryImage(threadId, StoryEntityKind.Character, characterId, "ava.png", "image/png", Png1x1, true),
            CancellationToken.None);

        var saved = await service.UpdateCropAsync(new UpdateStoryImageCrop(threadId, image.ImageId, 25, 75, 150), CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var asset = await dbContext.StoryImageAssets.SingleAsync();
        Assert.Equal(new StoryImageAvatarCropView(25, 75, 150), saved.AvatarCrop);
        Assert.Equal(25, asset.AvatarFocusXPercent);
        Assert.Equal(75, asset.AvatarFocusYPercent);
        Assert.Equal(150, asset.AvatarZoomPercent);
    }

    [Fact]
    public async Task UpdateCropAsync_RejectsImageFromAnotherThread()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var (otherThreadId, otherCharacterId) = await SeedCharacterAsync(factory, "Bea");
        var service = CreateService(factory);
        var image = await service.UploadAsync(
            new UploadStoryImage(otherThreadId, StoryEntityKind.Character, otherCharacterId, "bea.png", "image/png", Png1x1, true),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateCropAsync(new UpdateStoryImageCrop(threadId, image.ImageId, 25, 75, 150), CancellationToken.None));

        Assert.Contains("could not be found in this chat", exception.Message);
    }

    [Fact]
    public async Task ImageViews_ReturnSavedCropAndCenteredDefault()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var service = CreateService(
            factory,
            agentCatalog: new TestAgentCatalog(new AgentProviderOptionView(Guid.NewGuid(), Guid.NewGuid(), "GPT Image", "OpenAI", AiProviderKind.OpenAI)));
        var image = await service.UploadAsync(
            new UploadStoryImage(threadId, StoryEntityKind.Character, characterId, "ava.png", "image/png", Png1x1, true),
            CancellationToken.None);

        Assert.Equal(StoryImageAvatarCropView.Default, image.AvatarCrop);

        await service.UpdateCropAsync(new UpdateStoryImageCrop(threadId, image.ImageId, 15, 35, 125), CancellationToken.None);
        var loaded = await service.GetImageAsync(threadId, image.ImageId, CancellationToken.None);
        var catalog = await service.GetCatalogAsync(threadId, null, 20, CancellationToken.None);
        var dialog = await service.GetGenerationDialogAsync(threadId, CancellationToken.None);

        Assert.Equal(new StoryImageAvatarCropView(15, 35, 125), loaded!.AvatarCrop);
        Assert.Equal(new StoryImageAvatarCropView(15, 35, 125), catalog.Images.Single().AvatarCrop);
        Assert.Equal(new StoryImageAvatarCropView(15, 35, 125), dialog.Entities.Single().PrimaryImageCrop);
    }

    [Fact]
    public async Task SaveGeneratedImageAsync_PersistsSelectedCandidateAndSetsPrimary()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var transientSessionId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        await using (var generatedContext = await factory.CreateDbContextAsync())
        {
            generatedContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = imageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                FileName = "generated-image.png",
                Title = "Generated story image",
                SourceKind = StoryImageSourceKind.Generated,
                UserPrompt = "portrait",
                FinalPrompt = "final portrait prompt",
                GenerationRationale = "Used selected entity.",
                IsTransient = true,
                TransientSessionId = transientSessionId,
                TransientExpiresUtc = DateTime.UtcNow.AddHours(1),
                CreatedUtc = DateTime.UtcNow
            });
            await generatedContext.SaveChangesAsync();
        }

        var service = CreateService(factory);

        var image = await service.SaveGeneratedImageAsync(
            new SaveGeneratedStoryImage(
                threadId,
                imageId,
                transientSessionId,
                [new StoryImageEntitySelection(StoryEntityKind.Character, characterId)],
                true),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var asset = await dbContext.StoryImageAssets.SingleAsync();
        var link = await dbContext.StoryImageLinks.SingleAsync();
        var story = await dbContext.ChatStories.SingleAsync();

        Assert.Equal(asset.Id, image.ImageId);
        Assert.Equal(threadId, asset.ThreadId);
        Assert.Equal("portrait", asset.UserPrompt);
        Assert.False(asset.IsTransient);
        Assert.Null(asset.TransientSessionId);
        Assert.Null(asset.TransientExpiresUtc);
        Assert.Equal(asset.Id, link.ImageId);
        Assert.Equal(asset.Id, story.Characters.Entries.Single().PrimaryImageId);
    }

    [Fact]
    public async Task GeneratePreviewAsync_CreatesTransientImage()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var modelId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            var provider = new AiProvider
            {
                Id = providerId,
                Name = "OpenAI",
                ProviderKind = AiProviderKind.OpenAI,
                BaseEndpoint = "https://api.openai.test/",
                ApiKey = "test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            dbContext.AiProviders.Add(provider);
            dbContext.AiModels.Add(new AiModel
            {
                Id = modelId,
                ProviderId = providerId,
                Provider = provider,
                ProviderModelId = "gpt-image-1",
                DisplayName = "GPT Image",
                Endpoint = "https://api.openai.test/v1/",
                IsEnabled = true,
                IsImageModelEnabled = true,
                PlanningSettingsJson = "{}",
                WritingSettingsJson = "{}",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService(
            factory,
            new StaticHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                data: {"type":"image_generation.partial_image","b64_json":"{{Convert.ToBase64String(Png1x1)}}","partial_image_index":0}

                data: [DONE]

                """)
            }),
            new TestAgentCatalog(new AgentProviderOptionView(modelId, providerId, "GPT Image", "OpenAI", AiProviderKind.OpenAI)),
            new NullThreadAgentService());
        var transientSessionId = Guid.NewGuid();

        var preview = await service.GeneratePreviewAsync(
            new GenerateStoryImagePreview(
                threadId,
                transientSessionId,
                "portrait",
                null,
                [new StoryImageEntitySelection(StoryEntityKind.Character, characterId)],
                [],
                "1024x1024",
                "auto",
                "low"),
            null,
            CancellationToken.None);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var asset = await verifyContext.StoryImageAssets.SingleAsync();
        Assert.Equal(asset.Id, preview.ImageId);
        Assert.Equal($"/story-images/{asset.Id}", preview.ImageUrl);
        Assert.Equal(Png1x1, asset.Bytes);
        Assert.True(asset.IsTransient);
        Assert.Equal(transientSessionId, asset.TransientSessionId);
        Assert.NotNull(asset.TransientExpiresUtc);
        Assert.Empty(verifyContext.StoryImageLinks);
    }

    [Fact]
    public async Task SaveGeneratedImageAsync_PromotesSelectedImageAndDeletesOtherSessionPreviews()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var transientSessionId = Guid.NewGuid();
        var selectedImageId = Guid.NewGuid();
        var discardedImageId = Guid.NewGuid();
        await SeedTransientImageAsync(factory, threadId, transientSessionId, selectedImageId, "Selected");
        await SeedTransientImageAsync(factory, threadId, transientSessionId, discardedImageId, "Discarded");
        var service = CreateService(factory);

        await service.SaveGeneratedImageAsync(
            new SaveGeneratedStoryImage(
                threadId,
                selectedImageId,
                transientSessionId,
                [new StoryImageEntitySelection(StoryEntityKind.Character, characterId)],
                true),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var image = await dbContext.StoryImageAssets.SingleAsync();
        Assert.Equal(selectedImageId, image.Id);
        Assert.False(image.IsTransient);
        Assert.Null(image.TransientSessionId);
    }

    [Fact]
    public async Task DiscardTransientImagesAsync_RemovesOnlyMatchingThreadSessionImages()
    {
        var factory = CreateDbFactory();
        var (threadId, _) = await SeedCharacterAsync(factory, "Ava");
        var (otherThreadId, _) = await SeedCharacterAsync(factory, "Bea");
        var transientSessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        var matchingImageId = Guid.NewGuid();
        var otherSessionImageId = Guid.NewGuid();
        var otherThreadImageId = Guid.NewGuid();
        await SeedTransientImageAsync(factory, threadId, transientSessionId, matchingImageId, "Matching");
        await SeedTransientImageAsync(factory, threadId, otherSessionId, otherSessionImageId, "Other session");
        await SeedTransientImageAsync(factory, otherThreadId, transientSessionId, otherThreadImageId, "Other thread");
        var service = CreateService(factory);

        await service.DiscardTransientImagesAsync(threadId, transientSessionId, CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var remainingIds = await dbContext.StoryImageAssets.Select(x => x.Id).ToListAsync();
        Assert.DoesNotContain(matchingImageId, remainingIds);
        Assert.Contains(otherSessionImageId, remainingIds);
        Assert.Contains(otherThreadImageId, remainingIds);
    }

    [Fact]
    public async Task SavingTinifySettings_QueuesExistingPrimaryImages()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var imageId = Guid.NewGuid();
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            dbContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = imageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Primary",
                SourceKind = StoryImageSourceKind.Uploaded,
                CreatedUtc = DateTime.UtcNow
            });
            var story = await dbContext.ChatStories.SingleAsync();
            story.Characters = new ChatStoryCharactersDocument(story.Characters.Entries
                .Select(x => x.Id == characterId ? x with { PrimaryImageId = imageId } : x)
                .ToList());
            await dbContext.SaveChangesAsync();
        }

        var optimization = CreateOptimizationService(factory);
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var image = await verifyContext.StoryImageAssets.SingleAsync();
        Assert.NotNull(image.OptimizationQueuedUtc);
    }

    [Fact]
    public async Task UploadAsync_QueuesImageOnlyWhenTinifyIsConfigured()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var optimization = CreateOptimizationService(factory);
        var service = CreateService(factory, imageOptimizationService: optimization);

        var unqueued = await service.UploadAsync(
            new UploadStoryImage(threadId, StoryEntityKind.Character, characterId, "before.png", "image/png", Png1x1, false),
            CancellationToken.None);
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);
        var queued = await service.UploadAsync(
            new UploadStoryImage(threadId, StoryEntityKind.Character, characterId, "after.png", "image/png", Png1x1, false),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var before = await dbContext.StoryImageAssets.SingleAsync(x => x.Id == unqueued.ImageId);
        var after = await dbContext.StoryImageAssets.SingleAsync(x => x.Id == queued.ImageId);
        Assert.Null(before.OptimizationQueuedUtc);
        Assert.NotNull(after.OptimizationQueuedUtc);
    }

    [Fact]
    public async Task SaveGeneratedImageAsync_QueuesPromotedImageOnly()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var optimization = CreateOptimizationService(factory);
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);
        var transientSessionId = Guid.NewGuid();
        var selectedImageId = Guid.NewGuid();
        var discardedImageId = Guid.NewGuid();
        await SeedTransientImageAsync(factory, threadId, transientSessionId, selectedImageId, "Selected");
        await SeedTransientImageAsync(factory, threadId, transientSessionId, discardedImageId, "Discarded");
        var service = CreateService(factory, imageOptimizationService: optimization);

        await service.SaveGeneratedImageAsync(
            new SaveGeneratedStoryImage(
                threadId,
                selectedImageId,
                transientSessionId,
                [new StoryImageEntitySelection(StoryEntityKind.Character, characterId)],
                true),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var image = await dbContext.StoryImageAssets.SingleAsync();
        Assert.Equal(selectedImageId, image.Id);
        Assert.NotNull(image.OptimizationQueuedUtc);
    }

    [Fact]
    public async Task SetPrimaryImageAsync_QueuesSelectedImage()
    {
        var factory = CreateDbFactory();
        var (threadId, characterId) = await SeedCharacterAsync(factory, "Ava");
        var imageId = Guid.NewGuid();
        await using (var seedContext = await factory.CreateDbContextAsync())
        {
            seedContext.StoryImageAssets.Add(new StoryImageAsset
            {
                Id = imageId,
                ThreadId = threadId,
                Bytes = Png1x1,
                ContentType = "image/png",
                Title = "Catalog image",
                SourceKind = StoryImageSourceKind.Uploaded,
                CreatedUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var optimization = CreateOptimizationService(factory);
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);
        var service = CreateService(factory, imageOptimizationService: optimization);
        await service.SetPrimaryImageAsync(new SetStoryEntityPrimaryImage(threadId, StoryEntityKind.Character, characterId, imageId), CancellationToken.None);

        await using var verifyContext = await factory.CreateDbContextAsync();
        var image = await verifyContext.StoryImageAssets.SingleAsync();
        Assert.NotNull(image.OptimizationQueuedUtc);
    }

    [Fact]
    public async Task ProcessQueuedImagesAsync_ReplacesImageWithWebp()
    {
        var factory = CreateDbFactory();
        var (threadId, _) = await SeedCharacterAsync(factory, "Ava");
        var imageId = await SeedQueuedImageAsync(factory, threadId);
        var optimization = CreateOptimizationService(
            factory,
            new SequenceHttpClientFactory(
                CreateTinifyUploadResponse(),
                CreateTinifyConvertResponse(Webp1x1)));
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);

        var processed = await optimization.ProcessQueuedImagesAsync(CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var image = await dbContext.StoryImageAssets.SingleAsync(x => x.Id == imageId);
        Assert.Equal(1, processed);
        Assert.Equal(Webp1x1, image.Bytes);
        Assert.Equal("image/webp", image.ContentType);
        Assert.Equal("queued.webp", image.FileName);
        Assert.Equal(1, image.OptimizationAttemptCount);
        Assert.NotNull(image.OptimizedUtc);
        Assert.Null(image.OptimizationLastError);
    }

    [Fact]
    public async Task ProcessQueuedImagesAsync_RetriesFailedImageOnlyOnce()
    {
        var factory = CreateDbFactory();
        var (threadId, _) = await SeedCharacterAsync(factory, "Ava");
        var imageId = await SeedQueuedImageAsync(factory, threadId);
        var optimization = CreateOptimizationService(
            factory,
            new SequenceHttpClientFactory(
                CreateTinifyFailureResponse(),
                CreateTinifyFailureResponse(),
                CreateTinifyUploadResponse(),
                CreateTinifyConvertResponse(Webp1x1)));
        await optimization.SaveSettingsAsync(new SaveStoryImageOptimizationSettings(true, "tinify-key"), CancellationToken.None);

        await optimization.ProcessQueuedImagesAsync(CancellationToken.None);
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            var image = await dbContext.StoryImageAssets.SingleAsync(x => x.Id == imageId);
            Assert.Equal(1, image.OptimizationAttemptCount);
            Assert.NotNull(image.OptimizationLastError);
            image.OptimizationLastAttemptUtc = DateTime.UtcNow.AddMinutes(-16);
            await dbContext.SaveChangesAsync();
        }

        await optimization.ProcessQueuedImagesAsync(CancellationToken.None);
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            var image = await dbContext.StoryImageAssets.SingleAsync(x => x.Id == imageId);
            Assert.Equal(2, image.OptimizationAttemptCount);
            Assert.Null(image.OptimizedUtc);
            image.OptimizationLastAttemptUtc = DateTime.UtcNow.AddMinutes(-16);
            await dbContext.SaveChangesAsync();
        }

        await optimization.ProcessQueuedImagesAsync(CancellationToken.None);
        await using var verifyContext = await factory.CreateDbContextAsync();
        var finalImage = await verifyContext.StoryImageAssets.SingleAsync(x => x.Id == imageId);
        Assert.Equal(2, finalImage.OptimizationAttemptCount);
        Assert.Null(finalImage.OptimizedUtc);
    }

    private static StoryImageService CreateService(
        TestDbContextFactory factory,
        IHttpClientFactory? httpClientFactory = null,
        IAgentCatalog? agentCatalog = null,
        IThreadAgentService? threadAgentService = null,
        IStoryImageOptimizationService? imageOptimizationService = null) => new(
        factory,
        httpClientFactory ?? new EmptyHttpClientFactory(),
        agentCatalog!,
        threadAgentService!,
        imageOptimizationService ?? new StoryImageOptimizationService(
            factory,
            httpClientFactory ?? new EmptyHttpClientFactory(),
            new ActivityNotifier(),
            NullLogger<StoryImageOptimizationService>.Instance),
        new ActivityNotifier(),
        NullLogger<StoryImageService>.Instance);

    private static StoryImageOptimizationService CreateOptimizationService(
        TestDbContextFactory factory,
        IHttpClientFactory? httpClientFactory = null) => new(
        factory,
        httpClientFactory ?? new EmptyHttpClientFactory(),
        new ActivityNotifier(),
        NullLogger<StoryImageOptimizationService>.Instance);

    private static async Task<(Guid ThreadId, Guid CharacterId)> SeedCharacterAsync(TestDbContextFactory factory, string name)
    {
        var threadId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        await using var dbContext = await factory.CreateDbContextAsync();
        var thread = new ChatThread
        {
            Id = threadId,
            Title = "Story",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        dbContext.ChatThreads.Add(thread);
        dbContext.ChatStories.Add(new ChatStory
        {
            ChatThreadId = threadId,
            Thread = thread,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Characters = new ChatStoryCharactersDocument([
                new StoryCharacterDocument(characterId, name, StoryCharacterUserSheetDocument.Empty, StoryCharacterModelSheetDocument.Empty, 1, null, false)
            ])
        });
        await dbContext.SaveChangesAsync();
        return (threadId, characterId);
    }

    private static async Task SeedTransientImageAsync(TestDbContextFactory factory, Guid threadId, Guid transientSessionId, Guid imageId, string title)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        dbContext.StoryImageAssets.Add(new StoryImageAsset
        {
            Id = imageId,
            ThreadId = threadId,
            Bytes = Png1x1,
            ContentType = "image/png",
            Title = title,
            SourceKind = StoryImageSourceKind.Generated,
            IsTransient = true,
            TransientSessionId = transientSessionId,
            TransientExpiresUtc = DateTime.UtcNow.AddHours(1),
            CreatedUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedQueuedImageAsync(TestDbContextFactory factory, Guid threadId)
    {
        var imageId = Guid.NewGuid();
        await using var dbContext = await factory.CreateDbContextAsync();
        dbContext.StoryImageAssets.Add(new StoryImageAsset
        {
            Id = imageId,
            ThreadId = threadId,
            Bytes = Png1x1,
            ContentType = "image/png",
            FileName = "queued.png",
            Title = "Queued image",
            SourceKind = StoryImageSourceKind.Uploaded,
            OptimizationQueuedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync();
        return imageId;
    }

    private static TestDbContextFactory CreateDbFactory() =>
        new(new DbContextOptionsBuilder<DbAppContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    private static readonly byte[] Webp1x1 =
    [
        0x52, 0x49, 0x46, 0x46, 0x16, 0x00, 0x00, 0x00,
        0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x58,
        0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static HttpResponseMessage CreateTinifyUploadResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}")
        };
        response.Headers.Location = new Uri("https://api.tinify.com/output/test");
        return response;
    }

    private static HttpResponseMessage CreateTinifyConvertResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
        return response;
    }

    private static HttpResponseMessage CreateTinifyFailureResponse() => new(HttpStatusCode.Unauthorized)
    {
        Content = new StringContent("""{"error":"Unauthorized","message":"Credentials are invalid"}""")
    };

    private sealed class TestDbContextFactory(DbContextOptions<DbAppContext> options) : IDbContextFactory<DbAppContext>
    {
        public DbAppContext CreateDbContext() => new(options);

        public Task<DbAppContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StaticHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHandler(response));
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = response.Content,
                ReasonPhrase = response.ReasonPhrase
            });
    }

    private sealed class SequenceHttpClientFactory(params HttpResponseMessage[] responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SequenceResponseHandler(responses));
    }

    private sealed class SequenceResponseHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(_index, responses.Count - 1)];
            _index++;
            var clone = new HttpResponseMessage(response.StatusCode)
            {
                Content = response.Content,
                ReasonPhrase = response.ReasonPhrase
            };
            if (response.Headers.Location is not null)
                clone.Headers.Location = response.Headers.Location;

            return Task.FromResult(clone);
        }
    }

    private sealed class TestAgentCatalog(AgentProviderOptionView imageModel) : IAgentCatalog
    {
        public bool HasEnabledAgents => false;

        public IReadOnlyList<AgentProviderOptionView> GetEnabledAgents() => [];

        public IReadOnlyList<AgentProviderOptionView> GetEnabledImageModels() => [imageModel];

        public IReadOnlyList<ConfiguredAgent> GetConfiguredAgents() => [];

        public Guid? GetDefaultModelId() => null;

        public string? GetDefaultAgentName() => null;

        public Guid? NormalizeSelectedModelId(Guid? selectedModelId) => selectedModelId;

        public string? NormalizeSelectedAgentName(string? selectedAgentName) => selectedAgentName;

        public ConfiguredAgent? GetAgentOrDefault(Guid? selectedModelId) => null;

        public ConfiguredAgent? GetAgentOrDefault(string? selectedAgentName) => null;
    }

    private sealed class NullThreadAgentService : IThreadAgentService
    {
        public Task<ThreadAgentSelectionView> GetSelectionAsync(Guid threadId, CancellationToken cancellationToken) =>
            Task.FromResult(new ThreadAgentSelectionView(null, null, [], false));

        public Task<ConfiguredAgent?> GetSelectedAgentAsync(Guid threadId, CancellationToken cancellationToken) =>
            Task.FromResult<ConfiguredAgent?>(null);

        public Task SetSelectedAgentAsync(Guid threadId, Guid modelId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetSelectedAgentAsync(Guid threadId, string agentName, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
