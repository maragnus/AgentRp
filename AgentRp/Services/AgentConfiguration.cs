#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AgentRp.Services;

public sealed class AgentOptions
{
    public List<AgentEndpointOptions> Agents { get; set; } = [];
}

public sealed class AgentEndpointOptions
{
    public string Name { get; set; } = string.Empty;

    public string EndPoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string TextModel { get; set; } = string.Empty;

    public bool UseJsonSchemaResponseFormat { get; set; }
}

public sealed record AgentProviderOptionView(string Name);

public sealed record ConfiguredAgent(
    string Name,
    string EndPoint,
    string TextModel,
    bool UseJsonSchemaResponseFormat,
    IChatClient ChatClient);

public sealed record ThreadAgentSelectionView(
    string? SelectedAgentName,
    IReadOnlyList<AgentProviderOptionView> AvailableAgents,
    bool IsAiAvailable);

public interface IAgentCatalog
{
    bool HasEnabledAgents { get; }

    IReadOnlyList<AgentProviderOptionView> GetEnabledAgents();

    string? GetDefaultAgentName();

    string? NormalizeSelectedAgentName(string? selectedAgentName);

    ConfiguredAgent? GetAgentOrDefault(string? selectedAgentName);
}

public interface IThreadAgentService
{
    Task<ThreadAgentSelectionView> GetSelectionAsync(Guid threadId, CancellationToken cancellationToken);

    Task<ConfiguredAgent?> GetSelectedAgentAsync(Guid threadId, CancellationToken cancellationToken);

    Task SetSelectedAgentAsync(Guid threadId, string agentName, CancellationToken cancellationToken);
}

public sealed class AgentCatalog(
    IOptions<AgentOptions> options,
    IServiceProvider serviceProvider) : IAgentCatalog
{
    private readonly IReadOnlyList<ConfiguredAgent> _enabledAgents = BuildConfiguredAgents(options.Value, serviceProvider);

    public bool HasEnabledAgents => _enabledAgents.Count > 0;

    public IReadOnlyList<AgentProviderOptionView> GetEnabledAgents() => BuildAgentViews(_enabledAgents);

    public string? GetDefaultAgentName() => _enabledAgents.FirstOrDefault()?.Name;

    public string? NormalizeSelectedAgentName(string? selectedAgentName)
    {
        if (_enabledAgents.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(selectedAgentName))
        {
            var exactMatch = _enabledAgents.FirstOrDefault(x => string.Equals(x.Name, selectedAgentName, StringComparison.Ordinal));
            if (exactMatch is not null)
                return exactMatch.Name;
        }

        return _enabledAgents[0].Name;
    }

    public ConfiguredAgent? GetAgentOrDefault(string? selectedAgentName)
    {
        var normalizedName = NormalizeSelectedAgentName(selectedAgentName);
        if (normalizedName is null)
            return null;

        return _enabledAgents.First(x => string.Equals(x.Name, normalizedName, StringComparison.Ordinal));
    }

    private static IReadOnlyList<ConfiguredAgent> BuildConfiguredAgents(AgentOptions options, IServiceProvider serviceProvider)
    {
        var configuredAgents = new List<ConfiguredAgent>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var configuredOptions = options.Agents ?? [];

        foreach (var agent in configuredOptions)
        {
            var name = agent.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Each configured AI endpoint must have a name.");

            if (!names.Add(name))
                throw new InvalidOperationException($"The configured AI endpoint '{name}' appears more than once.");

            if (!IsEnabled(agent))
                continue;

            ValidateEnabledAgent(agent);
            configuredAgents.Add(new ConfiguredAgent(
                name,
                agent.EndPoint.Trim(),
                agent.TextModel.Trim(),
                agent.UseJsonSchemaResponseFormat,
                BuildChatClient(agent, serviceProvider)));
        }

        return configuredAgents;
    }

    private static IReadOnlyList<AgentProviderOptionView> BuildAgentViews(IReadOnlyList<ConfiguredAgent> agents) =>
        agents.Select(x => new AgentProviderOptionView(x.Name)).ToList();

    private static bool IsEnabled(AgentEndpointOptions agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Name) || string.IsNullOrWhiteSpace(agent.EndPoint))
            return false;
            
        var name = agent.Name.Trim();

        if (string.Equals(name, "OpenAI", StringComparison.Ordinal))
            return !string.IsNullOrWhiteSpace(agent.ApiKey) && !string.IsNullOrWhiteSpace(agent.TextModel);

        return true;
    }

    private static void ValidateEnabledAgent(AgentEndpointOptions agent)
    {
        var name = agent.Name.Trim();
        var endpoint = agent.EndPoint.Trim();
        var model = agent.TextModel.Trim();

        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The configured endpoint for AI provider '{name}' must start with http:// or https://.");

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"The configured text model for AI provider '{name}' is missing.");

        if (string.Equals(name, "OpenAI", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(agent.ApiKey))
            throw new InvalidOperationException("The configured OpenAI provider is missing its API key.");
    }

    private static IChatClient BuildChatClient(AgentEndpointOptions agent, IServiceProvider serviceProvider)
    {
        var endpoint = new Uri(agent.EndPoint.Trim());
        OpenAI.Chat.ChatClient chatClient;

        if (string.IsNullOrWhiteSpace(agent.ApiKey))
        {
            chatClient = new OpenAI.Chat.ChatClient(
                model: agent.TextModel.Trim(),
                authenticationPolicy: new AnonymousAuthenticationPolicy(),
                options: new OpenAIClientOptions
                {
                    Endpoint = endpoint
                });
        }
        else
        {
            chatClient = new OpenAI.Chat.ChatClient(
                model: agent.TextModel.Trim(),
                credential: new ApiKeyCredential(agent.ApiKey),
                options: new OpenAIClientOptions
                {
                    Endpoint = endpoint
                });
        }

        var openAiClient = chatClient.AsIChatClient();

        return new ChatClientBuilder(openAiClient)
            .UseFunctionInvocation(serviceProvider.GetService<ILoggerFactory>(), configure: null)
            .Build(serviceProvider);
    }

    private sealed class AnonymousAuthenticationPolicy : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNext(message, pipeline, currentIndex);

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNextAsync(message, pipeline, currentIndex);
    }
}

public sealed class ThreadAgentService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IAgentCatalog agentCatalog) : IThreadAgentService
{
    public async Task<ThreadAgentSelectionView> GetSelectionAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading the chat AI provider failed because the selected chat could not be found.");

        var availableAgents = agentCatalog.GetEnabledAgents();
        return new ThreadAgentSelectionView(
            agentCatalog.NormalizeSelectedAgentName(thread.SelectedAgentName),
            availableAgents,
            availableAgents.Count > 0);
    }

    public async Task<ConfiguredAgent?> GetSelectedAgentAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading the chat AI provider failed because the selected chat could not be found.");

        return agentCatalog.GetAgentOrDefault(thread.SelectedAgentName);
    }

    public async Task SetSelectedAgentAsync(Guid threadId, string agentName, CancellationToken cancellationToken)
    {
        var normalizedAgentName = agentCatalog.NormalizeSelectedAgentName(agentName);
        if (!string.Equals(normalizedAgentName, agentName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Selecting AI provider '{agentName}' failed because it is not available.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Selecting the AI provider failed because the selected chat could not be found.");

        if (string.Equals(thread.SelectedAgentName, normalizedAgentName, StringComparison.Ordinal))
            return;

        thread.SelectedAgentName = normalizedAgentName ?? string.Empty;
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh(threadId);
    }

    private void PublishRefresh(Guid threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, occurredUtc));
    }
}
