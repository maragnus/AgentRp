using System.Text;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentRp.Services;

public sealed class StoryCharacterModelSheetService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IThreadAgentService threadAgentService,
    ILogger<StoryCharacterModelSheetService> logger) : IStoryCharacterModelSheetService
{
    public async Task<StoryCharacterModelSheetDraftView> GenerateDraftAsync(
        GenerateStoryCharacterModelSheetDraft request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Generating the model-ready character sheet failed because the selected story could not be found.");
        var character = story.Characters.Entries.FirstOrDefault(x => !x.IsArchived && x.Id == request.CharacterId)
            ?? throw new InvalidOperationException("Generating the model-ready character sheet failed because the selected character could not be found.");
        var userSheet = StoryCharacterModelSheetSupport.GetUserSheet(character);
        if (IsEmpty(userSheet))
            throw new InvalidOperationException($"Generating the model-ready character sheet failed because '{character.Name}' does not have enough user-friendly notes yet.");

        var agent = await threadAgentService.GetSelectedAgentAsync(request.ThreadId, cancellationToken);
        if (agent is null)
        {
            logger.LogError("No AI provider is configured for character model-sheet generation in chat {ThreadId}.", request.ThreadId);
            throw new InvalidOperationException("Generating the model-ready character sheet failed because no AI provider is configured for this chat.");
        }

        var response = await agent.ChatClient.GetResponseAsync<ModelSheetDraftResponse>(
            [
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildSystemPrompt()),
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildUserPrompt(story, character, userSheet))
            ],
            options: new ChatOptions { Temperature = 0.3f },
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var result = response.Result;
        var modelSheet = new StoryCharacterModelSheetView(
            Normalize(result.Summary),
            Normalize(result.Appearance),
            Normalize(result.Voice),
            Normalize(result.Hides),
            Normalize(result.Tendency),
            Normalize(result.Constraint),
            Normalize(result.Relationships),
            Normalize(result.LikesBeliefs),
            Normalize(result.PrivateMotivations));

        return new StoryCharacterModelSheetDraftView(
            character.Id,
            character.Name,
            modelSheet,
            StoryCharacterModelSheetSupport.GetStatus(character),
            string.IsNullOrWhiteSpace(result.ReviewSummary)
                ? $"Review the compact model-ready sheet for '{character.Name}' before saving it."
                : result.ReviewSummary.Trim());
    }

    private static string BuildSystemPrompt() =>
        """
        You compress author-facing character notes into a model-ready character sheet for lightweight scene prompting.

        Return structured output only.

        Output rules:
        - Use short phrases, fragments, and comma-delimited lists.
        - Do not write paragraphs.
        - Do not explain your reasoning.
        - Keep every field tight and prompt-friendly.
        - Preserve important relational and behavioral signals.
        - Prefer concrete wording over literary wording.
        - If a field is unsupported, return an empty string.

        Field intent:
        - summary: one tight identity line
        - appearance: concise evergreen appearance cues
        - voice: tone/style labels like deadpan, teasing, controlled
        - hides: what the character conceals internally
        - tendency: repeated behavior under pressure or exposure
        - constraint: what the character resists doing too quickly
        - relationships: compact relationship lines from this character's perspective
        - likesBeliefs: wants, likes, comfort-seeking, or values in compact form
        - privateMotivations: secret wants, fears, contradictions, or hidden drives

        Never repeat the same idea across multiple fields unless the duplication is truly necessary.
        """;

    private static string BuildUserPrompt(ChatStory story, StoryCharacterDocument character, StoryCharacterUserSheetDocument userSheet)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Character: {character.Name}");
        builder.AppendLine("Convert these user-facing notes into a compact model-ready sheet.");
        builder.AppendLine();
        builder.AppendLine("User-friendly notes:");
        builder.AppendLine($"- Summary: {Fallback(userSheet.Summary)}");
        builder.AppendLine($"- General appearance: {Fallback(userSheet.GeneralAppearance)}");
        builder.AppendLine($"- Core personality: {Fallback(userSheet.CorePersonality)}");
        builder.AppendLine($"- Relationships: {Fallback(userSheet.Relationships)}");
        builder.AppendLine($"- Preferences / beliefs: {Fallback(userSheet.PreferencesBeliefs)}");
        builder.AppendLine($"- Private motivations: {Fallback(userSheet.PrivateMotivations)}");

        var otherCharacters = story.Characters.Entries
            .Where(x => !x.IsArchived && x.Id != character.Id)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name)
            .ToList();
        if (otherCharacters.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Other known character names: {string.Join(", ", otherCharacters)}");
        }

        var existingModelSheet = StoryCharacterModelSheetSupport.GetModelSheet(character);
        if (!StoryCharacterModelSheetSupport.IsEmpty(existingModelSheet))
        {
            builder.AppendLine();
            builder.AppendLine("Existing saved model-ready sheet to revise if useful:");
            builder.AppendLine($"- Summary: {Fallback(existingModelSheet.Summary)}");
            builder.AppendLine($"- Appearance: {Fallback(existingModelSheet.Appearance)}");
            builder.AppendLine($"- Voice: {Fallback(existingModelSheet.Voice)}");
            builder.AppendLine($"- Hides: {Fallback(existingModelSheet.Hides)}");
            builder.AppendLine($"- Tendency: {Fallback(existingModelSheet.Tendency)}");
            builder.AppendLine($"- Constraint: {Fallback(existingModelSheet.Constraint)}");
            builder.AppendLine($"- Relationships: {Fallback(existingModelSheet.Relationships)}");
            builder.AppendLine($"- Likes / beliefs: {Fallback(existingModelSheet.LikesBeliefs)}");
            builder.AppendLine($"- Private motivations: {Fallback(existingModelSheet.PrivateMotivations)}");
        }

        builder.AppendLine();
        builder.AppendLine("Use the target shape exactly and keep it compressed enough for lightweight prompting.");
        return builder.ToString().TrimEnd();
    }

    private static bool IsEmpty(StoryCharacterUserSheetDocument userSheet) =>
        string.IsNullOrWhiteSpace(userSheet.Summary)
        && string.IsNullOrWhiteSpace(userSheet.GeneralAppearance)
        && string.IsNullOrWhiteSpace(userSheet.CorePersonality)
        && string.IsNullOrWhiteSpace(userSheet.Relationships)
        && string.IsNullOrWhiteSpace(userSheet.PreferencesBeliefs)
        && string.IsNullOrWhiteSpace(userSheet.PrivateMotivations);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string Fallback(string? value) => string.IsNullOrWhiteSpace(value) ? "None" : value.Trim();

    private sealed record ModelSheetDraftResponse(
        string ReviewSummary,
        string? Summary,
        string? Appearance,
        string? Voice,
        string? Hides,
        string? Tendency,
        string? Constraint,
        string? Relationships,
        string? LikesBeliefs,
        string? PrivateMotivations);
}
