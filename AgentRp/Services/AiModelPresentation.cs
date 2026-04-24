using AgentRp.Data;

namespace AgentRp.Services;

public static class AiModelPresentation
{
    private static readonly IReadOnlyDictionary<AiProviderKind, string> RecommendedModelIds = new Dictionary<AiProviderKind, string>
    {
        [AiProviderKind.OpenAI] = "gpt-5.5-mini",
        [AiProviderKind.Grok] = "grok-4-1-fast-non-reasoning"
    };

    public static bool ShouldShowSubtitle(string title, string subtitle) =>
        !string.Equals(title.Trim(), subtitle.Trim(), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AiProviderDraftModelView> OrderDraftModels(
        AiProviderKind providerKind,
        IEnumerable<AiProviderDraftModelView> models) =>
        models
            .Order(new DraftModelComparer(providerKind))
            .ToList();

    public static IReadOnlyList<AiModelEditorView> OrderEditorModels(
        AiProviderKind providerKind,
        IEnumerable<AiModelEditorView> models) =>
        models
            .Order(new EditorModelComparer(providerKind))
            .ToList();

    public static IReadOnlyList<AiProviderDraftModelSelection> BuildDraftSelections(
        AiProviderKind providerKind,
        IEnumerable<AiProviderDraftModelView> models,
        IReadOnlyList<AiProviderDraftModelSelection> existingSelections)
    {
        var orderedModels = OrderDraftModels(providerKind, models);
        var existingById = existingSelections.ToDictionary(x => x.ProviderModelId, StringComparer.Ordinal);
        var hasExistingSelections = existingSelections.Count > 0;
        var recommendedModelId = GetRecommendedModelId(providerKind);
        var selectedModelId = hasExistingSelections
            ? null
            : orderedModels.FirstOrDefault(x => IsRecommendedModel(x.ProviderModelId, recommendedModelId))?.ProviderModelId
                ?? orderedModels.FirstOrDefault()?.ProviderModelId;

        var selections = orderedModels
            .Select(model =>
            {
                var isEnabled = existingById.TryGetValue(model.ProviderModelId, out var existing)
                    ? existing.IsEnabled
                    : !hasExistingSelections && string.Equals(model.ProviderModelId, selectedModelId, StringComparison.Ordinal);

                return new AiProviderDraftModelSelection(
                    model.ProviderModelId,
                    model.DisplayName,
                    model.Endpoint,
                    model.Repository,
                    isEnabled);
            })
            .ToList();

        if (selections.Count > 0 && selections.All(x => !x.IsEnabled))
            selections[0] = selections[0] with { IsEnabled = true };

        return selections;
    }

    private static string? GetRecommendedModelId(AiProviderKind providerKind) =>
        RecommendedModelIds.GetValueOrDefault(providerKind);

    private static bool IsRecommendedModel(string modelId, string? recommendedModelId) =>
        !string.IsNullOrWhiteSpace(recommendedModelId)
        && string.Equals(modelId, recommendedModelId, StringComparison.OrdinalIgnoreCase);

    public static int CompareProviderModelIds(AiProviderKind providerKind, string left, string right)
    {
        var recommendedModelId = GetRecommendedModelId(providerKind);
        var leftRecommended = IsRecommendedModel(left, recommendedModelId);
        var rightRecommended = IsRecommendedModel(right, recommendedModelId);
        if (leftRecommended != rightRecommended)
            return leftRecommended ? -1 : 1;

        var leftNumbers = ExtractModelSortNumbers(left);
        var rightNumbers = ExtractModelSortNumbers(right);

        var numberCount = Math.Max(leftNumbers.VersionGroups.Count, rightNumbers.VersionGroups.Count);
        for (var index = 0; index < numberCount; index++)
        {
            var leftNumber = index < leftNumbers.VersionGroups.Count ? leftNumbers.VersionGroups[index] : 0;
            var rightNumber = index < rightNumbers.VersionGroups.Count ? rightNumbers.VersionGroups[index] : 0;
            if (leftNumber != rightNumber)
                return rightNumber.CompareTo(leftNumber);
        }

        var dateCount = Math.Max(leftNumbers.DateGroups.Count, rightNumbers.DateGroups.Count);
        for (var index = 0; index < dateCount; index++)
        {
            var leftNumber = index < leftNumbers.DateGroups.Count ? leftNumbers.DateGroups[index] : 0;
            var rightNumber = index < rightNumbers.DateGroups.Count ? rightNumbers.DateGroups[index] : 0;
            if (leftNumber != rightNumber)
                return rightNumber.CompareTo(leftNumber);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelSortNumbers ExtractModelSortNumbers(string value)
    {
        var versionGroups = new List<long>();
        var dateGroups = new List<long>();
        var current = 0L;
        var hasDigits = false;

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                hasDigits = true;
                current = (current * 10) + character - '0';
                continue;
            }

            if (!hasDigits)
                continue;

            AddNumberGroup(versionGroups, dateGroups, current);
            current = 0;
            hasDigits = false;
        }

        if (hasDigits)
            AddNumberGroup(versionGroups, dateGroups, current);

        return new ModelSortNumbers(versionGroups, dateGroups);
    }

    private static void AddNumberGroup(List<long> versionGroups, List<long> dateGroups, long value)
    {
        if (value >= 20_000_000)
            dateGroups.Add(value);
        else
            versionGroups.Add(value);
    }

    private sealed class DraftModelComparer(AiProviderKind providerKind) : IComparer<AiProviderDraftModelView>
    {
        public int Compare(AiProviderDraftModelView? x, AiProviderDraftModelView? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return 1;

            if (y is null)
                return -1;

            var modelIdComparison = CompareProviderModelIds(providerKind, x.ProviderModelId, y.ProviderModelId);
            if (modelIdComparison != 0)
                return modelIdComparison;

            return string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class EditorModelComparer(AiProviderKind providerKind) : IComparer<AiModelEditorView>
    {
        public int Compare(AiModelEditorView? x, AiModelEditorView? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return 1;

            if (y is null)
                return -1;

            var modelIdComparison = CompareProviderModelIds(providerKind, x.ProviderModelId, y.ProviderModelId);
            if (modelIdComparison != 0)
                return modelIdComparison;

            return string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record ModelSortNumbers(IReadOnlyList<long> VersionGroups, IReadOnlyList<long> DateGroups);
}
