using AgentRp.Data;

namespace AgentRp.Services;

public sealed class AgentTurnComposer : IAgentTurnComposer
{
    public Task<ComposedAgentTurn> ComposeAsync(string prompt, bool isBranch, CancellationToken cancellationToken)
    {
        var normalizedPrompt = prompt.Trim();
        var subject = BuildSubject(normalizedPrompt);
        var focusLine = isBranch
            ? $"This branch explores an alternative take on {subject}."
            : $"The response focuses on {subject}.";

        var assistantMarkdown = $$"""
        ### Direct answer
        I understood your request as **{{subject}}** and prepared a concise response path for it.

        ### Working direction
        - {{focusLine}}
        - I kept the response generic so the app shell can support many kinds of agent tasks.
        - The transcript now preserves branch history, so alternate edits can live beside the original path.

        ### Next useful step
        Once the real agent runtime is wired in, this same workspace can replace the draft answer with live model output while keeping the branch and process UI intact.
        """;

        var steps = new List<ComposedProcessStep>
        {
            new(
                "Understand the request",
                $"Clarified the user goal around {subject}.",
                $"Parsed the latest prompt and identified the main intent: {normalizedPrompt}",
                "fa-regular fa-magnifying-glass",
                ProcessStepStatus.Completed),
            new(
                isBranch ? "Choose the alternate branch" : "Plan the response path",
                isBranch
                    ? "Reused the earlier turn as a branching point and built a fresh continuation."
                    : "Outlined the simplest response shape that still preserves agentic affordances.",
                isBranch
                    ? "The edit-and-resubmit flow creates a sibling branch from the original user turn instead of overwriting history."
                    : "The answer plan emphasizes a reusable chat shell, transcript continuity, and a visible reasoning trail.",
                "fa-regular fa-code-branch",
                ProcessStepStatus.Completed),
            new(
                "Draft the answer",
                "Prepared the assistant response and attached it to the completed process run.",
                "The final assistant message is intentionally lightweight and markdown-friendly so the UI can render rich content without extra chrome.",
                "fa-regular fa-pen-line",
                ProcessStepStatus.Completed)
        };

        var summary = isBranch
            ? "Reviewed the earlier turn, chose a branch point, and drafted an alternate answer."
            : "Reviewed the prompt, planned a response, and drafted the assistant answer.";

        return Task.FromResult(new ComposedAgentTurn(assistantMarkdown, summary, steps));
    }

    private static string BuildSubject(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "the latest request";

        var firstLine = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
            return "the latest request";

        return firstLine.Length <= 72
            ? firstLine.TrimEnd('.', '!', '?')
            : $"{firstLine[..69].TrimEnd()}...";
    }
}
