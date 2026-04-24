#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using AgentRp.Data;
using Anthropic;
using Anthropic.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Services;

public sealed record AgentProviderOptionView(
    Guid ModelId,
    string Name,
    string ProviderName,
    AiProviderKind ProviderKind);

public sealed record ConfiguredAgent(
    Guid ModelId,
    string Name,
    string ProviderName,
    string EndPoint,
    string ApiKey,
    string TextModel,
    AiProviderKind ProviderKind,
    bool UseJsonSchemaResponseFormat,
    IChatClient ChatClient);

public sealed record ThreadAgentSelectionView(
    string? SelectedAgentName,
    Guid? SelectedModelId,
    IReadOnlyList<AgentProviderOptionView> AvailableAgents,
    bool IsAiAvailable);

public interface IAgentCatalog
{
    bool HasEnabledAgents { get; }

    IReadOnlyList<AgentProviderOptionView> GetEnabledAgents();

    IReadOnlyList<ConfiguredAgent> GetConfiguredAgents();

    Guid? GetDefaultModelId();

    string? GetDefaultAgentName();

    Guid? NormalizeSelectedModelId(Guid? selectedModelId);

    string? NormalizeSelectedAgentName(string? selectedAgentName);

    ConfiguredAgent? GetAgentOrDefault(Guid? selectedModelId);

    ConfiguredAgent? GetAgentOrDefault(string? selectedAgentName);
}

public interface IThreadAgentService
{
    Task<ThreadAgentSelectionView> GetSelectionAsync(Guid threadId, CancellationToken cancellationToken);

    Task<ConfiguredAgent?> GetSelectedAgentAsync(Guid threadId, CancellationToken cancellationToken);

    Task SetSelectedAgentAsync(Guid threadId, Guid modelId, CancellationToken cancellationToken);

    Task SetSelectedAgentAsync(Guid threadId, string agentName, CancellationToken cancellationToken);
}

public sealed class AgentCatalog(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IServiceProvider serviceProvider) : IAgentCatalog
{
    public bool HasEnabledAgents => GetEnabledModelRows().Count > 0;

    public IReadOnlyList<AgentProviderOptionView> GetEnabledAgents() =>
        GetEnabledModelRows()
            .Select(x => new AgentProviderOptionView(x.ModelId, x.Name, x.ProviderName, x.ProviderKind))
            .ToList();

    public IReadOnlyList<ConfiguredAgent> GetConfiguredAgents() =>
        GetEnabledModelRows()
            .Select(BuildConfiguredAgent)
            .ToList();

    public Guid? GetDefaultModelId() => GetEnabledModelRows().FirstOrDefault()?.ModelId;

    public string? GetDefaultAgentName() => GetEnabledModelRows().FirstOrDefault()?.Name;

    public Guid? NormalizeSelectedModelId(Guid? selectedModelId)
    {
        var models = GetEnabledModelRows();
        if (models.Count == 0)
            return null;

        if (selectedModelId.HasValue && models.Any(x => x.ModelId == selectedModelId.Value))
            return selectedModelId.Value;

        return models[0].ModelId;
    }

    public string? NormalizeSelectedAgentName(string? selectedAgentName)
    {
        var models = GetEnabledModelRows();
        if (models.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(selectedAgentName))
        {
            var exactMatch = models.FirstOrDefault(x => string.Equals(x.Name, selectedAgentName, StringComparison.Ordinal));
            if (exactMatch is not null)
                return exactMatch.Name;
        }

        return models[0].Name;
    }

    public ConfiguredAgent? GetAgentOrDefault(Guid? selectedModelId)
    {
        var normalizedModelId = NormalizeSelectedModelId(selectedModelId);
        if (!normalizedModelId.HasValue)
            return null;

        return BuildConfiguredAgent(GetEnabledModelRows().First(x => x.ModelId == normalizedModelId.Value));
    }

    public ConfiguredAgent? GetAgentOrDefault(string? selectedAgentName)
    {
        var models = GetEnabledModelRows();
        if (models.Count == 0)
            return null;

        var model = !string.IsNullOrWhiteSpace(selectedAgentName)
            ? models.FirstOrDefault(x => string.Equals(x.Name, selectedAgentName, StringComparison.Ordinal))
            : null;

        return BuildConfiguredAgent(model ?? models[0]);
    }

    private IReadOnlyList<EnabledModelRow> GetEnabledModelRows()
    {
        using var dbContext = dbContextFactory.CreateDbContext();
        var rows = dbContext.AiModels
            .AsNoTracking()
            .Include(x => x.Provider)
            .Where(x => x.IsEnabled && x.Provider.IsEnabled)
            .Select(x => new EnabledModelRow(
                x.Id,
                x.DisplayName,
                x.Provider.Name,
                x.Provider.ProviderKind,
                x.Provider.SortOrder,
                x.SortOrder,
                x.Endpoint ?? x.Provider.BaseEndpoint,
                x.Provider.ApiKey,
                x.ProviderModelId,
                x.UseJsonSchemaResponseFormat))
            .ToList();

        return rows
            .OrderBy(x => x.ProviderSortOrder)
            .ThenBy(x => x.ProviderName)
            .ThenBy(x => x, EnabledModelRowComparer.Instance)
            .ToList();
    }

    private ConfiguredAgent BuildConfiguredAgent(EnabledModelRow model)
    {
        ValidateEnabledModel(model);
        return new ConfiguredAgent(
            model.ModelId,
            model.Name,
            model.ProviderName,
            model.Endpoint,
            model.ApiKey,
            model.ProviderModelId,
            model.ProviderKind,
            model.UseJsonSchemaResponseFormat,
            BuildChatClient(model));
    }

