using Markdig;

namespace AgentRp.Services;

public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public string Render(string markdown) => Markdown.ToHtml(markdown?.Trim() ?? string.Empty, _pipeline);
}
