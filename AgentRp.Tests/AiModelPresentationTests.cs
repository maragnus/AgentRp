using AgentRp.Data;
using AgentRp.Services;

namespace AgentRp.Tests;

public sealed class AiModelPresentationTests
{
    [Fact]
    public void BuildDraftSelections_OpenAi_SelectsRecommendedModel()
    {
        var selections = AiModelPresentation.BuildDraftSelections(
            AiProviderKind.OpenAI,
            [
                Model("gpt-5.4"),
                Model("gpt-5.5-mini"),
                Model("gpt-4.1")
            ],
            []);

        Assert.Equal("gpt-5.5-mini", Assert.Single(selections, x => x.IsEnabled).ProviderModelId);
        Assert.Equal("gpt-5.5-mini", selections[0].ProviderModelId);
    }

    [Fact]
    public void BuildDraftSelections_Grok_SelectsRecommendedModel()
    {
        var selections = AiModelPresentation.BuildDraftSelections(
            AiProviderKind.Grok,
            [
                Model("grok-3-mini"),
                Model("grok-4"),
                Model("grok-4-1-fast-non-reasoning")
            ],
            []);

        Assert.Equal("grok-4-1-fast-non-reasoning", Assert.Single(selections, x => x.IsEnabled).ProviderModelId);
        Assert.Equal("grok-4-1-fast-non-reasoning", selections[0].ProviderModelId);
    }

    [Fact]
    public void BuildDraftSelections_WhenNoRecommendation_SelectsFirstSortedModel()
    {
        var selections = AiModelPresentation.BuildDraftSelections(
            AiProviderKind.Claude,
            [
                Model("claude-sonnet-4-20250514"),
                Model("claude-sonnet-4-6-20260410"),
                Model("claude-3-7-sonnet")
            ],
            []);

        Assert.Equal("claude-sonnet-4-6-20260410", selections[0].ProviderModelId);
        Assert.Equal("claude-sonnet-4-6-20260410", Assert.Single(selections, x => x.IsEnabled).ProviderModelId);
    }

    [Fact]
    public void OrderDraftModels_SortsNewerVersionedModelsFirst()
    {
        var ordered = AiModelPresentation.OrderDraftModels(
            AiProviderKind.OpenAiCompatible,
            [
                Model("local-model-2"),
                Model("local-model-10"),
                Model("local-model-1")
            ]);

        Assert.Equal(["local-model-10", "local-model-2", "local-model-1"], ordered.Select(x => x.ProviderModelId).ToArray());
    }

    [Fact]
    public void BuildDraftSelections_WhenRefreshing_PreservesExistingSelections()
    {
        var selections = AiModelPresentation.BuildDraftSelections(
            AiProviderKind.OpenAI,
            [
                Model("gpt-5.5-mini"),
                Model("gpt-5.4"),
                Model("gpt-5.4-mini")
            ],
            [
                new("gpt-5.5-mini", "gpt-5.5-mini", null, null, false),
                new("gpt-5.4", "gpt-5.4", null, null, true)
            ]);

        Assert.True(selections.Single(x => x.ProviderModelId == "gpt-5.4").IsEnabled);
        Assert.False(selections.Single(x => x.ProviderModelId == "gpt-5.5-mini").IsEnabled);
        Assert.False(selections.Single(x => x.ProviderModelId == "gpt-5.4-mini").IsEnabled);
    }

    [Fact]
    public void ShouldShowSubtitle_HidesDuplicateDisplayValues()
    {
        Assert.False(AiModelPresentation.ShouldShowSubtitle("gpt-5.5-mini", " GPT-5.5-MINI "));
        Assert.True(AiModelPresentation.ShouldShowSubtitle("GPT 5.5 Mini", "gpt-5.5-mini"));
    }

    private static AiProviderDraftModelView Model(string id) => new(id, id, null, null);
}
