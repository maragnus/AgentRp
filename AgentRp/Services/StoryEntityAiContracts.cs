namespace AgentRp.Services;

public enum StoryEntityKind
{
    Character,
    Item,
    Location,
    HistoryFact,
    TimelineEntry
}

public sealed record StartStoryEntityAiDraft(
    Guid ThreadId,
    StoryEntityKind EntityKind,
    Guid? EntityId,
    string Prompt);

public sealed record RefineStoryEntityAiDraft(
    Guid ThreadId,
    StoryEntityAiDraftSessionView Session,
    string Prompt);

public sealed record AcceptStoryEntityAiDraft(
    Guid ThreadId,
    StoryEntityAiDraftSessionView Session);

public sealed record StoryEntityAiDraftSessionView(
    Guid SessionId,
    Guid ThreadId,
    StoryEntityKind EntityKind,
    Guid? EntityId,
    bool IsNew,
    string ReviewSummary,
    string LatestPrompt,
    IReadOnlyList<string> PromptHistory,
    StoryEntityAiDraftView Draft);

public abstract record StoryEntityAiDraftView(StoryEntityKind EntityKind);

public sealed record StoryEntityReferenceView(
    Guid Id,
    string Name);

public sealed record CharacterAiDraftView(
    string Name,
    StoryCharacterUserSheetView UserSheet,
    bool IsPresentInScene) : StoryEntityAiDraftView(StoryEntityKind.Character);

public sealed record ItemAiDraftView(
    string Name,
    string Summary,
    string Details,
    Guid? OwnerCharacterId,
    string? OwnerCharacterName,
    Guid? LocationId,
    string? LocationName,
    bool IsPresentInScene) : StoryEntityAiDraftView(StoryEntityKind.Item);

public sealed record LocationAiDraftView(
    string Name,
    string Summary,
    string Details,
    bool IsCurrent) : StoryEntityAiDraftView(StoryEntityKind.Location);

public sealed record HistoryFactAiDraftView(
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<StoryEntityReferenceView> Characters,
    IReadOnlyList<StoryEntityReferenceView> Locations,
    IReadOnlyList<StoryEntityReferenceView> Items) : StoryEntityAiDraftView(StoryEntityKind.HistoryFact);

public sealed record TimelineEntryAiDraftView(
    int? SortOrder,
    string WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<StoryEntityReferenceView> Characters,
    IReadOnlyList<StoryEntityReferenceView> Locations,
    IReadOnlyList<StoryEntityReferenceView> Items) : StoryEntityAiDraftView(StoryEntityKind.TimelineEntry);

public sealed record StoryEntityAcceptResult(
    Guid EntityId,
    StoryEntityKind EntityKind,
    string DisplayName);

public interface IStoryEntityAiAssistService
{
    Task<StoryEntityAiDraftSessionView> GenerateDraftAsync(StartStoryEntityAiDraft request, CancellationToken cancellationToken);

    Task<StoryEntityAiDraftSessionView> RefineDraftAsync(RefineStoryEntityAiDraft request, CancellationToken cancellationToken);

    Task<StoryEntityAcceptResult> AcceptDraftAsync(AcceptStoryEntityAiDraft request, CancellationToken cancellationToken);
}
