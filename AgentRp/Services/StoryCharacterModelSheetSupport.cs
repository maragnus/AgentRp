using AgentRp.Data;

namespace AgentRp.Services;

internal static class StoryCharacterModelSheetSupport
{
    internal static StoryCharacterUserSheetDocument GetUserSheet(StoryCharacterDocument character) =>
        character.UserSheet ?? StoryCharacterUserSheetDocument.Empty;

    internal static StoryCharacterModelSheetDocument GetModelSheet(StoryCharacterDocument character) =>
        character.ModelSheet ?? StoryCharacterModelSheetDocument.Empty;

    internal static StoryCharacterModelSheetStatus GetStatus(StoryCharacterDocument character)
    {
        if (IsEmpty(GetModelSheet(character)))
            return StoryCharacterModelSheetStatus.Missing;

        return character.ModelSheetReviewedAgainstRevision == character.UserSheetRevision
            ? StoryCharacterModelSheetStatus.Ready
            : StoryCharacterModelSheetStatus.Stale;
    }

    internal static bool IsReady(StoryCharacterDocument character) => GetStatus(character) == StoryCharacterModelSheetStatus.Ready;

    internal static void EnsureReady(
        IEnumerable<StoryCharacterDocument> characters,
        Func<StoryCharacterDocument, bool> include,
        string operation)
    {
        var blocked = characters
            .Where(include)
            .Where(x => !IsReady(x))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blocked.Count == 0)
            return;

        throw new InvalidOperationException(BuildBlockedMessage(blocked, operation));
    }

    internal static string BuildBlockedMessage(IReadOnlyList<StoryCharacterDocument> blocked, string operation)
    {
        var names = blocked.Select(x => $"'{x.Name}'").ToList();
        var joinedNames = names.Count == 1 ? names[0] : string.Join(", ", names);
        return $"{operation} failed because the model-ready character sheet is missing or stale for {joinedNames}. Regenerate, review, and save the model-ready sheet first.";
    }

    internal static bool IsEmpty(StoryCharacterModelSheetDocument document) =>
        string.IsNullOrWhiteSpace(document.Summary)
        && string.IsNullOrWhiteSpace(document.Appearance)
        && string.IsNullOrWhiteSpace(document.Voice)
        && string.IsNullOrWhiteSpace(document.Hides)
        && string.IsNullOrWhiteSpace(document.Tendency)
        && string.IsNullOrWhiteSpace(document.Constraint)
        && string.IsNullOrWhiteSpace(document.Relationships)
        && string.IsNullOrWhiteSpace(document.LikesBeliefs)
        && string.IsNullOrWhiteSpace(document.PrivateMotivations);
}
