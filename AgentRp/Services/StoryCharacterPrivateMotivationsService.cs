using System.Text;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentRp.Services;

public sealed class StoryCharacterPrivateMotivationsService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IThreadAgentService threadAgentService,
    ILogger<StoryCharacterPrivateMotivationsService> logger) : IStoryCharacterPrivateMotivationsService
{
    public async Task<StoryCharacterPrivateMotivationOptionsResult> GenerateOptionsAsync(
        GenerateStoryCharacterPrivateMotivationOptions request,
        CancellationToken cancellationToken)
    {
        var character = Normalize(request.Character);
        ValidateDraft(character, "Generating private motivations");

        var agent = await threadAgentService.GetSelectedAgentAsync(request.ThreadId, cancellationToken);
        if (agent is null)
        {
            logger.LogError("No AI provider is configured for private motivations in chat {ThreadId}.", request.ThreadId);
            throw new InvalidOperationException("Generating private motivations failed because no AI provider is configured for this chat.");
        }

        var snapshot = await LoadSnapshotAsync(request.ThreadId, cancellationToken);
        var brainstormResponse = await agent.ChatClient.GetResponseAsync<BrainstormStageResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildBrainstormSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildBrainstormUserPrompt(snapshot, character, request.ExistingOptions))
            ],
            options: new ChatOptions { Temperature = 0.8f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var curatedResponse = await agent.ChatClient.GetResponseAsync<CuratedOptionsResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildCurateSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildCurateUserPrompt(snapshot, character, request.ExistingOptions, brainstormResponse.Result))
            ],
            options: new ChatOptions { Temperature = 0.35f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);

        return new StoryCharacterPrivateMotivationOptionsResult(
            FallbackReviewSummary(curatedResponse.Result.ReviewSummary, character.Name, "Review the proposed private-motivation ideas."),
            MapOptions(curatedResponse.Result.Options, request.ExistingOptions));
    }

    public async Task<StoryCharacterPrivateMotivationsPreview> ComposeNarrativeAsync(
        ComposeStoryCharacterPrivateMotivations request,
        CancellationToken cancellationToken)
    {
        var character = Normalize(request.Character);
        ValidateDraft(character, "Composing private motivations");

        if (request.SelectedOptions.Count == 0)
            throw new InvalidOperationException($"Composing private motivations failed because no motivations were selected for '{BuildDisplayName(character.Name)}'.");

        var agent = await threadAgentService.GetSelectedAgentAsync(request.ThreadId, cancellationToken);
        if (agent is null)
        {
            logger.LogError("No AI provider is configured for private motivation composition in chat {ThreadId}.", request.ThreadId);
            throw new InvalidOperationException("Composing private motivations failed because no AI provider is configured for this chat.");
        }

        var snapshot = await LoadSnapshotAsync(request.ThreadId, cancellationToken);
        var response = await agent.ChatClient.GetResponseAsync<ComposeNarrativeResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildComposeSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildComposeUserPrompt(snapshot, character, request.SelectedOptions))
            ],
            options: new ChatOptions { Temperature = 0.4f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);

        var narrative = RequireText(response.Result.Narrative, "private motivations narrative");
        return new StoryCharacterPrivateMotivationsPreview(
            FallbackReviewSummary(response.Result.ReviewSummary, character.Name, "Review the private motivations narrative before applying it."),
            narrative);
    }

    private async Task<StoryPrivateMotivationsSnapshot> LoadSnapshotAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading story context for private motivations failed because the selected story could not be found.");

        return new StoryPrivateMotivationsSnapshot(
            story.ChatThreadId,
            story.Scene,
            story.Characters.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.Locations.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.Items.Entries.Where(x => !x.IsArchived).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            story.History);
    }

    private static StoryCharacterDraftContext Normalize(StoryCharacterDraftContext character) => character with
    {
        Name = NormalizeText(character.Name),
        Summary = NormalizeText(character.Summary),
        GeneralAppearance = NormalizeText(character.GeneralAppearance),
        CorePersonality = NormalizeText(character.CorePersonality),
        Relationships = NormalizeText(character.Relationships),
        PreferencesBeliefs = NormalizeText(character.PreferencesBeliefs),
        PrivateMotivations = NormalizeText(character.PrivateMotivations)
    };

    private static void ValidateDraft(StoryCharacterDraftContext character, string operation)
    {
        var hasAnyContext = new[]
        {
            character.Name,
            character.Summary,
            character.GeneralAppearance,
            character.CorePersonality,
            character.Relationships,
            character.PreferencesBeliefs,
            character.PrivateMotivations
        }.Any(x => !string.IsNullOrWhiteSpace(x));

        if (!hasAnyContext)
            throw new InvalidOperationException($"{operation} failed because the character draft is still blank. Add at least a name or a few notes first.");
    }

    private static IReadOnlyList<StoryCharacterPrivateMotivationOption> MapOptions(
        IReadOnlyList<CuratedOptionResponse>? options,
        IReadOnlyList<StoryCharacterPrivateMotivationOption> existingOptions)
    {
        if (options is null || options.Count == 0)
            return [];

        var existingKeys = existingOptions
            .Select(BuildOptionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedOptions = new List<StoryCharacterPrivateMotivationOption>();

        foreach (var option in options)
        {
            var mapped = new StoryCharacterPrivateMotivationOption(
                option.Category,
                RequireText(option.Label, "private motivation label"),
                RequireText(option.Explanation, "private motivation explanation"),
                RequireText(option.WhyItFits, "private motivation fit explanation"));
            var key = BuildOptionKey(mapped);
            if (existingKeys.Contains(key) || mappedOptions.Any(x => string.Equals(BuildOptionKey(x), key, StringComparison.OrdinalIgnoreCase)))
                continue;

            mappedOptions.Add(mapped);
        }

        return mappedOptions;
    }

    private static string BuildOptionKey(StoryCharacterPrivateMotivationOption option) =>
        $"{option.Category}|{NormalizeText(option.Label)}|{NormalizeText(option.Explanation)}";

    private static string BuildBrainstormSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You help authors infer hidden private motivations for a fictional character.");
        builder.AppendLine("Generate plausible internal drivers that would make the character more compelling in scene play.");
        builder.AppendLine("Stay grounded in the supplied story context.");
        builder.AppendLine("Avoid generic melodrama and obvious filler.");
        builder.AppendLine("If a category is weak for this character, return fewer ideas for it instead of forcing a bad answer.");
        builder.AppendLine("Return only structured brainstorm data.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildCurateSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You curate private-motivation ideas for a story authoring tool.");
        builder.AppendLine("Turn raw brainstorm ideas into sharp checkbox-ready options.");
        builder.AppendLine("Keep each option compact, distinct, and grounded in the story context.");
        builder.AppendLine("Do not repeat any existing options.");
        builder.AppendLine("It is okay to omit categories that have no compelling idea.");
        builder.AppendLine("Return only structured output.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildComposeSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You write concise private character motivation summaries for authors.");
        builder.AppendLine("Write hidden internal narrative, not public biography copy.");
        builder.AppendLine("Synthesize the selected motivations into one short, compelling narrative.");
        builder.AppendLine("Use one or two short paragraphs.");
        builder.AppendLine("Keep it grounded, emotionally specific, and useful for roleplay.");
        builder.AppendLine("Return only structured output.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildBrainstormUserPrompt(
        StoryPrivateMotivationsSnapshot snapshot,
        StoryCharacterDraftContext character,
        IReadOnlyList<StoryCharacterPrivateMotivationOption> existingOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.BuildContextSummary(character));
        builder.AppendLine();
        builder.AppendLine("Generate raw idea candidates across these categories:");
        foreach (var category in GetOrderedCategories())
            builder.AppendLine($"- {FormatCategory(category)}");

        if (existingOptions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avoid repeating these existing options:");
            foreach (var option in existingOptions)
                builder.AppendLine($"- {FormatCategory(option.Category)} | {option.Label} | {option.Explanation}");
        }

        builder.AppendLine();
        builder.AppendLine("Return 1-3 raw ideas per category when the context supports them.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildCurateUserPrompt(
        StoryPrivateMotivationsSnapshot snapshot,
        StoryCharacterDraftContext character,
        IReadOnlyList<StoryCharacterPrivateMotivationOption> existingOptions,
        BrainstormStageResponse brainstorm)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.BuildContextSummary(character));
        builder.AppendLine();
        builder.AppendLine("Existing options to avoid:");
        if (existingOptions.Count == 0)
            builder.AppendLine("None.");
        else
            foreach (var option in existingOptions)
                builder.AppendLine($"- {FormatCategory(option.Category)} | {option.Label} | {option.Explanation}");

        builder.AppendLine();
        builder.AppendLine("Raw brainstorm ideas:");
        if (brainstorm.Ideas is null || brainstorm.Ideas.Count == 0)
        {
            builder.AppendLine("No brainstorm ideas were returned.");
        }
        else
        {
            foreach (var idea in brainstorm.Ideas)
            {
                builder.AppendLine($"- {FormatCategory(idea.Category)}");
                builder.AppendLine($"  Premise: {idea.Premise}");
                builder.AppendLine($"  Explanation: {idea.Explanation}");
                builder.AppendLine($"  Story evidence: {idea.StoryEvidence}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Return only the strongest distinct checkbox-ready options.");
        builder.AppendLine("Each label should be short. Each explanation and why-it-fits line should each be one or two sentences.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildComposeUserPrompt(
        StoryPrivateMotivationsSnapshot snapshot,
        StoryCharacterDraftContext character,
        IReadOnlyList<StoryCharacterPrivateMotivationOption> selectedOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.BuildContextSummary(character));
        builder.AppendLine();
        builder.AppendLine("Selected private motivation ideas:");
        foreach (var option in selectedOptions)
        {
            builder.AppendLine($"- {FormatCategory(option.Category)} | {option.Label}");
            builder.AppendLine($"  Explanation: {option.Explanation}");
            builder.AppendLine($"  Why it fits: {option.WhyItFits}");
        }

        if (!string.IsNullOrWhiteSpace(character.PrivateMotivations))
        {
            builder.AppendLine();
            builder.AppendLine("Existing private motivations text to harmonize with if possible:");
            builder.AppendLine(character.PrivateMotivations);
        }

        builder.AppendLine();
        builder.AppendLine("Write a short narrative that the narrator or acting character can use as hidden motivation context.");
        return builder.ToString().TrimEnd();
    }

    private static string FormatCategory(StoryCharacterPrivateMotivationCategory category) => category switch
    {
        StoryCharacterPrivateMotivationCategory.SecretDesireOrCrush => "Secret desire or crush",
        StoryCharacterPrivateMotivationCategory.WhyTheyLashOut => "Why they lash out",
        StoryCharacterPrivateMotivationCategory.FearOfLoss => "Fear of losing something",
        StoryCharacterPrivateMotivationCategory.OldWound => "Old wound",
        StoryCharacterPrivateMotivationCategory.UnadmittedTruth => "What they will not admit out loud",
        StoryCharacterPrivateMotivationCategory.BiggestAspiration => "Biggest aspiration",
        StoryCharacterPrivateMotivationCategory.ContradictionOrCompulsion => "Contradiction or compulsion",
        _ => category.ToString()
    };

    private static IReadOnlyList<StoryCharacterPrivateMotivationCategory> GetOrderedCategories() =>
    [
        StoryCharacterPrivateMotivationCategory.SecretDesireOrCrush,
        StoryCharacterPrivateMotivationCategory.WhyTheyLashOut,
        StoryCharacterPrivateMotivationCategory.FearOfLoss,
        StoryCharacterPrivateMotivationCategory.OldWound,
        StoryCharacterPrivateMotivationCategory.UnadmittedTruth,
        StoryCharacterPrivateMotivationCategory.BiggestAspiration,
        StoryCharacterPrivateMotivationCategory.ContradictionOrCompulsion
    ];

    private static string RequireText(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        throw new InvalidOperationException($"Generating private motivations failed because the model returned an empty {fieldName}.");
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    private static string FallbackReviewSummary(string? value, string characterName, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"{fallback} Target: {BuildDisplayName(characterName)}."
            : trimmed;
    }

    private static string BuildDisplayName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Untitled Character" : name.Trim();

    private sealed record BrainstormStageResponse(
        string? ReviewSummary,
        IReadOnlyList<BrainstormIdeaResponse>? Ideas);

    private sealed record BrainstormIdeaResponse(
        StoryCharacterPrivateMotivationCategory Category,
        string? Premise,
        string? Explanation,
        string? StoryEvidence);

    private sealed record CuratedOptionsResponse(
        string? ReviewSummary,
        IReadOnlyList<CuratedOptionResponse>? Options);

    private sealed record CuratedOptionResponse(
        StoryCharacterPrivateMotivationCategory Category,
        string? Label,
        string? Explanation,
        string? WhyItFits);

    private sealed record ComposeNarrativeResponse(
        string? ReviewSummary,
        string? Narrative);

    private sealed class StoryPrivateMotivationsSnapshot(
        Guid threadId,
        ChatStorySceneDocument scene,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyList<StoryLocationDocument> locations,
        IReadOnlyList<StoryItemDocument> items,
        ChatStoryHistoryDocument history)
    {
        public Guid ThreadId { get; } = threadId;

        public ChatStorySceneDocument Scene { get; } = scene;

        public IReadOnlyList<StoryCharacterDocument> Characters { get; } = characters;

        public IReadOnlyList<StoryLocationDocument> Locations { get; } = locations;

        public IReadOnlyList<StoryItemDocument> Items { get; } = items;

        public ChatStoryHistoryDocument History { get; } = history;

        public string BuildContextSummary(StoryCharacterDraftContext target)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Target character draft:");
            builder.AppendLine($"Name: {BuildDisplayName(target.Name)}");
            builder.AppendLine($"Summary: {FallbackText(target.Summary)}");
            builder.AppendLine($"General appearance: {FallbackText(target.GeneralAppearance)}");
            builder.AppendLine($"Core personality: {FallbackText(target.CorePersonality)}");
            builder.AppendLine($"Relationships: {FallbackText(target.Relationships)}");
            builder.AppendLine($"Preferences / beliefs: {FallbackText(target.PreferencesBeliefs)}");
            builder.AppendLine($"Existing private motivations: {FallbackText(target.PrivateMotivations)}");
            builder.AppendLine($"Present in current scene: {(target.IsPresentInScene ? "Yes" : "No")}");
            builder.AppendLine();
            builder.AppendLine($"Current scene: {BuildSceneSummary()}");
            builder.AppendLine();
            builder.AppendLine("Other active characters:");
            AppendCharacterContexts(builder, BuildOtherCharacterContexts(target));
            builder.AppendLine();
            builder.AppendLine("Relevant locations:");
            AppendLines(builder, BuildRelevantLocations(target));
            builder.AppendLine();
            builder.AppendLine("Relevant items:");
            AppendLines(builder, BuildRelevantItems(target));
            builder.AppendLine();
            builder.AppendLine("Relevant story history:");
            AppendLines(builder, BuildRelevantHistory(target));
            return builder.ToString().TrimEnd();
        }

        private string BuildSceneSummary()
        {
            var currentLocation = Scene.CurrentLocationId.HasValue
                ? Locations.FirstOrDefault(x => x.Id == Scene.CurrentLocationId.Value)?.Name
                : null;
            var presentCharacters = Characters
                .Where(x => Scene.PresentCharacterIds.Contains(x.Id))
                .Select(x => x.Name)
                .ToList();
            var presentItems = Items
                .Where(x => Scene.PresentItemIds.Contains(x.Id))
                .Select(x => x.Name)
                .ToList();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(currentLocation))
                parts.Add($"Current location: {currentLocation}");
            if (presentCharacters.Count > 0)
                parts.Add($"Present characters: {string.Join(", ", presentCharacters)}");
            if (presentItems.Count > 0)
                parts.Add($"Present items: {string.Join(", ", presentItems)}");
            if (!string.IsNullOrWhiteSpace(Scene.DerivedContextSummary))
                parts.Add(Scene.DerivedContextSummary);
            if (!string.IsNullOrWhiteSpace(Scene.ManualContextNotes))
                parts.Add(Scene.ManualContextNotes);

            return parts.Count == 0 ? "No explicit current scene notes." : string.Join(" | ", parts);
        }

        private IReadOnlyList<OtherCharacterContext> BuildOtherCharacterContexts(StoryCharacterDraftContext target) =>
            Characters
                .Where(x => !target.CharacterId.HasValue || x.Id != target.CharacterId.Value)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new OtherCharacterContext(
                    x.Name,
                    x.Summary,
                    x.GeneralAppearance,
                    x.CorePersonality,
                    x.Relationships,
                    x.PreferencesBeliefs,
                    x.PrivateMotivations,
                    Scene.PresentCharacterIds.Contains(x.Id)))
                .ToList();

        private IReadOnlyList<string> BuildRelevantLocations(StoryCharacterDraftContext target)
        {
            var locationIds = GetRelevantHistoryEntries(target)
                .SelectMany(x => x.LocationIds)
                .ToHashSet();

            foreach (var location in Locations)
            {
                if (ContainsName(target.Relationships, location.Name)
                    || ContainsName(target.PrivateMotivations, location.Name)
                    || ContainsName(target.Summary, location.Name))
                    locationIds.Add(location.Id);
            }

            if (Scene.CurrentLocationId.HasValue)
                locationIds.Add(Scene.CurrentLocationId.Value);

            return Locations
                .Where(x => locationIds.Contains(x.Id))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(x => $"{x.Name}: {x.Summary}")
                .ToList();
        }

        private IReadOnlyList<string> BuildRelevantItems(StoryCharacterDraftContext target)
        {
            var itemIds = GetRelevantHistoryEntries(target)
                .SelectMany(x => x.ItemIds)
                .ToHashSet();

            foreach (var item in Items)
            {
                if (ContainsName(target.Relationships, item.Name)
                    || ContainsName(target.PrivateMotivations, item.Name)
                    || ContainsName(target.Summary, item.Name))
                    itemIds.Add(item.Id);
            }

            foreach (var itemId in Scene.PresentItemIds)
                itemIds.Add(itemId);

            return Items
                .Where(x => itemIds.Contains(x.Id))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(x => $"{x.Name}: {x.Summary}")
                .ToList();
        }

        private IReadOnlyList<string> BuildRelevantHistory(StoryCharacterDraftContext target) =>
            GetRelevantHistoryEntries(target)
                .Take(10)
                .Select(x => string.IsNullOrWhiteSpace(x.WhenText)
                    ? $"{x.Kind} | {x.Title} | {x.Summary} | {x.Details} | Links: {BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds)}"
                    : $"{x.Kind} | {x.WhenText}: {x.Title} | {x.Summary} | {x.Details} | Links: {BuildLinkedNames(x.CharacterIds, x.LocationIds, x.ItemIds)}")
                .ToList();

        private IReadOnlyList<HistoryLinkEntry> GetRelevantHistoryEntries(StoryCharacterDraftContext target)
        {
            var targetName = NormalizeText(target.Name);
            var relatedCharacterIds = new HashSet<Guid>();
            if (target.CharacterId.HasValue)
                relatedCharacterIds.Add(target.CharacterId.Value);

            foreach (var character in Characters)
            {
                if (ContainsName(target.Relationships, character.Name)
                    || ContainsName(character.Relationships, targetName))
                    relatedCharacterIds.Add(character.Id);
            }

            var allEntries = History.Facts
                .Select(x => new HistoryLinkEntry("Fact", string.Empty, x.Title, x.Summary, x.Details, x.CharacterIds, x.LocationIds, x.ItemIds))
                .Concat(History.TimelineEntries.Select(x => new HistoryLinkEntry("Timeline", x.WhenText ?? "Unknown time", x.Title, x.Summary, x.Details, x.CharacterIds, x.LocationIds, x.ItemIds)))
                .ToList();
            var entries = allEntries
                .Where(x => IsRelevantHistoryEntry(x, target, relatedCharacterIds))
                .ToList();

            return entries.Count == 0
                ? allEntries.Take(4).ToList()
                : entries;
        }

        private bool IsRelevantHistoryEntry(
            HistoryLinkEntry entry,
            StoryCharacterDraftContext target,
            IReadOnlySet<Guid> relatedCharacterIds)
        {
            if (target.CharacterId.HasValue && entry.CharacterIds.Contains(target.CharacterId.Value))
                return true;

            if (entry.CharacterIds.Any(relatedCharacterIds.Contains))
                return true;

            var targetName = NormalizeText(target.Name);
            if (!string.IsNullOrWhiteSpace(targetName)
                && (ContainsName(entry.Title, targetName)
                    || ContainsName(entry.Summary, targetName)
                    || ContainsName(entry.Details, targetName)
                    || BuildLinkedNames(entry.CharacterIds, entry.LocationIds, entry.ItemIds).Contains(targetName, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private string BuildLinkedNames(
            IReadOnlyList<Guid> characterIds,
            IReadOnlyList<Guid> locationIds,
            IReadOnlyList<Guid> itemIds)
        {
            var names = Characters.Where(x => characterIds.Contains(x.Id)).Select(x => x.Name)
                .Concat(Locations.Where(x => locationIds.Contains(x.Id)).Select(x => x.Name))
                .Concat(Items.Where(x => itemIds.Contains(x.Id)).Select(x => x.Name))
                .ToList();
            return names.Count == 0 ? "None" : string.Join(", ", names);
        }

        private static void AppendLines(StringBuilder builder, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
            {
                builder.AppendLine("- None strongly linked yet.");
                return;
            }

            foreach (var line in lines)
                builder.AppendLine($"- {line}");
        }

        private static void AppendCharacterContexts(StringBuilder builder, IReadOnlyList<OtherCharacterContext> characters)
        {
            if (characters.Count == 0)
            {
                builder.AppendLine("- No other active characters yet.");
                return;
            }

            foreach (var character in characters)
            {
                builder.AppendLine($"- {BuildDisplayName(character.Name)}");
                builder.AppendLine($"  Summary: {FallbackText(character.Summary)}");
                builder.AppendLine($"  General appearance: {FallbackText(character.GeneralAppearance)}");
                builder.AppendLine($"  Core personality: {FallbackText(character.CorePersonality)}");
                builder.AppendLine($"  Relationships: {FallbackText(character.Relationships)}");
                builder.AppendLine($"  Preferences / beliefs: {FallbackText(character.PreferencesBeliefs)}");
                builder.AppendLine($"  Private motivations: {FallbackText(character.PrivateMotivations)}");
                builder.AppendLine($"  Present in current scene: {(character.IsPresentInScene ? "Yes" : "No")}");
            }
        }

        private static bool ContainsName(string source, string candidate)
        {
            var normalizedCandidate = NormalizeText(candidate);
            return !string.IsNullOrWhiteSpace(normalizedCandidate)
                && source.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
        }

        private static string FallbackText(string value) =>
            string.IsNullOrWhiteSpace(value) ? "None yet." : value;

        private sealed record HistoryLinkEntry(
            string Kind,
            string WhenText,
            string Title,
            string Summary,
            string Details,
            IReadOnlyList<Guid> CharacterIds,
            IReadOnlyList<Guid> LocationIds,
            IReadOnlyList<Guid> ItemIds);

        private sealed record OtherCharacterContext(
            string Name,
            string Summary,
            string GeneralAppearance,
            string CorePersonality,
            string Relationships,
            string PreferencesBeliefs,
            string PrivateMotivations,
            bool IsPresentInScene);
    }
}
