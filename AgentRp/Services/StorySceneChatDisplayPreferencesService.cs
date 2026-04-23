using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed record StorySceneChatDisplayPreferencesView(
    bool ShowProcessPanels);

public sealed record UpdateStorySceneChatDisplayPreferences(
    bool ShowProcessPanels);

public interface IStorySceneChatDisplayPreferencesService
{
    Task<StorySceneChatDisplayPreferencesView> GetPreferencesAsync(CancellationToken cancellationToken);

    Task<StorySceneChatDisplayPreferencesView> UpdatePreferencesAsync(UpdateStorySceneChatDisplayPreferences update, CancellationToken cancellationToken);
}

public sealed class StorySceneChatDisplayPreferencesService(
    IDbContextFactory<AppContext> dbContextFactory) : IStorySceneChatDisplayPreferencesService
{
    private const string SettingsKey = "story-scene-chat-display-preferences";
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly StorySceneChatDisplayPreferencesView DefaultPreferences = new(true);

    public async Task<StorySceneChatDisplayPreferencesView> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);
        return Deserialize(settings.JsonValue);
    }

    public async Task<StorySceneChatDisplayPreferencesView> UpdatePreferencesAsync(
        UpdateStorySceneChatDisplayPreferences update,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(update);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);
        settings.JsonValue = Serialize(normalized);
        settings.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return normalized;
    }

    private static StorySceneChatDisplayPreferencesView Normalize(UpdateStorySceneChatDisplayPreferences update) =>
        new(update.ShowProcessPanels);

    private static StorySceneChatDisplayPreferencesView Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DefaultPreferences;

        try
        {
            return JsonSerializer.Deserialize<StorySceneChatDisplayPreferencesView>(json, JsonSerializerOptions) ?? DefaultPreferences;
        }
        catch (JsonException)
        {
            return DefaultPreferences;
        }
    }

    private static string Serialize(StorySceneChatDisplayPreferencesView preferences) =>
        JsonSerializer.Serialize(preferences, JsonSerializerOptions);

    private static async Task<AppSetting> GetOrCreateSettingsAsync(AppContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new AppSetting
        {
            Key = SettingsKey,
            JsonValue = Serialize(DefaultPreferences),
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.AppSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }
}
