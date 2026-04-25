using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using AgentRp.Services;

namespace AgentRp.Tests;

public sealed partial class UserFeedbackServiceTests
{
    [Fact]
    public void ShowBackgroundError_ExceptionOverload_UsesUserFacingFormatter()
    {
        var service = new UserFeedbackService();
        var exception = new InvalidOperationException(
            "Provider rejected token: sk-proj-this-is-a-very-long-secret-value-that-should-not-display.");

        service.ShowBackgroundError(exception, "Testing provider failed.", "Connection Failed");

        var message = Assert.Single(service.Messages);
        Assert.Equal("Connection Failed", message.Title);
        Assert.Contains("Testing provider failed", message.Message);
        Assert.Contains("Provider rejected token:", message.Message);
        Assert.Contains("***", message.Message);
        Assert.DoesNotContain("this-is-a-very-long-secret-value", message.Message);
    }

    [Fact]
    public void ComponentCatchBlocks_DoNotReplaceExceptionReasonsWithGenericMessages()
    {
        var repoRoot = FindRepoRoot();
        var componentRoot = Path.Combine(repoRoot, "AgentRp", "Components");
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentRoot, "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            foreach (var block in ReadCatchBlocks(lines))
            {
                for (var i = block.StartLine; i <= block.EndLine; i++)
                {
                    var line = lines[i];
                    if (GenericErrorAssignmentRegex().IsMatch(line))
                        failures.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1} assigns a generic failure string inside catch; use UserFacingErrorMessageBuilder.Build.");
                }

                var blockText = string.Join('\n', lines[block.StartLine..(block.EndLine + 1)]);
                foreach (Match match in ToastCallRegex().Matches(blockText))
                {
                    var call = match.Value;
                    var normalizedCall = WhitespaceRegex().Replace(call, string.Empty);
                    if (!normalizedCall.Contains("ShowBackgroundError(exception", StringComparison.Ordinal)
                        && !normalizedCall.Contains("ShowBackgroundError(_", StringComparison.Ordinal)
                        && !call.Contains("UserFacingErrorMessageBuilder.Build", StringComparison.Ordinal))
                        failures.Add($"{Path.GetRelativePath(repoRoot, file)}:{block.StartLine + 1} calls ShowBackgroundError inside catch without the formatter or exception overload.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "AgentRp")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Finding the repository root failed.");
    }

    private static IEnumerable<CatchBlock> ReadCatchBlocks(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("catch (Exception exception)", StringComparison.Ordinal))
                continue;

            var start = i;
            var depth = 0;
            var sawOpenBrace = false;
            for (var j = i; j < lines.Length; j++)
            {
                foreach (var character in lines[j])
                {
                    if (character == '{')
                    {
                        depth++;
                        sawOpenBrace = true;
                    }
                    else if (character == '}')
                    {
                        depth--;
                        if (sawOpenBrace && depth == 0)
                        {
                            yield return new CatchBlock(start, j);
                            i = j;
                            goto NextCatch;
                        }
                    }
                }
            }

        NextCatch:;
        }
    }

    private sealed record CatchBlock(int StartLine, int EndLine);

    [GeneratedRegex("_(?:\\w*ErrorMessage|errorMessage)\\s*=\\s*(?:\\$|@|\\$@|@\\$)?\"")]
    private static partial Regex GenericErrorAssignmentRegex();

    [GeneratedRegex("""ShowBackgroundError\s*\((?s:.*?)\);""")]
    private static partial Regex ToastCallRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
