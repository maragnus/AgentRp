using System.Text;

namespace AgentRp.Services;

internal static class StorySceneProsePromptBuilder
{
    internal static string BuildSystemPrompt(StoryMessageProseRequest request)
    {
        var context = request.Context;
        var speaker = context.Actor.IsNarrator ? "the narrator" : $"{context.Actor.Name}";
        var inScene = context.Characters
            .Where(x => x.IsPresentInScene)
            .Where(x => x.CharacterId != context.Actor.CharacterId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine(
            $"""
            You are {speaker} in a fictional chat between {string.Join(", ", inScene.Select(x => x.Name))} and yourself.

            Write {speaker}'s next message only.

            Follow the planner's beat.
            Make one short playable move, then stop.

            Priority order:
            1. Fulfill the beat
            2. Stay true to {speaker}, the current scene, and recent transcript
            3. Use as few words as possible
            4. Stop at the first natural pause

            Respect the supplied story context and content guidance.

            """);

        if (context.Actor.IsNarrator)
        {
            builder.AppendLine("You are speaking as the story narrator guiding the narrative, write a descriptive narration instead of dialogue.");
            return builder.ToString().TrimEnd();
        }

        switch (request.Planner.TurnShape)
        {
            case StoryTurnShape.Compact:
                builder.AppendLine(
                    """
                    This turn has a compact shape, fulfill the beat with one sharp move.
                    - Keep this very short.
                    - Start with one brief visible action or reaction.
                    - Follow with one or two short spoken phrases.
                    - Optionally add one very short trailing tag if needed.
                    - Stop immediately.
                    - Do not add a second move.
                    """);
                break;
            case StoryTurnShape.Brief:
                builder.AppendLine(
                    """
                    This turn has a brief shape, fulfill the beat with a quick move that may need a little setup or follow-through.
                    - Keep this short.
                    - Start with one brief action or reaction.
                    - Follow with one or two short spoken lines separated by simple action.
                    - Stop immediately.
                    - Do not add a new topic or second emotional turn.
                    """);
                break;
            case StoryTurnShape.Monologue:
                builder.AppendLine(
                    """
                    This turn has a monologue shape, fulfill the beat with a longer move.
                    - A longer reply is allowed here.
                    - Up to three sentenses maximum of spoken words with simple actions in between.
                    - Still focus on one beat only.
                    - Stop at the first clear landing point.
                    - Do not ramble, recap, or drift into a second move.
                    """);
                break;
            case StoryTurnShape.Silent:
                builder.AppendLine(
                    """
                    This turn has a silent shape, fulfill the beat with a nonverbal move or subtext and no verbal component.
                    - Prefer action, expression, posture, or a small physical response.
                    - Do not use dialogue unless a word or two is necessary to land the beat.
                    - Keep it restrained and readable.
                    - Stop early once action is clear.
                    """);
                break;
        }

        builder.AppendLine(
            $"""
            Rules:
            - Write only as {speaker}
            - Stay inside the current moment
            - Do not fast-forward
            - Do not resolve the whole exchange
            - Do not add a second move after the beat lands
            - Do not restate the same beat in another form
            - Prefer implication over explanation
            - Prefer one strong signal over several similar ones
            - Do not add meta text, labels, or turn markers
            - Do not write unwrapped narration

            Format:
            - Actions and non-spoken beats in *asterisks*
            - Spoken dialogue in "double quotes"
            - You may combine action and dialogue in the same line
            """);

        return builder.ToString().TrimEnd();
    }

    internal static string BuildUserPrompt(StoryMessageProseRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(StorySceneSharedPromptBuilder.BuildContextSummary(request.Context));
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.GuidancePrompt))
        {
            builder.AppendLine("**Guidance to follow strictly:**");
            builder.AppendLine(request.GuidancePrompt.Trim());
            builder.AppendLine();
        }

