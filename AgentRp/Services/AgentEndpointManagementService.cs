using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentRp.Services;

public sealed class AgentEndpointManagementService(
    IAgentCatalog agentCatalog,
    IHttpClientFactory httpClientFactory,
    ILogger<AgentEndpointManagementService> logger) : IAgentEndpointManagementService
{
    private static readonly Uri HuggingFaceWhoAmIUri = new("https://huggingface.co/api/whoami-v2");
    private static readonly Uri HuggingFaceManagementBaseUri = new("https://api.endpoints.huggingface.cloud/v2/");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> _namespaceCache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<AgentEndpointStatusView>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var huggingFaceAgents = agentCatalog.GetConfiguredAgents()
            .Where(x => x.ProviderKind == AgentProviderKind.HuggingFaceInferenceEndpoint)
            .ToList();
        if (huggingFaceAgents.Count == 0)
            return [];

        var endpointsByToken = new Dictionary<string, IReadOnlyDictionary<string, ManagedEndpoint>>(StringComparer.Ordinal);
        var authenticationFailedTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var apiKeyGroup in huggingFaceAgents.GroupBy(x => x.ApiKey, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(apiKeyGroup.Key) || endpointsByToken.ContainsKey(apiKeyGroup.Key) || authenticationFailedTokens.Contains(apiKeyGroup.Key))
                continue;

            try
            {
                endpointsByToken[apiKeyGroup.Key] = await GetEndpointsByUrlAsync(apiKeyGroup.Key, cancellationToken);
            }
            catch (HuggingFaceAuthenticationException exception)
            {
                logger.LogWarning(exception, "Loading Hugging Face endpoint status failed because the configured API key could not access endpoint management.");
                authenticationFailedTokens.Add(apiKeyGroup.Key);
            }
        }

        var statuses = new List<AgentEndpointStatusView>(huggingFaceAgents.Count);
        foreach (var agent in huggingFaceAgents)
        {
            if (string.IsNullOrWhiteSpace(agent.ApiKey))
            {
                statuses.Add(BuildMissingApiKeyStatus(agent));
                continue;
            }

            if (authenticationFailedTokens.Contains(agent.ApiKey))
            {
                statuses.Add(BuildAuthenticationFailedStatus(agent));
                continue;
            }

            statuses.Add(BuildStatus(agent, endpointsByToken[agent.ApiKey]));
        }

        return statuses;
    }

    public async Task<AgentEndpointStatusView> ExecuteActionAsync(string agentName, ManagedAgentAction action, CancellationToken cancellationToken)
    {
        var agent = agentCatalog.GetConfiguredAgents()
            .FirstOrDefault(x => string.Equals(x.Name, agentName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Managing the Hugging Face endpoint failed because the configured agent '{agentName}' could not be found.");
        if (agent.ProviderKind != AgentProviderKind.HuggingFaceInferenceEndpoint)
            throw new InvalidOperationException($"Managing the Hugging Face endpoint failed because '{agentName}' is not a Hugging Face-managed endpoint.");
        if (string.IsNullOrWhiteSpace(agent.ApiKey))
            throw new InvalidOperationException($"Managing the Hugging Face endpoint for '{agentName}' failed because the configured API key is missing.");

        try
        {
            var endpoint = await GetLinkedEndpointAsync(agent, cancellationToken);
            ValidateAction(agent.Name, action, endpoint);

            var relativePath = action switch
            {
                ManagedAgentAction.Start => $"endpoint/{Uri.EscapeDataString(endpoint.Namespace)}/{Uri.EscapeDataString(endpoint.Endpoint.Name)}/resume",
                ManagedAgentAction.Pause => $"endpoint/{Uri.EscapeDataString(endpoint.Namespace)}/{Uri.EscapeDataString(endpoint.Endpoint.Name)}/pause",
                ManagedAgentAction.ScaleToZero => $"endpoint/{Uri.EscapeDataString(endpoint.Namespace)}/{Uri.EscapeDataString(endpoint.Endpoint.Name)}/scale-to-zero",
                _ => throw new InvalidOperationException($"Managing the Hugging Face endpoint for '{agent.Name}' failed because the action '{action}' is not supported.")
            };

            using var client = CreateHuggingFaceClient(agent.ApiKey);
            using var response = await client.PostAsync(new Uri(HuggingFaceManagementBaseUri, relativePath), content: null, cancellationToken);
            var updatedEndpoint = await ReadJsonResponseAsync<EndpointWithStatusResponse>(response, cancellationToken);
            return BuildLinkedStatus(agent, new ManagedEndpoint(endpoint.Namespace, updatedEndpoint));
        }
        catch (HuggingFaceAuthenticationException exception)
        {
            throw new InvalidOperationException(
                $"Managing the Hugging Face endpoint for '{agent.Name}' failed because the configured API key could not access Hugging Face endpoint management.",
                exception);
        }
    }

    private async Task<Dictionary<string, ManagedEndpoint>> GetEndpointsByUrlAsync(string apiKey, CancellationToken cancellationToken)
    {
        var namespaces = await GetNamespacesAsync(apiKey, cancellationToken);
        var endpointsByUrl = new Dictionary<string, ManagedEndpoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var @namespace in namespaces)
        {
            var cursor = string.Empty;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);

            do
            {
                var page = await ListEndpointsAsync(apiKey, @namespace, cursor, cancellationToken);
                foreach (var endpoint in page.Items)
                {
                    var normalizedUrl = AgentEndpointUrlNormalizer.Normalize(endpoint.Status.Url);
                    if (normalizedUrl is null)
                        continue;

                    endpointsByUrl[normalizedUrl] = new ManagedEndpoint(@namespace, endpoint);
                }

                cursor = string.IsNullOrWhiteSpace(page.NextCursor) || !seenCursors.Add(page.NextCursor)
                    ? string.Empty
                    : page.NextCursor;
            }
            while (!string.IsNullOrWhiteSpace(cursor));
        }

        return endpointsByUrl;
    }

    private async Task<IReadOnlyList<string>> GetNamespacesAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _namespaceCache.GetOrAdd(apiKey, key => LoadNamespacesAsync(key, cancellationToken));
        }
        catch
        {
            _namespaceCache.TryRemove(apiKey, out _);
            throw;
        }
    }

    private async Task<IReadOnlyList<string>> LoadNamespacesAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var client = CreateHuggingFaceClient(apiKey);
        using var response = await client.GetAsync(HuggingFaceWhoAmIUri, cancellationToken);
        var whoAmI = await ReadJsonResponseAsync<HuggingFaceWhoAmIResponse>(response, cancellationToken);

        var namespaces = new List<string>();
        AddNamespace(namespaces, whoAmI.Name);
        foreach (var organization in whoAmI.Orgs)
            AddNamespace(namespaces, organization.Name);

        return namespaces;
    }

    private async Task<EndpointListResponse> ListEndpointsAsync(string apiKey, string @namespace, string? cursor, CancellationToken cancellationToken)
    {
        using var client = CreateHuggingFaceClient(apiKey);
        var requestUri = BuildEndpointListUri(@namespace, cursor);
        using var response = await client.GetAsync(requestUri, cancellationToken);
        return await ReadJsonResponseAsync<EndpointListResponse>(response, cancellationToken);
    }

    private async Task<ManagedEndpoint> GetLinkedEndpointAsync(ConfiguredAgent agent, CancellationToken cancellationToken)
    {
        var endpointsByUrl = await GetEndpointsByUrlAsync(agent.ApiKey, cancellationToken);
        var normalizedUrl = AgentEndpointUrlNormalizer.Normalize(agent.EndPoint);
        if (normalizedUrl is null || !endpointsByUrl.TryGetValue(normalizedUrl, out var endpoint))
            throw new InvalidOperationException($"Managing the Hugging Face endpoint for '{agent.Name}' failed because no matching managed endpoint was found for the configured URL.");

        return endpoint;
    }

    private static Uri BuildEndpointListUri(string @namespace, string? cursor)
    {
        var relativePath = $"endpoint/{Uri.EscapeDataString(@namespace)}?limit=100";
        if (!string.IsNullOrWhiteSpace(cursor))
            relativePath += $"&cursor={Uri.EscapeDataString(cursor)}";

        return new Uri(HuggingFaceManagementBaseUri, relativePath);
    }

    private static void AddNamespace(List<string> namespaces, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || namespaces.Any(x => string.Equals(x, candidate, StringComparison.Ordinal)))
            return;

        namespaces.Add(candidate);
    }

    private HttpClient CreateHuggingFaceClient(string apiKey)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static AgentEndpointStatusView BuildStatus(ConfiguredAgent agent, IReadOnlyDictionary<string, ManagedEndpoint> endpointsByUrl)
    {
        var normalizedUrl = AgentEndpointUrlNormalizer.Normalize(agent.EndPoint);
        if (normalizedUrl is null || !endpointsByUrl.TryGetValue(normalizedUrl, out var endpoint))
            return new AgentEndpointStatusView(
                agent.Name,
                agent.ProviderKind,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                "No matching Hugging Face endpoint was found for the configured URL.");

        return BuildLinkedStatus(agent, endpoint);
    }

    private static AgentEndpointStatusView BuildLinkedStatus(ConfiguredAgent agent, ManagedEndpoint endpoint)
    {
        var state = endpoint.Endpoint.Status.State?.Trim();
        return new AgentEndpointStatusView(
            agent.Name,
            agent.ProviderKind,
            true,
            state,
            string.IsNullOrWhiteSpace(endpoint.Endpoint.Status.Message) ? null : endpoint.Endpoint.Status.Message.Trim(),
            endpoint.Endpoint.Status.UpdatedAt,
            string.IsNullOrWhiteSpace(endpoint.Endpoint.Model.Repository) ? null : endpoint.Endpoint.Model.Repository.Trim(),
            endpoint.Namespace,
            endpoint.Endpoint.Name,
            CanStart(state),
            CanPause(state),
            CanScaleToZero(state),
            null);
    }

    private static AgentEndpointStatusView BuildMissingApiKeyStatus(ConfiguredAgent agent) =>
        new(
            agent.Name,
            agent.ProviderKind,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            "Add an API key to link and manage this Hugging Face endpoint.");

    private static AgentEndpointStatusView BuildAuthenticationFailedStatus(ConfiguredAgent agent) =>
        new(
            agent.Name,
            agent.ProviderKind,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            "The configured API key could not access Hugging Face endpoint management.");

    private static bool CanStart(string? state) =>
        state is not null && (state.Equals("paused", StringComparison.OrdinalIgnoreCase) || state.Equals("scaledToZero", StringComparison.OrdinalIgnoreCase));

    private static bool CanPause(string? state) =>
        IsActiveState(state);

    private static bool CanScaleToZero(string? state) =>
        IsActiveState(state);

    private static bool IsActiveState(string? state) =>
        state is not null
        && (state.Equals("running", StringComparison.OrdinalIgnoreCase)
            || state.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || state.Equals("initializing", StringComparison.OrdinalIgnoreCase)
            || state.Equals("updating", StringComparison.OrdinalIgnoreCase));

    private static void ValidateAction(string agentName, ManagedAgentAction action, ManagedEndpoint endpoint)
    {
        var state = endpoint.Endpoint.Status.State?.Trim();
        var isAllowed = action switch
        {
            ManagedAgentAction.Start => CanStart(state),
            ManagedAgentAction.Pause => CanPause(state),
            ManagedAgentAction.ScaleToZero => CanScaleToZero(state),
            _ => false
        };
        if (isAllowed)
            return;

        throw new InvalidOperationException(
            $"{DescribeAction(action)} the Hugging Face endpoint for '{agentName}' failed because it is currently '{state ?? "unknown"}'.");
    }

    private static string DescribeAction(ManagedAgentAction action) =>
        action switch
        {
            ManagedAgentAction.Start => "Starting",
            ManagedAgentAction.Pause => "Pausing",
            ManagedAgentAction.ScaleToZero => "Scaling to zero",
            _ => "Managing"
        };

    private static async Task<T> ReadJsonResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions, cancellationToken)
                ?? throw new InvalidOperationException("Reading the Hugging Face endpoint management response failed because the service returned an empty response body.");

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HuggingFaceAuthenticationException(response.StatusCode, responseBody);

        throw new InvalidOperationException(
            $"Calling Hugging Face endpoint management failed with status code {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
    }

    private sealed record ManagedEndpoint(
        string Namespace,
        EndpointWithStatusResponse Endpoint);

    private sealed class HuggingFaceAuthenticationException(HttpStatusCode statusCode, string? responseBody)
        : InvalidOperationException($"Hugging Face endpoint management authentication failed with status code {(int)statusCode} ({statusCode}). Response: {responseBody}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }

    private sealed class HuggingFaceWhoAmIResponse
    {
        public string Name { get; set; } = string.Empty;

        public List<HuggingFaceOrganizationResponse> Orgs { get; set; } = [];
    }

    private sealed class HuggingFaceOrganizationResponse
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class EndpointListResponse
    {
        public List<EndpointWithStatusResponse> Items { get; set; } = [];

        public string? NextCursor { get; set; }
    }

    private sealed class EndpointWithStatusResponse
    {
        public string Name { get; set; } = string.Empty;

        public EndpointModelResponse Model { get; set; } = new();

        public EndpointStatusResponse Status { get; set; } = new();
    }

    private sealed class EndpointModelResponse
    {
        public string Repository { get; set; } = string.Empty;
    }

    private sealed class EndpointStatusResponse
    {
        public string? State { get; set; }

        public string? Message { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public string? Url { get; set; }
    }
}

internal static class AgentEndpointUrlNormalizer
{
    public static string? Normalize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var trimmedEndpoint = endpoint.Trim();
        if (!trimmedEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !trimmedEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return null;

        var endpointUri = new Uri(trimmedEndpoint);
        var path = endpointUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            path = path[..^3].TrimEnd('/');

        return $"{endpointUri.Scheme}://{endpointUri.Authority}{path}".TrimEnd('/').ToLowerInvariant();
    }
}
