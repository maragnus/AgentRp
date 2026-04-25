using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Services;

public sealed class StoryImageOptimizationService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IActivityNotifier activityNotifier,
    ILogger<StoryImageOptimizationService> logger) : IStoryImageOptimizationService
{
    private const string SettingsKey = "story-image-optimization-settings";
    private const int MaxAttemptCount = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private static readonly Uri ShrinkEndpoint = new("https://api.tinify.com/shrink");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task<StoryImageOptimizationSettingsView> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        return await BuildSettingsViewAsync(dbContext, Deserialize(setting), setting?.UpdatedUtc, cancellationToken);
    }

    public async Task<StoryImageOptimizationSettingsView> SaveSettingsAsync(SaveStoryImageOptimizationSettings settings, CancellationToken cancellationToken)
    {
        var normalized = new StoryImageOptimizationSettingsDocument(settings.IsEnabled, settings.ApiKey.Trim());
        if (normalized.IsEnabled && string.IsNullOrWhiteSpace(normalized.ApiKey))
            throw new InvalidOperationException("Saving Tinify settings failed because the API key was empty.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        var previous = Deserialize(setting);
        if (setting is null)
        {
            setting = new AppSetting { Key = SettingsKey, JsonValue = string.Empty, UpdatedUtc = DateTime.UtcNow };
            dbContext.AppSettings.Add(setting);
        }

        setting.JsonValue = ChatStoryJson.Serialize(normalized);
        setting.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (IsConfigured(normalized) && (!IsConfigured(previous) || !string.Equals(previous.ApiKey, normalized.ApiKey, StringComparison.Ordinal)))
            await QueuePrimaryImagesAsync(dbContext, cancellationToken);

        return await BuildSettingsViewAsync(dbContext, normalized, setting.UpdatedUtc, cancellationToken);
    }

    public async Task QueueImageAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsConfiguredAsync(dbContext, cancellationToken))
            return;

        var now = DateTime.UtcNow;
        var image = await dbContext.StoryImageAssets.FirstOrDefaultAsync(
            x => x.Id == imageId && !x.IsTransient && x.OptimizedUtc == null,
            cancellationToken);
        if (image is null)
            return;

        QueueImage(image, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task QueuePrimaryImagesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsConfiguredAsync(dbContext, cancellationToken))
            return;

        await QueuePrimaryImagesAsync(dbContext, cancellationToken);
    }

    public async Task<int> ProcessQueuedImagesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
        if (!IsConfigured(settings))
            return 0;

        var now = DateTime.UtcNow;
        var retryBeforeUtc = now.Subtract(RetryDelay);
        var imageIds = await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => !x.IsTransient
                && x.OptimizedUtc == null
                && x.OptimizationQueuedUtc != null
                && x.OptimizationAttemptCount < MaxAttemptCount
                && (x.OptimizationAttemptCount == 0 || x.OptimizationLastAttemptUtc <= retryBeforeUtc))
            .OrderBy(x => x.OptimizationQueuedUtc)
            .Select(x => x.Id)
            .Take(5)
            .ToListAsync(cancellationToken);

        var processedCount = 0;
        foreach (var imageId in imageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProcessImageAsync(dbContext, settings.ApiKey, imageId, cancellationToken))
                processedCount++;
        }

        return processedCount;
    }

    private async Task<bool> ProcessImageAsync(DbAppContext dbContext, string apiKey, Guid imageId, CancellationToken cancellationToken)
    {
        var image = await dbContext.StoryImageAssets.FirstOrDefaultAsync(x => x.Id == imageId, cancellationToken);
        if (image is null || image.IsTransient || image.OptimizedUtc.HasValue || image.OptimizationAttemptCount >= MaxAttemptCount)
            return false;

        image.OptimizationAttemptCount++;
        image.OptimizationLastAttemptUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var optimized = await OptimizeWithTinifyAsync(apiKey, image.Bytes, image.ContentType, cancellationToken);
            var dimensions = StoryImageDimensions.TryRead(optimized.Bytes, optimized.ContentType);
            image.Bytes = optimized.Bytes;
            image.ContentType = optimized.ContentType;
            image.FileName = BuildOptimizedFileName(image);
            image.Width = dimensions?.Width ?? image.Width;
            image.Height = dimensions?.Height ?? image.Height;
            image.OptimizedUtc = DateTime.UtcNow;
            image.OptimizationLastError = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            PublishRefresh(image.ThreadId);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tinify optimization failed for story image {ImageId}.", image.Id);
            image.OptimizationLastError = UserFacingErrorMessageBuilder.Build($"Optimizing image '{BuildDisplayTitle(image)}' with Tinify failed.", exception);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    private async Task<OptimizedImage> OptimizeWithTinifyAsync(string apiKey, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));

        using var uploadContent = new ByteArrayContent(bytes);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var uploadResponse = await client.PostAsync(ShrinkEndpoint, uploadContent, cancellationToken);
        await EnsureSuccessAsync(uploadResponse, "Uploading an image to Tinify", cancellationToken);

        var outputLocation = uploadResponse.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(outputLocation))
            throw new InvalidOperationException("Uploading an image to Tinify failed because Tinify did not return an output URL.");

        if (!outputLocation.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !outputLocation.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Uploading an image to Tinify failed because Tinify returned an invalid output URL.");

        var convertRequest = new { convert = new { type = "image/webp" } };
        using var convertResponse = await client.PostAsJsonAsync(new Uri(outputLocation), convertRequest, JsonSerializerOptions, cancellationToken);
        await EnsureSuccessAsync(convertResponse, "Converting an image to WebP with Tinify", cancellationToken);

        var optimizedBytes = await convertResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var optimizedContentType = convertResponse.Content.Headers.ContentType?.MediaType ?? "image/webp";
        return new OptimizedImage(optimizedBytes, optimizedContentType);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ExternalServiceFailureException(
            UserFacingErrorMessageBuilder.BuildExternalHttpFailure(operation, response.StatusCode, body, "Tinify"),
            response.StatusCode,
            body);
    }

    private async Task QueuePrimaryImagesAsync(DbAppContext dbContext, CancellationToken cancellationToken)
    {
        var primaryImageIds = await LoadPrimaryImageIdsAsync(dbContext, cancellationToken);
        if (primaryImageIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var images = await dbContext.StoryImageAssets
            .Where(x => primaryImageIds.Contains(x.Id) && !x.IsTransient && x.OptimizedUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var image in images)
            QueueImage(image, now);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void QueueImage(StoryImageAsset image, DateTime now)
    {
        image.OptimizationQueuedUtc ??= now;
        image.OptimizationLastError = null;
    }

    private static async Task<HashSet<Guid>> LoadPrimaryImageIdsAsync(DbAppContext dbContext, CancellationToken cancellationToken)
    {
        var stories = await dbContext.ChatStories.AsNoTracking().ToListAsync(cancellationToken);
        return stories
            .SelectMany(x => x.Characters.Entries.Select(y => y.PrimaryImageId)
                .Concat(x.Locations.Entries.Select(y => y.PrimaryImageId))
                .Concat(x.Items.Entries.Select(y => y.PrimaryImageId)))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
    }

    private static async Task<StoryImageOptimizationSettingsView> BuildSettingsViewAsync(
        DbAppContext dbContext,
        StoryImageOptimizationSettingsDocument settings,
        DateTime? updatedUtc,
        CancellationToken cancellationToken)
    {
        var queuedCount = await dbContext.StoryImageAssets.CountAsync(x => x.OptimizationQueuedUtc != null && x.OptimizedUtc == null && x.OptimizationAttemptCount < MaxAttemptCount, cancellationToken);
        var optimizedCount = await dbContext.StoryImageAssets.CountAsync(x => x.OptimizedUtc != null, cancellationToken);
        var failedCount = await dbContext.StoryImageAssets.CountAsync(x => x.OptimizationQueuedUtc != null && x.OptimizedUtc == null && x.OptimizationAttemptCount >= MaxAttemptCount, cancellationToken);
        return new StoryImageOptimizationSettingsView(
            settings.IsEnabled,
            !string.IsNullOrWhiteSpace(settings.ApiKey),
            settings.ApiKey,
            updatedUtc,
            queuedCount,
            optimizedCount,
            failedCount);
    }

    private async Task<bool> IsConfiguredAsync(DbAppContext dbContext, CancellationToken cancellationToken) =>
        IsConfigured(await LoadSettingsAsync(dbContext, cancellationToken));

    private static async Task<StoryImageOptimizationSettingsDocument> LoadSettingsAsync(DbAppContext dbContext, CancellationToken cancellationToken)
    {
        var setting = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        return Deserialize(setting);
    }

    private static StoryImageOptimizationSettingsDocument Deserialize(AppSetting? setting) =>
        ChatStoryJson.Deserialize(setting?.JsonValue, StoryImageOptimizationSettingsDocument.Default);

    private static bool IsConfigured(StoryImageOptimizationSettingsDocument settings) =>
        settings.IsEnabled && !string.IsNullOrWhiteSpace(settings.ApiKey);

    private static string BuildOptimizedFileName(StoryImageAsset image)
    {
        var stem = string.IsNullOrWhiteSpace(image.FileName)
            ? image.Id.ToString("N")
            : Path.GetFileNameWithoutExtension(image.FileName);
        return $"{stem}.webp";
    }

    private static string BuildDisplayTitle(StoryImageAsset image) =>
        string.IsNullOrWhiteSpace(image.Title) ? image.FileName ?? image.Id.ToString("N") : image.Title;

    private void PublishRefresh(Guid threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, occurredUtc));
    }

    private sealed record StoryImageOptimizationSettingsDocument(bool IsEnabled, string ApiKey)
    {
        public static StoryImageOptimizationSettingsDocument Default { get; } = new(false, string.Empty);
    }

    private sealed record OptimizedImage(byte[] Bytes, string ContentType);
}
