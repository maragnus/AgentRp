using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentRp.Services;

public sealed class ExternalServiceFailureException(
    string message,
    HttpStatusCode statusCode,
    string? responseBody = null,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ResponseBody { get; } = responseBody;
}

public static partial class UserFacingErrorMessageBuilder
{
    private const int MaxDetailLength = 600;

    public static string Build(string fallbackMessage, Exception exception)
    {
        var fallback = EnsureSentence(Sanitize(RemoveTechnicalNoise(fallbackMessage.Trim())));
        var fallbackStem = TrimSentence(fallback);
        var detail = FindBestDetail(exception, fallbackStem);
        if (string.IsNullOrWhiteSpace(detail))
            return fallback;

        detail = EnsureSentence(Sanitize(RemoveTechnicalNoise(detail)));
        if (string.IsNullOrWhiteSpace(detail) || string.Equals(detail, fallback, StringComparison.Ordinal))
            return fallback;

        if (detail.StartsWith(fallbackStem, StringComparison.OrdinalIgnoreCase))
            return detail;

        return $"{fallbackStem}: {detail}";
    }

    public static string BuildExternalHttpFailure(
        string operation,
        HttpStatusCode statusCode,
        string responseBody,
        string? serviceName = null)
    {
        var detail = ExtractResponseDetail(responseBody);
        var service = serviceName ?? InferServiceName(operation, responseBody);
        var status = $"{(int)statusCode} ({statusCode})";

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var message = string.IsNullOrWhiteSpace(service)
                ? $"{operation} failed because the configured API key was rejected"
                : $"{operation} failed because {service} rejected the configured API key";
            return AppendDetail(message, detail);
        }

        if (IsApiKeyDetail(detail))
        {
            var message = string.IsNullOrWhiteSpace(service)
                ? $"{operation} failed because the configured API key was rejected"
                : $"{operation} failed because {service} rejected the configured API key";
            return AppendDetail(message, detail);
        }

        return AppendDetail($"{operation} failed because the service returned {status}", detail);
    }

    public static string Sanitize(string message)
    {
        var sanitized = message.Replace("\r", " ").Replace("\n", " ");
        sanitized = SecretAssignmentRegex().Replace(sanitized, match => $"{match.Groups[1].Value}{match.Groups[2].Value}{RedactSecret(match.Groups[3].Value)}");
        sanitized = CredentialValueRegex().Replace(sanitized, match => $"{match.Groups[1].Value}{RedactSecret(match.Groups[2].Value)}");
        sanitized = BearerTokenRegex().Replace(sanitized, match => $"{match.Groups[1].Value}{RedactSecret(match.Groups[2].Value)}");
        sanitized = LongSecretRegex().Replace(sanitized, match => RedactSecret(match.Value));
        sanitized = WhitespaceRegex().Replace(sanitized, " ").Trim();

        if (sanitized.Length <= MaxDetailLength)
            return sanitized;

        return sanitized[..MaxDetailLength].TrimEnd() + "...";
    }

    private static string? FindBestDetail(Exception exception, string fallbackStem)
    {
        var exceptions = Flatten(exception).ToList();
        var external = exceptions.FirstOrDefault(x => x is ExternalServiceFailureException);
        if (external is not null)
        {
            var message = BuildDetail(external);
            if (!IsLowValueMessage(message, fallbackStem))
                return message;
        }

        foreach (var current in exceptions.AsEnumerable().Reverse())
        {
            var message = BuildDetail(current);
            if (!IsLowValueMessage(message, fallbackStem))
                return message;
        }

        return null;
    }

    private static string BuildDetail(Exception exception) =>
        RemoveTechnicalNoise(exception is ExternalServiceFailureException external
            ? external.Message
            : ExtractExternalDetail(exception.Message) ?? exception.Message);

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            yield return current;
    }

    private static bool IsLowValueMessage(string message, string fallbackStem)
    {
        var trimmed = TrimSentence(message);
        if (string.IsNullOrWhiteSpace(trimmed))
            return true;

        if (string.Equals(trimmed, fallbackStem, StringComparison.OrdinalIgnoreCase))
            return true;

        return trimmed.StartsWith("Exception of type ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractExternalDetail(string message)
    {
        var responseIndex = message.IndexOf("Response:", StringComparison.OrdinalIgnoreCase);
        if (responseIndex < 0)
            return null;

        var responseBody = message[(responseIndex + "Response:".Length)..].Trim();
        return ExtractResponseDetail(responseBody) ?? responseBody;
    }

    private static string? ExtractResponseDetail(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        var trimmed = responseBody.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed;

        try
        {
            var json = JsonNode.Parse(trimmed);
            return ReadJsonString(json, "error", "message")
                ?? ReadJsonString(json, "error")
                ?? ReadJsonString(json, "message")
                ?? ReadJsonString(json, "detail")
                ?? ReadJsonString(json, "details")
                ?? ReadJsonString(json, "title")
                ?? trimmed;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static string? ReadJsonString(JsonNode? node, params string[] path)
    {
        foreach (var segment in path)
        {
            if (node is not JsonObject)
                return null;

            node = node?[segment];
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject or JsonArray => node.ToJsonString(),
            _ => null
        };
    }

    private static string AppendDetail(string message, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return EnsureSentence(Sanitize(message));

        return EnsureSentence(Sanitize($"{TrimSentence(message)}. {detail}"));
    }

    private static string RemoveTechnicalNoise(string message)
    {
        var stackIndex = message.IndexOf(" at ", StringComparison.Ordinal);
        if (stackIndex > 0)
            message = message[..stackIndex];

        return message.Trim();
    }

    private static string EnsureSentence(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        return message[^1] is '.' or '!' or '?' ? message : message + ".";
    }

    private static string TrimSentence(string message) => message.Trim().TrimEnd('.', '!', '?');

    private static bool IsApiKeyDetail(string? detail) =>
        !string.IsNullOrWhiteSpace(detail)
        && (detail.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("token", StringComparison.OrdinalIgnoreCase));

    private static string? InferServiceName(string operation, string responseBody)
    {
        if (operation.Contains("Grok", StringComparison.OrdinalIgnoreCase)
            || operation.Contains("xAI", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("console.x.ai", StringComparison.OrdinalIgnoreCase))
            return "xAI";

        if (operation.Contains("Hugging Face", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("huggingface", StringComparison.OrdinalIgnoreCase))
            return "Hugging Face";

        if (operation.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
            return "OpenAI";

        if (operation.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            || operation.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
            return "Anthropic";

        return null;
    }

    private static string RedactSecret(string secret)
    {
        var trimmed = secret.Trim();
        if (trimmed.Length <= 8)
            return "***";

        return $"{trimmed[..4]}***{trimmed[^4..]}";
    }

    [GeneratedRegex("""(?i)\b(api[-_ ]?key|token|authorization|bearer)(["'\s:=]+)([A-Za-z0-9_\-\.]{12,})""")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("""(?i)\b((?:api[-_ ]?key|token)\b[^:]{0,40}:\s*)([A-Za-z0-9_\-\.]{12,})""")]
    private static partial Regex CredentialValueRegex();

    [GeneratedRegex("""(?i)\b(Bearer\s+)([A-Za-z0-9_\-\.]{12,})""")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("""\b(?:sk-[A-Za-z0-9_\-]{12,}|[A-Za-z0-9_\-]{24,}\.[A-Za-z0-9_\-]{12,}\.[A-Za-z0-9_\-]{12,}|[A-Za-z0-9_\-]{48,})\b""")]
    private static partial Regex LongSecretRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
