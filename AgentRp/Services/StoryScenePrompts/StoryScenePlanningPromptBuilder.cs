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
        - Turn shape: choose exactly one of compact, brief, monologue, or silent.
        - Beat: the kind of move being made in this turn.
        - Intent: the actor's immediate intention.
        - Immediate goal: what this turn tries to achieve right now.
        - Why now: why this beat fits this exact moment in the transcript.
        - Change introduced: what becomes different after this turn.
        - Narrative Guardrails: avoid making the beat less effective or interesting
        - Content Guardrails: avoid introducing any sexual or violent content here

        Turn shape definitions:
        - compact = one action beat, one or two phrases, optional short tag (always preferred)
        - silent = action/subtext only, no spoken lines (common)
        - brief = one action beat, one to two short lines with a tag in between (rare)
        - monologue = short monologue allowed (only when asked)

        Prioritize compact and silent almost always.
        Use brief or monologue only when the turn naturally needs recounting or explanation for an open-ended prompt such as "how was your day".

        Pick the most valuable next beat, not the safest or most literal reply. But eagerly follow the narrative.
        If a direct reaction is needed, react.
        If no direct reaction is needed, introduce a small new beat that moves the scene.

        A strong beat changes something.
        It may shift pressure, test a boundary, redirect attention, create a question, add discomfort, add intimacy, or force a reply.

        Avoid empty beats.
        Do not only restate rules, confirm the current situation, paraphrase the last line, or preserve the same tension without adding value.

        Keep the beat playable and local.
        Do not fast-forward.
        Do not resolve the whole exchange.
        Do not plan follow-up beats.
        Stop where the next person would naturally answer or act.

        Respect the supplied story context and content guidance.
        If content is forbidden, do not plan beats that introduce it.
        If content is encouraged, you may lean into it when the current scene supports it.

        Do not write the final message text.
        """;

    internal static string BuildUserPrompt(PostStorySceneMessage request, StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(StorySceneSharedPromptBuilder.BuildContextSummary(context));
        builder.AppendLine();

        if (UsesGuidance(request.Mode))
            builder.AppendLine($"Use this guidance to compose the next message: {request.GuidancePrompt?.Trim()}");

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
