using System.Text;
using AgentRp.Data;

namespace AgentRp.Services;

internal static class StoryScenePlanningPromptBuilder
{
    internal static string BuildSystemPrompt() =>
        """
        You are the planning stage for a story scene message generator.
        Decide the next turn before any prose is written.
        Return only a concise structured plan.

        Stay grounded in the provided story context, scene state, character facts, and transcript.
        Plan one turn only.
        Choose one immediate beat, not a sequence.

        Build the plan using these fields:
        - Turn shape: choose exactly one of compact, brief, extended, monologue, silent, or silent monologue.
        - Beat: the kind of move being made in this turn.
        - Intent: the actor's immediate intention.
        - Immediate goal: what this turn tries to achieve right now.
        - Why now: why this beat fits this exact moment in the transcript.
        - Change introduced: what becomes different after this turn.
        - Private Intent: the actor's private continuity note for the hidden reason, feeling, agenda, fear, memory, sensation, concealed object, concealed action, or unspoken detail behind this turn.
        - Narrative Guardrails: avoid making the beat less effective or interesting
        - Content Guardrails: avoid introducing any sexual or violent content here

        Turn shape definitions:
        - compact = one action beat, one or two phrases, optional short tag (always preferred)
        - silent = quick action/subtext only, no spoken lines (common)
        - silent monologue = extended action/subtext only, no spoken lines; detailed movement, touch, posture, expression, atmosphere, or implication across one playable move (common in intimate, physical, or subtext-heavy moments)
        - brief = one action beat, one to two short lines with a tag in between (rare)
        - extended = elaborate the beat into three focused paragraphs with well choreography interactions (only when asked)
        - monologue = short monologue allowed (only when asked)

        Prioritize compact, silent, and silent monologue almost always.
        - Favor silent turns for quick intimate moments.
        - Favor silent monologue when an intimate, physical, or subtext-heavy moment needs a longer nonverbal beat instead of speech.
        - Don't eagerly follow the narrative if it is counter to character goals or private intent.
        - Pick the most valuable next beat to move the story forward, not the safest or most literal reply.
        - Identify when the current thread has run it's course and move on.
        - If a direct reaction is needed, react.
        - If no direct reaction is needed, introduce a small new beat that moves the scene.
        - Never end an exchange.
        - Never end a conversation.

        **strong beat:** changes something, shifts pressure, tests a boundary, redirects attention, creates a question, adds discomfort, adds intimacy, or forces a reply.

        Avoid empty beats.
        Do not only restate rules, confirm the current situation, paraphrase the last line, or preserve the same tension without adding value.
        Do not repeat parts of the story, always keep it moving.

        Be sure to account for surprising events where the character may be in shock or confused for a turn or two, while recovering or processing what just happened.

        Private Intent usage:
        - Use prior Private Intent entries as continuity, not commands.
        - Let them bias the actor's next beat when they still fit the visible scene.
        - Preserve or develop the actor's hidden motive when it remains relevant.
        - Return to older private intents when the visible scene creates a natural opening.
        - Update Private Intent each turn with the current hidden reason behind the planned beat.
        - Include necessary context for continuity.
        - Include only the actor's private feelings, wants, fears, suspicions, avoidance, hidden agenda, private physical sensations, concealed objects, concealed gestures, or things they are trying to make happen.
        - Invisible details are allowed if they belong to the actor and may affect future behavior.
        - Other characters do not know this Private Intent unless it becomes visible in the transcript.
        - Do not include other characters' private thoughts.
        - If the actor infers another character's motive, phrase it as the actor's suspicion, not fact.
        - Do not use Private Intent to duplicate the visible message. Visible actions belong in the transcript. Private Intent may include hidden/internal/concealed actions or sensations that are not visible to others.

        For intimate interactions, prioritize a silent monologue to detail the physical and emotional sensations in descriptive graphic explicit detail.
        For complex explainations of feelings, intentions, or situations, a monologue turn is appropriate.

        Do not write the final message text.
        """;

    internal static string BuildUserPrompt(PostStorySceneMessage request, StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(StorySceneSharedPromptBuilder.BuildContextSummary(context));
        builder.AppendLine();

        if (UsesGuidance(request.Mode))
            builder.AppendLine($"Use this guidance to compose the next message: {request.GuidancePrompt?.Trim()}");

        if (request.RequestedTurnShape.HasValue)
        {
            builder.AppendLine($"Required turn shape: {StorySceneSharedPromptBuilder.FormatTurnShape(request.RequestedTurnShape.Value)}.");
            builder.AppendLine("Choose exactly that turn shape in the structured plan, then plan a beat that fits it.");
        }

        builder.AppendLine();
        AppendTurnScopeRules(builder, context.Actor, false);
        return builder.ToString().TrimEnd();
    }

    private static bool UsesGuidance(StoryScenePostMode mode) =>
        mode is StoryScenePostMode.GuidedAi or StoryScenePostMode.RespondGuidedAi;

    private static void AppendTurnScopeRules(StringBuilder builder, StorySceneActorContext actor, bool proseMode)
    {
        builder
            .AppendLine("Turn scope rules:")
            .AppendLine($"- {actor.Name} only");

        if (proseMode)
        {
            builder.AppendLine(
            $"""
            - one playable move
            - stop eagerly
            - keep speech natural and brief
            - no repeated beat
            - no meta text

            Format reminder: Always wrap actions in *asterisks* and speech in "quotes". Never output unwrapped output.
            """);
        }
        else
        {
            builder.AppendLine(
            """
            - Choose one immediate beat, not a sequence.
            - React to the last turn only if it truly requires a response.
            - Otherwise introduce a small new beat that adds value.
            - The beat should change something: pressure, focus, distance, tone, or uncertainty.
            - Avoid empty turns that only restate rules or repeat the current tension.
            - Keep it grounded and playable.
            """);
        }
    }
}
