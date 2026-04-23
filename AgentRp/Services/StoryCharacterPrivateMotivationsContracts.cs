namespace AgentRp.Services;

public enum StoryCharacterPrivateMotivationCategory
{
    SecretDesireOrCrush,
    WhyTheyLashOut,
    FearOfLoss,
    OldWound,
    UnadmittedTruth,
    BiggestAspiration,
    ContradictionOrCompulsion
}

public sealed record StoryCharacterDraftContext(
    Guid? CharacterId,
    string Name,
    StoryCharacterUserSheetView UserSheet,
    StoryCharacterModelSheetView ModelSheet,
    StoryCharacterModelSheetStatus ModelSheetStatus,
    bool IsPresentInScene);

public sealed record StoryCharacterPrivateMotivationOption(
    StoryCharacterPrivateMotivationCategory Category,
    string Label,
    string Explanation,
    string WhyItFits);

public sealed record GenerateStoryCharacterPrivateMotivationOptions(
    Guid ThreadId,
    StoryCharacterDraftContext Character,
    IReadOnlyList<StoryCharacterPrivateMotivationOption> ExistingOptions);

public sealed record StoryCharacterPrivateMotivationOptionsResult(
    string ReviewSummary,
    IReadOnlyList<StoryCharacterPrivateMotivationOption> Options);

public sealed record ComposeStoryCharacterPrivateMotivations(
    Guid ThreadId,
    StoryCharacterDraftContext Character,
    IReadOnlyList<StoryCharacterPrivateMotivationOption> SelectedOptions);

public sealed record StoryCharacterPrivateMotivationsPreview(
    string ReviewSummary,
    string Narrative);

public interface IStoryCharacterPrivateMotivationsService
{
    Task<StoryCharacterPrivateMotivationOptionsResult> GenerateOptionsAsync(
        GenerateStoryCharacterPrivateMotivationOptions request,
        CancellationToken cancellationToken);

    Task<StoryCharacterPrivateMotivationsPreview> ComposeNarrativeAsync(
        ComposeStoryCharacterPrivateMotivations request,
        CancellationToken cancellationToken);
}
