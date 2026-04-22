namespace AgentRp.Data;

public sealed record StoryChatAppearanceDocument(
    IReadOnlyList<StoryChatCharacterAppearanceDocument> Characters)
{
    public static StoryChatAppearanceDocument Empty { get; } = new([]);
}

public sealed record StoryChatCharacterAppearanceDocument(
    Guid CharacterId,
    string CurrentAppearance);

public static class StoryChatAppearanceDocumentNormalizer
{
    public static StoryChatAppearanceDocument Normalize(StoryChatAppearanceDocument? document) => new(
        document?.Characters?
            .Where(x => x is not null)
            .Select(Normalize)
            .ToList()
        ?? []);

    private static StoryChatCharacterAppearanceDocument Normalize(StoryChatCharacterAppearanceDocument? document) => new(
        document?.CharacterId ?? Guid.Empty,
        document?.CurrentAppearance?.Trim() ?? string.Empty);
}
