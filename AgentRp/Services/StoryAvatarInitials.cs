using System.Text;

namespace AgentRp.Services;

public static class StoryAvatarInitials
{
    public static string Build(string? name)
    {
        var words = (name ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToList();
        if (words.Count == 0)
            return "?";

        if (words.Count > 1)
            return $"{FirstTextElement(words[0])}{FirstTextElement(words[^1])}".ToUpperInvariant();

        var word = words[0];
        return string.Concat(word.EnumerateRunes().Take(2).Select(x => x.ToString())).ToUpperInvariant();
    }

    private static string FirstTextElement(string value) =>
        value.EnumerateRunes().FirstOrDefault().ToString();
}
