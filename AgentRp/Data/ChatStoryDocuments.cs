using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

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

public sealed record StoryCharacterDocument(
    Guid Id,
    string Name,
    string Summary,
    string GeneralAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    string PrivateMotivations,
    bool IsArchived);

public sealed record StoryLocationDocument(
    Guid Id,
    string Name,
    string Summary,
    string Details,
    bool IsArchived);

public sealed record StoryItemDocument(
    Guid Id,
    string Name,
    string Summary,
    string Details,
    Guid? OwnerCharacterId,
    Guid? LocationId,
    bool IsArchived);

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
        NormalizeText(document?.Summary),
        NormalizeText(document?.GeneralAppearance),
        NormalizeText(document?.CorePersonality),
        NormalizeText(document?.Relationships),
        NormalizeText(document?.PreferencesBeliefs),
        NormalizeText(document?.PrivateMotivations),
        document?.IsArchived ?? false);

    private static StoryLocationDocument Normalize(StoryLocationDocument? document) => new(
        document?.Id ?? Guid.Empty,
        NormalizeText(document?.Name),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        document?.IsArchived ?? false);

    private static StoryItemDocument Normalize(StoryItemDocument? document) => new(
        document?.Id ?? Guid.Empty,
        NormalizeText(document?.Name),
        NormalizeText(document?.Summary),
        NormalizeText(document?.Details),
        document?.OwnerCharacterId,
        document?.LocationId,
        document?.IsArchived ?? false);

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

    private static string NormalizeText(string? value) => value ?? string.Empty;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
