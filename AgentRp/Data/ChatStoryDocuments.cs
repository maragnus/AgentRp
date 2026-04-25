using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRp.Data;

public sealed class ChatStory
{
    public Guid ChatThreadId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public string SceneJson { get; set; } = ChatStoryJson.Serialize(ChatStorySceneDocument.Empty);

    public string CharactersJson { get; set; } = ChatStoryJson.Serialize(ChatStoryCharactersDocument.Empty);

    public string LocationsJson { get; set; } = ChatStoryJson.Serialize(ChatStoryLocationsDocument.Empty);

    public string ItemsJson { get; set; } = ChatStoryJson.Serialize(ChatStoryItemsDocument.Empty);

    public string HistoryJson { get; set; } = ChatStoryJson.Serialize(ChatStoryHistoryDocument.Empty);

    public string StoryContextJson { get; set; } = ChatStoryJson.Serialize(ChatStoryContextDocument.Empty);

    public ChatThread Thread { get; set; } = null!;

    [NotMapped]
    public ChatStorySceneDocument Scene
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(SceneJson, ChatStorySceneDocument.Empty));
        set => SceneJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }

    [NotMapped]
    public ChatStoryCharactersDocument Characters
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(CharactersJson, ChatStoryCharactersDocument.Empty));
        set => CharactersJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }

    [NotMapped]
    public ChatStoryLocationsDocument Locations
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(LocationsJson, ChatStoryLocationsDocument.Empty));
        set => LocationsJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }

    [NotMapped]
    public ChatStoryItemsDocument Items
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(ItemsJson, ChatStoryItemsDocument.Empty));
        set => ItemsJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }

    [NotMapped]
    public ChatStoryHistoryDocument History
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(HistoryJson, ChatStoryHistoryDocument.Empty));
        set => HistoryJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }

    [NotMapped]
    public ChatStoryContextDocument StoryContext
    {
        get => StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(StoryContextJson, ChatStoryContextDocument.Empty));
        set => StoryContextJson = ChatStoryJson.Serialize(StoryDocumentNormalizer.Normalize(value));
    }
}

public enum StoryContentIntensity
{
    Forbidden,
    Allowed,
    Encouraged
}

public sealed record ChatStorySceneDocument(
    Guid? CurrentLocationId,
    IReadOnlyList<Guid> PresentCharacterIds,
    IReadOnlyList<Guid> PresentItemIds,
    string? DerivedContextSummary,
    string? ManualContextNotes)
{
    public static ChatStorySceneDocument Empty { get; } = new(
        null,
        [],
        [],
        null,
        null);
}

public sealed record ChatStoryCharactersDocument(IReadOnlyList<StoryCharacterDocument> Entries)
{
    public static ChatStoryCharactersDocument Empty { get; } = new([]);
}

public sealed record ChatStoryLocationsDocument(IReadOnlyList<StoryLocationDocument> Entries)
{
    public static ChatStoryLocationsDocument Empty { get; } = new([]);
}

public sealed record ChatStoryItemsDocument(IReadOnlyList<StoryItemDocument> Entries)
{
    public static ChatStoryItemsDocument Empty { get; } = new([]);
}

public sealed record ChatStoryHistoryDocument(
    IReadOnlyList<StoryHistoryFactDocument> Facts,
    IReadOnlyList<StoryTimelineEntryDocument> TimelineEntries)
{
    public static ChatStoryHistoryDocument Empty { get; } = new([], []);
}

public sealed record ChatStoryContextDocument(
    string Genre,
    string Setting,
    string Tone,
    string StoryDirection,
    StoryContentIntensity ExplicitContent,
    StoryContentIntensity ViolentContent)
{
    public static ChatStoryContextDocument Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        StoryContentIntensity.Allowed,
        StoryContentIntensity.Allowed);
}

public sealed record StoryCharacterUserSheetDocument(
    string Summary,
    string GeneralAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    string PrivateMotivations)
{
    public static StoryCharacterUserSheetDocument Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}