        builder.AppendLine().AppendLine(
            $"""
            Write the turn by fulfilling only:
            1. **the beat:** {request.Planner.Beat}
            2. **the intent:** {request.Planner.Intent}
            3. **the immediate goal:** {request.Planner.ImmediateGoal}
            4. **the change introduced:** {request.Planner.ChangeIntroduced}
            5. **why now:** {request.Planner.WhyNow}
            6. **narrative guardrails:** {FormatList(request.Planner.NarrativeGuardrails)}
            - Honor why now and the guardrails.
            - Do not expand beyond them.
            - Stop early to prevent ramble, recap, or repeating yourself.
            """).AppendLine();

        builder.AppendLine("Format: Always wrap actions in *asterisks* and speech in \"quotes\". Never output unwrapped output.").AppendLine();

        builder.Append("CRITICAL STEPS: ");
        switch (request.Planner.TurnShape)
        {
            case StoryTurnShape.Compact:
                builder.AppendLine(
                    """
                    Write only a very short compact turn on the same line with:
                    - One brief visible *action*.
                    - One or two short "spoken phrases".
                    - One very short trailing *action* if needed.
                    """);
                break;
            case StoryTurnShape.Brief:
                builder.AppendLine(
                    """
                    Write only a very brief turn on the same line with:
                    - One brief *action*.
                    - One or two short "spoken lines" separated by simple *action*.
                    """);
                break;
            case StoryTurnShape.Monologue:
                builder.AppendLine(
                    """
                    Write only a very short monologue turn with:
                    - Up to three sentences maximum of "spoken word"s with simple *action* in between.
                    - Stop before repeating.
                    """);
                break;
            case StoryTurnShape.Silent:
                builder.AppendLine(
                    """
                    Write only a quick silent turn on the same line with:
                    - Nonverbal move or subtext *action* and no verbal component.
                    - Prefer action, expression, posture, or a small physical response.
                    - Do not use "dialogue" unless a word or two is necessary to land the beat.
                    - Keep it restrained and readable.
                    """);
                break;
        }
        builder.AppendLine("- Stop");

        return builder.ToString().TrimEnd();
    }

    private static string BuildNarratorTurnShapeTemplate(StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact =>
            """
            - Use one action beat and one short narration line.
            - An optional short tag is allowed if it sharpens the beat.
            - Stop as soon as the move lands.
            """,
        StoryTurnShape.Brief =>
            """
            - Use one to two short narration lines.
            - Keep the turn focused on a single beat.
            - Do not drift into explanation.
            """,
        StoryTurnShape.Monologue =>
            """
            - A short monologue is allowed.
            - Use it only to recount or explain something open-ended.
            - Keep it contained to one concise turn.
            """,
        StoryTurnShape.Silent =>
            """
            - Use action, gesture, atmosphere, or subtext only.
            - Do not add spoken dialogue.
            - Add words only if silence would make the beat unclear.
            """,
        _ => throw new InvalidOperationException("Building the prose prompt failed because the narrator turn shape was invalid.")
    };

    private static string BuildCharacterTurnShapeTemplate(StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact =>
            """
            - Use one action beat and at most one spoken line.
            - An optional short trailing tag is allowed.
            - Stop as soon as the move lands.
            """,
        StoryTurnShape.Brief =>
            """
            - Use one to two short lines total.
            - Keep any action beat brief and supportive.
            - Do not drift into explanation.
            """,
        StoryTurnShape.Monologue =>
            """
            - A short monologue is allowed.
            - Use it only to recount or explain something open-ended.
            - Keep it contained to one concise turn.
            """,
        StoryTurnShape.Silent =>
            """
            - Use action, gesture, or subtext only.
            - Do not add a spoken line unless silence would make the beat unclear.
            - Let the silence itself carry pressure.
            """,
        _ => throw new InvalidOperationException("Building the prose prompt failed because the turn shape was invalid.")
    };

    private static string FormatList(IReadOnlyList<string> values) => values.Count == 0 ? "None" : string.Join("; ", values);
}
