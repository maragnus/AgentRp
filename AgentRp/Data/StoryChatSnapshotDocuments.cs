namespace AgentRp.Data;

public sealed record StoryChatSnapshotDocument(
    string NarrativeSummary,
    IReadOnlyList<StoryChatSnapshotFactDocument> Facts,
    IReadOnlyList<StoryChatSnapshotTimelineEntryDocument> TimelineEntries)
{
    public static StoryChatSnapshotDocument Empty { get; } = new(string.Empty, [], []);
}

public sealed record StoryChatSnapshotFactDocument(
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryChatSnapshotTimelineEntryDocument(
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);