    private IChatClient BuildChatClient(EnabledModelRow model)
    {
        var client = model.ProviderKind == AiProviderKind.Claude
            ? BuildClaudeChatClient(model)
            : BuildOpenAiCompatibleChatClient(model);

        return new ChatClientBuilder(client)
            .UseFunctionInvocation(serviceProvider.GetService<ILoggerFactory>(), configure: null)
            .Build(serviceProvider);
    }

    private static IChatClient BuildClaudeChatClient(EnabledModelRow model)
    {
        var client = new AnthropicClient(new ClientOptions { ApiKey = model.ApiKey });
        return client.AsIChatClient(model.ProviderModelId);
    }

    private static IChatClient BuildOpenAiCompatibleChatClient(EnabledModelRow model)
    {
        var endpoint = new Uri(model.Endpoint);
        OpenAI.Chat.ChatClient chatClient;

        if (string.IsNullOrWhiteSpace(model.ApiKey))
        {
            chatClient = new OpenAI.Chat.ChatClient(
                model: model.ProviderModelId,
                authenticationPolicy: new AnonymousAuthenticationPolicy(),
                options: new OpenAIClientOptions
                {
                    Endpoint = endpoint
                });
        }
        else
        {
            chatClient = new OpenAI.Chat.ChatClient(
                model: model.ProviderModelId,
                credential: new ApiKeyCredential(model.ApiKey),
                options: new OpenAIClientOptions
                {
                    Endpoint = endpoint
                });
        }

        return chatClient.AsIChatClient();
    }

    private static void ValidateEnabledModel(EnabledModelRow model)
    {
        if (string.IsNullOrWhiteSpace(model.Endpoint))
            throw new InvalidOperationException($"Loading AI model '{model.Name}' failed because its endpoint is missing.");

        if (!model.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !model.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Loading AI model '{model.Name}' failed because its endpoint must start with http:// or https://.");

        if (string.IsNullOrWhiteSpace(model.ProviderModelId))
            throw new InvalidOperationException($"Loading AI model '{model.Name}' failed because its provider model id is missing.");

        if (model.ProviderKind is AiProviderKind.OpenAI or AiProviderKind.Grok or AiProviderKind.Claude
            && string.IsNullOrWhiteSpace(model.ApiKey))
            throw new InvalidOperationException($"Loading AI model '{model.Name}' failed because the {model.ProviderName} API key is missing.");
    }

    private sealed class AnonymousAuthenticationPolicy : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNext(message, pipeline, currentIndex);

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNextAsync(message, pipeline, currentIndex);
    }

    private sealed record EnabledModelRow(
        Guid ModelId,
        string Name,
        string ProviderName,
        AiProviderKind ProviderKind,
        int ProviderSortOrder,
        int ModelSortOrder,
        string Endpoint,
        string ApiKey,
        string ProviderModelId,
        bool UseJsonSchemaResponseFormat);

    private sealed class EnabledModelRowComparer : IComparer<EnabledModelRow>
    {
        public static EnabledModelRowComparer Instance { get; } = new();

        public int Compare(EnabledModelRow? x, EnabledModelRow? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return 1;

            if (y is null)
                return -1;

            var modelIdComparison = AiModelPresentation.CompareProviderModelIds(x.ProviderKind, x.ProviderModelId, y.ProviderModelId);
            if (modelIdComparison != 0)
                return modelIdComparison;

            var sortOrderComparison = x.ModelSortOrder.CompareTo(y.ModelSortOrder);
            if (sortOrderComparison != 0)
                return sortOrderComparison;

            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class ThreadAgentService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IAgentCatalog agentCatalog) : IThreadAgentService
{
    public async Task<ThreadAgentSelectionView> GetSelectionAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading the chat AI model failed because the selected chat could not be found.");

        var availableAgents = agentCatalog.GetEnabledAgents();
        var normalizedModelId = agentCatalog.NormalizeSelectedModelId(thread.SelectedAiModelId);
        var selected = availableAgents.FirstOrDefault(x => x.ModelId == normalizedModelId);
        return new ThreadAgentSelectionView(
            selected?.Name,
            selected?.ModelId,
            availableAgents,
            availableAgents.Count > 0);
    }

    public async Task<ConfiguredAgent?> GetSelectedAgentAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Loading the chat AI model failed because the selected chat could not be found.");

        return agentCatalog.GetAgentOrDefault(thread.SelectedAiModelId);
    }

    public async Task SetSelectedAgentAsync(Guid threadId, Guid modelId, CancellationToken cancellationToken)
    {
        var normalizedModelId = agentCatalog.NormalizeSelectedModelId(modelId);
        if (normalizedModelId != modelId)
            throw new InvalidOperationException("Selecting the AI model failed because it is not available.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Selecting the AI model failed because the selected chat could not be found.");

        if (thread.SelectedAiModelId == normalizedModelId)
            return;

        thread.SelectedAiModelId = normalizedModelId;
        thread.SelectedAgentName = agentCatalog.GetEnabledAgents().First(x => x.ModelId == normalizedModelId).Name;
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh(threadId);
    }

    public async Task SetSelectedAgentAsync(Guid threadId, string agentName, CancellationToken cancellationToken)
    {
        var match = agentCatalog.GetEnabledAgents()
            .FirstOrDefault(x => string.Equals(x.Name, agentName, StringComparison.Ordinal));
        if (match is null)
            throw new InvalidOperationException($"Selecting AI model '{agentName}' failed because it is not available.");

        await SetSelectedAgentAsync(threadId, match.ModelId, cancellationToken);
    }

    private void PublishRefresh(Guid threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, occurredUtc));
    }
}
