using System.Globalization;
using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public enum StoryGenerationStage
{
    Planning = 1,
    Writing = 2
}

public sealed record StoryModelStageSettingsView(
    double Temperature,
    double? TopP,
    int? MaxOutputTokens,
    long? Seed,
    double? FrequencyPenalty,
    double? PresencePenalty,
    IReadOnlyList<string> StopSequences);

public sealed record StoryGenerationSettingsView(
    StoryModelStageSettingsView Planning,
    StoryModelStageSettingsView Writing);

public sealed record UpdateStoryGenerationSettings(
    StoryModelStageSettingsView Planning,
    StoryModelStageSettingsView Writing);

public sealed record StoryGenerationSettingsStageTransferPackage(
    int SchemaVersion,
    DateTime ExportedUtc,
    StoryGenerationStage Stage,
    StoryModelStageSettingsView Settings);

public interface IStoryGenerationSettingsService
{
    Task<StoryGenerationSettingsView> GetSettingsAsync(CancellationToken cancellationToken);

    Task<StoryGenerationSettingsView> UpdateSettingsAsync(UpdateStoryGenerationSettings update, CancellationToken cancellationToken);

    StoryModelStageSettingsView NormalizeStageSettings(StoryGenerationStage stage, StoryModelStageSettingsView settings);

    string SerializeStageTransferPackage(StoryGenerationStage stage, StoryModelStageSettingsView settings);

    StoryModelStageSettingsView DeserializeStageTransferPackage(string json, StoryGenerationStage expectedStage);

    string BuildStageExportFileName(StoryGenerationStage stage, DateTime exportedUtc);
}

public sealed class StoryGenerationSettingsService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier) : IStoryGenerationSettingsService
{
    private const string SettingsKey = "story-generation-settings";
    private const int CurrentStageTransferSchemaVersion = 1;
    private const double DefaultPlannerTemperature = 0.4;
    private const double DefaultWritingTemperature = 0.9;
    private const double MinimumTemperature = 0;
    private const double MaximumTemperature = 2;
    private const double MinimumProbability = 0;
    private const double MaximumProbability = 1;
    private const double MinimumPenalty = -2;
    private const double MaximumPenalty = 2;
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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

    public StoryModelStageSettingsView NormalizeStageSettings(StoryGenerationStage stage, StoryModelStageSettingsView settings) =>
        NormalizeStage(settings, stage);

    public string SerializeStageTransferPackage(StoryGenerationStage stage, StoryModelStageSettingsView settings)
    {
        var normalized = NormalizeStage(settings, stage);
        var package = new StoryGenerationSettingsStageTransferPackage(
            CurrentStageTransferSchemaVersion,
            DateTime.UtcNow,
            stage,
            normalized);
        return JsonSerializer.Serialize(package, JsonSerializerOptions);
    }

