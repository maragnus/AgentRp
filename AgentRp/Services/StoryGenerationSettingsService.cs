using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed record StoryGenerationSettingsView(
    double PlannerTemperature,
    double ProseTemperature);

public sealed record UpdateStoryGenerationSettings(
    double PlannerTemperature,
    double ProseTemperature);

public interface IStoryGenerationSettingsService
{
    Task<StoryGenerationSettingsView> GetSettingsAsync(CancellationToken cancellationToken);

    Task<StoryGenerationSettingsView> UpdateSettingsAsync(UpdateStoryGenerationSettings update, CancellationToken cancellationToken);
}

public sealed class StoryGenerationSettingsService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier) : IStoryGenerationSettingsService
{
    private const string SettingsKey = "story-generation-settings";
    private const double DefaultPlannerTemperature = 0.4;
    private const double DefaultProseTemperature = 0.9;
    private const double MinimumTemperature = 0;
    private const double MaximumTemperature = 2;
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoryGenerationSettingsView> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);
        return Deserialize(settings.JsonValue);
    }

    public async Task<StoryGenerationSettingsView> UpdateSettingsAsync(UpdateStoryGenerationSettings update, CancellationToken cancellationToken)
    {
        var normalized = Normalize(update);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);

        settings.JsonValue = Serialize(normalized);
        settings.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        PublishRefresh();
        return normalized;
    }

    private static StoryGenerationSettingsView Normalize(UpdateStoryGenerationSettings update)
    {
        var plannerTemperature = NormalizeTemperature(update.PlannerTemperature, "planner temperature");
        var proseTemperature = NormalizeTemperature(update.ProseTemperature, "writing temperature");
        return new StoryGenerationSettingsView(plannerTemperature, proseTemperature);
    }

    private static double NormalizeTemperature(double temperature, string label)
    {
        if (!double.IsFinite(temperature))
            throw new InvalidOperationException($"Saving AI settings failed because the {label} was not a valid number.");

        if (temperature is < MinimumTemperature or > MaximumTemperature)
            throw new InvalidOperationException($"Saving AI settings failed because the {label} must be between 0 and 2.");

        return Math.Round(temperature, 1, MidpointRounding.AwayFromZero);
    }

    private static StoryGenerationSettingsView Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CreateDefaultSettings();

        try
        {
            var settings = JsonSerializer.Deserialize<StoryGenerationSettingsView>(json, JsonSerializerOptions);
            if (settings is null)
                return CreateDefaultSettings();

            return Normalize(new UpdateStoryGenerationSettings(settings.PlannerTemperature, settings.ProseTemperature));
        }
        catch (JsonException)
        {
            return CreateDefaultSettings();
        }
        catch (InvalidOperationException)
        {
            return CreateDefaultSettings();
        }
    }

    private static string Serialize(StoryGenerationSettingsView settings) => JsonSerializer.Serialize(settings, JsonSerializerOptions);

    private static StoryGenerationSettingsView CreateDefaultSettings() => new(DefaultPlannerTemperature, DefaultProseTemperature);

    private static async Task<AppSetting> GetOrCreateSettingsAsync(AgentRp.Data.AppContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new AppSetting
        {
            Key = SettingsKey,
            JsonValue = Serialize(CreateDefaultSettings()),
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.AppSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private void PublishRefresh()
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, null, occurredUtc));
    }
}
