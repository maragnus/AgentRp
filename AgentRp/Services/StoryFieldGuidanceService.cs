using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed class StoryFieldGuidanceService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory) : IStoryFieldGuidanceService
{
    private const string SettingsKey = "story-field-guidance";
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StoryEntityFieldGuidanceView>> GetGuidanceAsync(StoryEntityKind entityKind, CancellationToken cancellationToken)
    {
        var document = await GetOrCreateDocumentAsync(cancellationToken);
        return StoryFieldGuidanceRegistry.GetFields(entityKind)
            .Select(field => CreateView(entityKind, field, document))
            .ToList();
    }

    public async Task<StoryEntityFieldGuidanceView> GetGuidanceAsync(StoryEntityKind entityKind, StoryEntityFieldKey fieldKey, CancellationToken cancellationToken)
    {
        var document = await GetOrCreateDocumentAsync(cancellationToken);
        return CreateView(entityKind, StoryFieldGuidanceRegistry.GetField(entityKind, fieldKey), document);
    }

    public async Task<StoryEntityFieldGuidanceView> UpdateGuidanceAsync(UpdateStoryEntityFieldGuidance update, CancellationToken cancellationToken)
    {
        ValidateGuidance(update.Guidance);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);
        var document = Deserialize(settings.JsonValue);

        var field = StoryFieldGuidanceRegistry.GetField(update.EntityKind, update.FieldKey);
        document.Upsert(update.EntityKind, update.FieldKey, update.Guidance);

        settings.JsonValue = Serialize(document);
        settings.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new StoryEntityFieldGuidanceView(update.EntityKind, field.FieldKey, field.Label, update.Guidance);
    }

    private async Task<StoryFieldGuidanceDocument> GetOrCreateDocumentAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateSettingsAsync(dbContext, cancellationToken);
        return Deserialize(settings.JsonValue);
    }

    private static StoryEntityFieldGuidanceView CreateView(
        StoryEntityKind entityKind,
        StoryFieldDefinition field,
        StoryFieldGuidanceDocument document)
    {
        var guidance = document.Get(entityKind, field.FieldKey) ?? field.DefaultGuidance;
        return new StoryEntityFieldGuidanceView(entityKind, field.FieldKey, field.Label, guidance);
    }

    private static void ValidateGuidance(StoryFieldGuidance guidance)
    {
        if (guidance.SuggestedLength.Minimum <= 0 || guidance.SuggestedLength.Maximum <= 0)
            throw new InvalidOperationException("Saving field guidance failed because the suggested length must be greater than zero.");

        if (guidance.SuggestedLength.Minimum > guidance.SuggestedLength.Maximum)
            throw new InvalidOperationException("Saving field guidance failed because the suggested minimum length exceeded the maximum.");
    }

    private static StoryFieldGuidanceDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SeedDefaults();

        try
        {
            var document = JsonSerializer.Deserialize<StoryFieldGuidanceDocument>(json, JsonSerializerOptions);
            return document ?? SeedDefaults();
        }
        catch (JsonException)
        {
            return SeedDefaults();
        }
    }

    private static string Serialize(StoryFieldGuidanceDocument document) => JsonSerializer.Serialize(document, JsonSerializerOptions);

    private static StoryFieldGuidanceDocument SeedDefaults()
    {
        var sections = Enum.GetValues<StoryEntityKind>()
            .Select(entityKind => new StoryFieldGuidanceSection(
                entityKind,
                StoryFieldGuidanceRegistry.GetFields(entityKind)
                    .Select(field => new StoryFieldGuidanceEntry(field.FieldKey, field.DefaultGuidance))
                    .ToList()))
            .ToList();

        return new StoryFieldGuidanceDocument(sections);
    }

    private static async Task<AgentRp.Data.AppSetting> GetOrCreateSettingsAsync(AgentRp.Data.AppContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new AgentRp.Data.AppSetting
        {
            Key = SettingsKey,
            JsonValue = Serialize(SeedDefaults()),
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.AppSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private sealed class StoryFieldGuidanceDocument
    {
        public StoryFieldGuidanceDocument(IReadOnlyList<StoryFieldGuidanceSection> sections)
        {
            Sections = sections;
        }

        public IReadOnlyList<StoryFieldGuidanceSection> Sections { get; private set; }

        public StoryFieldGuidance? Get(StoryEntityKind entityKind, StoryEntityFieldKey fieldKey) =>
            Sections.FirstOrDefault(x => x.EntityKind == entityKind)?.Entries.FirstOrDefault(x => x.FieldKey == fieldKey)?.Guidance;

        public void Upsert(StoryEntityKind entityKind, StoryEntityFieldKey fieldKey, StoryFieldGuidance guidance)
        {
            var sections = Sections.ToList();
            var sectionIndex = sections.FindIndex(x => x.EntityKind == entityKind);
            if (sectionIndex < 0)
            {
                sections.Add(new StoryFieldGuidanceSection(entityKind, [new StoryFieldGuidanceEntry(fieldKey, guidance)]));
                Sections = sections;
                return;
            }

            var entries = sections[sectionIndex].Entries.ToList();
            var entryIndex = entries.FindIndex(x => x.FieldKey == fieldKey);
            if (entryIndex >= 0)
                entries[entryIndex] = new StoryFieldGuidanceEntry(fieldKey, guidance);
            else
                entries.Add(new StoryFieldGuidanceEntry(fieldKey, guidance));

            sections[sectionIndex] = sections[sectionIndex] with { Entries = entries };
            Sections = sections;
        }
    }

    private sealed record StoryFieldGuidanceSection(
        StoryEntityKind EntityKind,
        IReadOnlyList<StoryFieldGuidanceEntry> Entries);

    private sealed record StoryFieldGuidanceEntry(
        StoryEntityFieldKey FieldKey,
        StoryFieldGuidance Guidance);
}

public sealed record StoryFieldDefinition(
    StoryEntityKind EntityKind,
    StoryEntityFieldKey FieldKey,
    string Label,
    StoryFieldGuidance DefaultGuidance);

public static class StoryFieldGuidanceRegistry
{
    private static readonly IReadOnlyList<StoryFieldDefinition> Fields =
    [
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.Name,
            "Name",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 4),
                "Ava Thorn")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.Summary,
            "Summary",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 1),
                "Sharp-tongued courier hiding a reckless streak.")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.GeneralAppearance,
            "General Appearance",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 2),
                "Tall, broad-shouldered, and usually read as older than her years, with dark hair and a weathered expression.")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.CorePersonality,
            "Core Personality",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(2, 4),
                "Quietly observant, stubborn under pressure, and kinder than she first appears.")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.Relationships,
            "Relationships",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.BulletList,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(2, 5),
                "- Mira: trusted partner\n- Captain Vale: mutual suspicion")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.PreferencesBeliefs,
            "Preferences / Likes / Beliefs",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.BulletList,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(3, 6),
                "- Hates wasted motion\n- Believes loyalty must be earned")),
        new(
            StoryEntityKind.Character,
            StoryEntityFieldKey.PrivateMotivations,
            "Private Motivations",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Paragraphs,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(1, 2),
                "She masks her tenderness with sarcasm because wanting people too badly has always made her feel weak. What she cannot admit is that she wants one person in particular to choose her first, and every cruel remark is a preemptive strike against being left behind again.")),
        new(
            StoryEntityKind.Location,
            StoryEntityFieldKey.Name,
            "Name",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 5),
                "Glass Harbor")),
        new(
            StoryEntityKind.Location,
            StoryEntityFieldKey.Summary,
            "Summary",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 2),
                "Fog-lashed trade district built on swaying piers and mirrored towers.")),
        new(
            StoryEntityKind.Location,
            StoryEntityFieldKey.Details,
            "Details",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Paragraphs,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(1, 3),
                "Salt-stained boardwalks, chained lanterns, hidden smugglers' lifts, and watchful customs clerks.")),
        new(
            StoryEntityKind.Item,
            StoryEntityFieldKey.Name,
            "Name",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 4),
                "Star-etched compass")),
        new(
            StoryEntityKind.Item,
            StoryEntityFieldKey.Summary,
            "Summary",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 2),
                "A navigational relic that points toward concealed doors instead of north.")),
        new(
            StoryEntityKind.Item,
            StoryEntityFieldKey.Details,
            "Details",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Paragraphs,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(1, 2),
                "Silver casing, cracked crystal face, and a needle that jerks when secrets are nearby.")),
        new(
            StoryEntityKind.HistoryFact,
            StoryEntityFieldKey.Title,
            "Title",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(2, 6),
                "The Harbor Fire Pact")),
        new(
            StoryEntityKind.HistoryFact,
            StoryEntityFieldKey.Summary,
            "Summary",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 2),
                "The ruling guilds promised mutual aid after the eastern docks burned twenty years ago.")),
        new(
            StoryEntityKind.HistoryFact,
            StoryEntityFieldKey.Details,
            "Details",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Paragraphs,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(1, 3),
                "The pact is publicly celebrated, but it also grants quiet leverage to the few families who financed the rebuild.")),
        new(
            StoryEntityKind.TimelineEntry,
            StoryEntityFieldKey.WhenText,
            "When",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 6),
                "Three winters ago")),
        new(
            StoryEntityKind.TimelineEntry,
            StoryEntityFieldKey.Title,
            "Title",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Words,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(2, 7),
                "Ava steals the compass")),
        new(
            StoryEntityKind.TimelineEntry,
            StoryEntityFieldKey.Summary,
            "Summary",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Sentences,
                StoryFieldGuidanceDetailLevel.Simple,
                new StoryFieldGuidanceLength(1, 2),
                "Ava takes the relic from a sealed customs vault and vanishes into the harbor crowds.")),
        new(
            StoryEntityKind.TimelineEntry,
            StoryEntityFieldKey.Details,
            "Details",
            new StoryFieldGuidance(
                StoryFieldGuidanceFormat.Paragraphs,
                StoryFieldGuidanceDetailLevel.Medium,
                new StoryFieldGuidanceLength(1, 3),
                "The theft sparks a manhunt, fractures an alliance, and puts the relic into the story's main conflict."))
    ];

    public static IReadOnlyList<StoryFieldDefinition> GetFields(StoryEntityKind entityKind) =>
        Fields.Where(x => x.EntityKind == entityKind).ToList();

    public static StoryFieldDefinition GetField(StoryEntityKind entityKind, StoryEntityFieldKey fieldKey) =>
        Fields.FirstOrDefault(x => x.EntityKind == entityKind && x.FieldKey == fieldKey)
        ?? throw new InvalidOperationException($"No editable field guidance definition exists for {entityKind}.{fieldKey}.");
}