    public StoryModelStageSettingsView DeserializeStageTransferPackage(string json, StoryGenerationStage expectedStage)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"Importing {DescribeStage(expectedStage)} model settings failed because the selected file was empty.");

        StoryGenerationSettingsStageTransferPackage package;

        try
        {
            package = JsonSerializer.Deserialize<StoryGenerationSettingsStageTransferPackage>(json, JsonSerializerOptions)
                ?? throw new InvalidOperationException($"Importing {DescribeStage(expectedStage)} model settings failed because the file did not contain a model settings package.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Importing {DescribeStage(expectedStage)} model settings failed because the file was not valid JSON.", exception);
        }

        if (package.SchemaVersion != CurrentStageTransferSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Importing {DescribeStage(expectedStage)} model settings failed because schema version {package.SchemaVersion} is not supported.");
        }

        if (package.Stage != expectedStage)
        {
            throw new InvalidOperationException(
                $"Importing {DescribeStage(expectedStage)} model settings failed because the file contains {DescribeStage(package.Stage)} settings.");
        }

        if (package.Settings is null)
            throw new InvalidOperationException($"Importing {DescribeStage(expectedStage)} model settings failed because the file did not contain stage settings.");

        return NormalizeStage(package.Settings, expectedStage);
    }

    public string BuildStageExportFileName(StoryGenerationStage stage, DateTime exportedUtc)
    {
        var dateStamp = exportedUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stageSlug = stage.ToString().ToLowerInvariant();
        return $"story-model-settings-{stageSlug}-{dateStamp}.json";
    }

    private static StoryGenerationSettingsView Normalize(UpdateStoryGenerationSettings update) => new(
        NormalizeStage(update.Planning, StoryGenerationStage.Planning),
        NormalizeStage(update.Writing, StoryGenerationStage.Writing));

    private static StoryModelStageSettingsView NormalizeStage(StoryModelStageSettingsView settings, StoryGenerationStage stage)
    {
        var stageLabel = DescribeStage(stage);
        var normalizedStops = NormalizeStopSequences(settings.StopSequences, stageLabel);

        return new StoryModelStageSettingsView(
            NormalizeTemperature(settings.Temperature, stageLabel),
            NormalizeTopP(settings.TopP, stageLabel),
            NormalizeMaxOutputTokens(settings.MaxOutputTokens, stageLabel),
            NormalizeSeed(settings.Seed, stageLabel),
            NormalizePenalty(settings.FrequencyPenalty, stageLabel, "frequency penalty"),
            NormalizePenalty(settings.PresencePenalty, stageLabel, "presence penalty"),
            normalizedStops);
    }

    private static double NormalizeTemperature(double temperature, string stageLabel)
    {
        if (!double.IsFinite(temperature))
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because the temperature was not a valid number.");

        if (temperature is < MinimumTemperature or > MaximumTemperature)
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because the temperature must be between 0 and 2.");

        return Math.Round(temperature, 1, MidpointRounding.AwayFromZero);
    }

    private static double? NormalizeTopP(double? topP, string stageLabel)
    {
        if (!topP.HasValue)
            return null;

        if (!double.IsFinite(topP.Value))
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because Top P was not a valid number.");

        if (topP.Value is < MinimumProbability or > MaximumProbability)
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because Top P must be between 0 and 1.");

        return Math.Round(topP.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static int? NormalizeMaxOutputTokens(int? maxOutputTokens, string stageLabel)
    {
        if (!maxOutputTokens.HasValue)
            return null;

        if (maxOutputTokens.Value <= 0)
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because max output tokens must be greater than 0.");

        return maxOutputTokens.Value;
    }

    private static long? NormalizeSeed(long? seed, string stageLabel)
    {
        if (!seed.HasValue)
            return null;

        if (seed.Value < 0)
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because the seed must be 0 or greater.");

        return seed.Value;
    }

    private static double? NormalizePenalty(double? penalty, string stageLabel, string label)
    {
        if (!penalty.HasValue)
            return null;

        if (!double.IsFinite(penalty.Value))
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because the {label} was not a valid number.");

        if (penalty.Value is < MinimumPenalty or > MaximumPenalty)
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because the {label} must be between -2 and 2.");

        return Math.Round(penalty.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static IReadOnlyList<string> NormalizeStopSequences(IReadOnlyList<string>? stopSequences, string stageLabel)
    {
        if (stopSequences is null || stopSequences.Count == 0)
            return [];

        var normalized = stopSequences
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Any(x => x is null))
            throw new InvalidOperationException($"Saving {stageLabel} model settings failed because a stop sequence was invalid.");

        return normalized.Cast<string>().ToList();
    }

    private static StoryGenerationSettingsView Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CreateDefaultSettings();

        try
        {
            var document = JsonSerializer.Deserialize<StoryGenerationSettingsDocument>(json, JsonSerializerOptions);
            if (document?.Planning is not null && document.Writing is not null)
            {
                return Normalize(new UpdateStoryGenerationSettings(
                    MapStageDocument(document.Planning, StoryGenerationStage.Planning),
                    MapStageDocument(document.Writing, StoryGenerationStage.Writing)));
            }

            var legacy = JsonSerializer.Deserialize<LegacyStoryGenerationSettingsDocument>(json, JsonSerializerOptions);
            if (legacy is not null)
            {
                return Normalize(new UpdateStoryGenerationSettings(
                    CreateDefaultStageSettings(StoryGenerationStage.Planning) with { Temperature = legacy.PlannerTemperature ?? DefaultPlannerTemperature },
                    CreateDefaultStageSettings(StoryGenerationStage.Writing) with { Temperature = legacy.ProseTemperature ?? DefaultWritingTemperature }));
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return CreateDefaultSettings();
    }

    private static StoryModelStageSettingsView MapStageDocument(StoryModelStageSettingsDocument document, StoryGenerationStage stage) => new(
        document.Temperature ?? GetDefaultTemperature(stage),
        document.TopP,
        document.MaxOutputTokens,
        document.Seed,
        document.FrequencyPenalty,
        document.PresencePenalty,
        document.StopSequences ?? []);

    private static string Serialize(StoryGenerationSettingsView settings) => JsonSerializer.Serialize(settings, JsonSerializerOptions);

    private static StoryGenerationSettingsView CreateDefaultSettings() => new(
        CreateDefaultStageSettings(StoryGenerationStage.Planning),
        CreateDefaultStageSettings(StoryGenerationStage.Writing));

    private static StoryModelStageSettingsView CreateDefaultStageSettings(StoryGenerationStage stage) => new(
        GetDefaultTemperature(stage),
        null,
        null,
        null,
        null,
        null,
        []);

    private static double GetDefaultTemperature(StoryGenerationStage stage) => stage switch
    {
        StoryGenerationStage.Planning => DefaultPlannerTemperature,
        StoryGenerationStage.Writing => DefaultWritingTemperature,
        _ => throw new InvalidOperationException($"Creating default settings failed because the stage '{stage}' is not supported.")
    };

    private static string DescribeStage(StoryGenerationStage stage) => stage switch
    {
        StoryGenerationStage.Planning => "Planning",
        StoryGenerationStage.Writing => "Writing",
        _ => stage.ToString()
    };

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

    private sealed record StoryGenerationSettingsDocument(
        StoryModelStageSettingsDocument? Planning,
        StoryModelStageSettingsDocument? Writing);

    private sealed record StoryModelStageSettingsDocument(
        double? Temperature,
        double? TopP,
        int? MaxOutputTokens,
        long? Seed,
        double? FrequencyPenalty,
        double? PresencePenalty,
        IReadOnlyList<string>? StopSequences);

    private sealed record LegacyStoryGenerationSettingsDocument(
        double? PlannerTemperature,
        double? ProseTemperature);
}