public sealed record StoryCharacterModelSheetDocument(
    string Summary,
    string Appearance,
    string Voice,
    string Hides,
    string Tendency,
    string Constraint,
    string Relationships,
    string LikesBeliefs,
    string PrivateMotivations)
{
    public static StoryCharacterModelSheetDocument Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}

public sealed record StoryCharacterDocument(
    Guid Id,
    string Name,
    StoryCharacterUserSheetDocument? UserSheet,
    StoryCharacterModelSheetDocument? ModelSheet,
    int UserSheetRevision,
    int? ModelSheetReviewedAgainstRevision,
    bool IsArchived,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? PrimaryImageId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneralAppearance { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorePersonality { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Relationships { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreferencesBeliefs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivateMotivations { get; init; }
}

public sealed record StoryLocationDocument(
    Guid Id,
    string Name,
    string Summary,
    string Details,
    bool IsArchived,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? PrimaryImageId = null);

public sealed record StoryItemDocument(
    Guid Id,
    string Name,
    string Summary,
    string Details,
    Guid? OwnerCharacterId,
    Guid? LocationId,
    bool IsArchived,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? PrimaryImageId = null);

public sealed record StoryHistoryFactDocument(
    Guid Id,
    int SortOrder,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryTimelineEntryDocument(
    Guid Id,
    int SortOrder,
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public static class ChatStoryJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    public static T Deserialize<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

public static class StoryDocumentNormalizer
{
    public static ChatStorySceneDocument Normalize(ChatStorySceneDocument? document) => new(
        document?.CurrentLocationId,
        NormalizeIds(document?.PresentCharacterIds),
        NormalizeIds(document?.PresentItemIds),
        NormalizeOptionalText(document?.DerivedContextSummary),
        NormalizeOptionalText(document?.ManualContextNotes));

    public static ChatStoryCharactersDocument Normalize(ChatStoryCharactersDocument? document) => new(
        NormalizeCharacters(document?.Entries));

    public static ChatStoryLocationsDocument Normalize(ChatStoryLocationsDocument? document) => new(
        NormalizeLocations(document?.Entries));

    public static ChatStoryItemsDocument Normalize(ChatStoryItemsDocument? document) => new(
        NormalizeItems(document?.Entries));

    public static ChatStoryHistoryDocument Normalize(ChatStoryHistoryDocument? document) => new(
        NormalizeFacts(document?.Facts),
        NormalizeTimelineEntries(document?.TimelineEntries));

    public static ChatStoryContextDocument Normalize(ChatStoryContextDocument? document) => new(
        NormalizeTrimmedText(document?.Genre),
        NormalizeTrimmedText(document?.Setting),
        NormalizeTrimmedText(document?.Tone),
        NormalizeTrimmedText(document?.StoryDirection),
        NormalizeContentIntensity(document?.ExplicitContent),
        NormalizeContentIntensity(document?.ViolentContent));

    private static IReadOnlyList<Guid> NormalizeIds(IReadOnlyList<Guid>? ids) =>
        ids?
            .Distinct()
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryCharacterDocument> NormalizeCharacters(IReadOnlyList<StoryCharacterDocument>? entries) =>
        entries?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryLocationDocument> NormalizeLocations(IReadOnlyList<StoryLocationDocument>? entries) =>
        entries?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryItemDocument> NormalizeItems(IReadOnlyList<StoryItemDocument>? entries) =>
        entries?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryHistoryFactDocument> NormalizeFacts(IReadOnlyList<StoryHistoryFactDocument>? entries) =>
        entries?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? [];

    private static IReadOnlyList<StoryTimelineEntryDocument> NormalizeTimelineEntries(IReadOnlyList<StoryTimelineEntryDocument>? entries) =>
        entries?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? [];

    private static StoryCharacterDocument Normalize(StoryCharacterDocument? document) => new(
        document?.Id ?? Guid.Empty,
        NormalizeText(document?.Name),
        Normalize(document?.UserSheet, document),
        Normalize(document?.ModelSheet),
        NormalizeUserSheetRevision(document),
        NormalizeModelSheetReviewedAgainstRevision(document),
        document?.IsArchived ?? false,
        document?.PrimaryImageId);

    private static StoryCharacterUserSheetDocument Normalize(
        StoryCharacterUserSheetDocument? document,
        StoryCharacterDocument? legacyDocument = null) => new(
        NormalizeText(document?.Summary ?? legacyDocument?.Summary),
        NormalizeText(document?.GeneralAppearance ?? legacyDocument?.GeneralAppearance),
        NormalizeText(document?.CorePersonality ?? legacyDocument?.CorePersonality),
        NormalizeText(document?.Relationships ?? legacyDocument?.Relationships),
        NormalizeText(document?.PreferencesBeliefs ?? legacyDocument?.PreferencesBeliefs),
        NormalizeText(document?.PrivateMotivations ?? legacyDocument?.PrivateMotivations));

    private static StoryCharacterModelSheetDocument Normalize(StoryCharacterModelSheetDocument? document) => new(
        NormalizeText(document?.Summary),
        NormalizeText(document?.Appearance),
        NormalizeText(document?.Voice),
        NormalizeText(document?.Hides),
        NormalizeText(document?.Tendency),
        NormalizeText(document?.Constraint),
        NormalizeText(document?.Relationships),
        NormalizeText(document?.LikesBeliefs),
        NormalizeText(document?.PrivateMotivations));

    private static int NormalizeUserSheetRevision(StoryCharacterDocument? document)
    {
        if (document is null)
            return 0;

        if (document.UserSheetRevision > 0)
            return document.UserSheetRevision;

        return HasLegacyUserSheetContent(document) ? 1 : 0;
    }

    private static int? NormalizeModelSheetReviewedAgainstRevision(StoryCharacterDocument? document)
    {
        if (document?.ModelSheetReviewedAgainstRevision is int revision && revision > 0)
            return revision;

        return null;
    }

    private static bool HasLegacyUserSheetContent(StoryCharacterDocument document) =>
        !string.IsNullOrWhiteSpace(document.Summary)
        || !string.IsNullOrWhiteSpace(document.GeneralAppearance)
        || !string.IsNullOrWhiteSpace(document.CorePersonality)
        || !string.IsNullOrWhiteSpace(document.Relationships)
        || !string.IsNullOrWhiteSpace(document.PreferencesBeliefs)
        || !string.IsNullOrWhiteSpace(document.PrivateMotivations);

    private static StoryLocationDocument Normalize(StoryLocationDocument? document) => new(
        document?.Id ?? Guid.Empty,
        NormalizeText(document?.Name),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        document?.IsArchived ?? false,
        document?.PrimaryImageId);

    private static StoryItemDocument Normalize(StoryItemDocument? document) => new(
        document?.Id ?? Guid.Empty,
        NormalizeText(document?.Name),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        document?.OwnerCharacterId,
        document?.LocationId,
        document?.IsArchived ?? false,
        document?.PrimaryImageId);

    private static StoryHistoryFactDocument Normalize(StoryHistoryFactDocument? document) => new(
        document?.Id ?? Guid.Empty,
        document?.SortOrder ?? 0,
        NormalizeText(document?.Title),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        NormalizeIds(document?.CharacterIds),
        NormalizeIds(document?.LocationIds),
        NormalizeIds(document?.ItemIds));

    private static StoryTimelineEntryDocument Normalize(StoryTimelineEntryDocument? document) => new(
        document?.Id ?? Guid.Empty,
        document?.SortOrder ?? 0,
        NormalizeOptionalText(document?.WhenText),
        NormalizeText(document?.Title),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        NormalizeIds(document?.CharacterIds),
        NormalizeIds(document?.LocationIds),
        NormalizeIds(document?.ItemIds));

    private static StoryContentIntensity NormalizeContentIntensity(StoryContentIntensity? intensity) =>
        intensity is StoryContentIntensity.Forbidden or StoryContentIntensity.Encouraged
            ? intensity.Value
            : StoryContentIntensity.Allowed;

    private static string NormalizeText(string? value) => value ?? string.Empty;

    private static string NormalizeTrimmedText(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
