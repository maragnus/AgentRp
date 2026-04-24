using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Services;

public sealed record AiProviderCatalogView(
    IReadOnlyList<AiProviderEditorView> Providers,
    IReadOnlyList<AgentProviderOptionView> EnabledModels);

public sealed record AiProviderEditorView(
    Guid ProviderId,
    string Name,
    AiProviderKind ProviderKind,
    string BaseEndpoint,
    string ApiKey,
    string ManagementApiKey,
    string? AccountId,
    string? ProjectId,
    string? TeamId,
    bool IsEnabled,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastDiscoveredUtc,
    string? LastDiscoveryError,
    DateTime? LastMetricsRefreshUtc,
    string? LastMetricsError,
    IReadOnlyList<AiModelEditorView> Models,
    IReadOnlyList<AiProviderMetricView> Metrics);

public sealed record AiModelEditorView(
    Guid ModelId,
    Guid ProviderId,
    string ProviderName,
    AiProviderKind ProviderKind,
    string ProviderModelId,
    string DisplayName,
    string? Endpoint,
    string? Repository,
    bool IsEnabled,
    bool UseJsonSchemaResponseFormat,
    StoryGenerationSettingsView Settings,
    DateTime UpdatedUtc);

public sealed record AiProviderMetricView(
    string MetricKind,
    string Label,
    string Value,
    string? Detail,
    DateTime RefreshedUtc);

public sealed record SaveAiProvider(
    Guid? ProviderId,
    string Name,
    AiProviderKind ProviderKind,
    string BaseEndpoint,
    string ApiKey,
    string ManagementApiKey,
    string? AccountId,
    string? ProjectId,
    string? TeamId,
    bool IsEnabled);

public sealed record SaveAiModel(
    Guid ModelId,
    string DisplayName,
    string? Endpoint,
    bool IsEnabled,
    bool UseJsonSchemaResponseFormat);

public sealed record AiProviderImportResult(
    int ProviderCount,
    int ModelCount);

public sealed record AiProviderDraft(
    string Name,
    AiProviderKind ProviderKind,
    string BaseEndpoint,
    string ApiKey,
    string ManagementApiKey,
    string? AccountId,
    string? ProjectId,
    string? TeamId);

public sealed record AiProviderDraftModelView(
    string ProviderModelId,
    string DisplayName,
    string? Endpoint,
    string? Repository);

public sealed record AiProviderDraftModelSelection(
    string ProviderModelId,
    string DisplayName,
    string? Endpoint,
    string? Repository,
    bool IsEnabled);

public sealed record AiProviderDraftDiscoveryResult(
    IReadOnlyList<AiProviderDraftModelView> Models,
    bool RequiresManualModel,
    string? Message);

public sealed record CreateAiProviderFromDraft(
    AiProviderDraft Provider,
    IReadOnlyList<AiProviderDraftModelSelection> Models);

public interface IAiProviderCatalogService
{
    Task<AiProviderCatalogView> GetCatalogAsync(CancellationToken cancellationToken);

    Task<AiProviderEditorView> SaveProviderAsync(SaveAiProvider command, CancellationToken cancellationToken);

    Task DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken);

    Task<AiProviderEditorView> SaveModelAsync(SaveAiModel command, CancellationToken cancellationToken);

    Task<AiProviderEditorView> DiscoverModelsAsync(Guid providerId, CancellationToken cancellationToken);

    Task<AiProviderEditorView> RefreshMetricsAsync(Guid providerId, CancellationToken cancellationToken);

    Task TestDraftProviderAsync(AiProviderDraft draft, CancellationToken cancellationToken);

    Task<AiProviderDraftDiscoveryResult> DiscoverDraftModelsAsync(AiProviderDraft draft, CancellationToken cancellationToken);

    Task TestDraftModelAsync(AiProviderDraft draft, string modelId, CancellationToken cancellationToken);

    Task<AiProviderEditorView> CreateProviderFromDraftAsync(CreateAiProviderFromDraft command, CancellationToken cancellationToken);

    string ExportCatalogJson(AiProviderCatalogView catalog);

    Task<AiProviderImportResult> ImportCatalogAsync(string json, CancellationToken cancellationToken);
}

public sealed class AiProviderCatalogService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IActivityNotifier activityNotifier) : IAiProviderCatalogService
{
    private static readonly Uri HuggingFaceWhoAmIUri = new("https://huggingface.co/api/whoami-v2");
    private static readonly Uri HuggingFaceManagementBaseUri = new("https://api.endpoints.huggingface.cloud/v2/");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<AiProviderCatalogView> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var providers = await dbContext.AiProviders
            .AsNoTracking()
            .Include(x => x.Models)
            .Include(x => x.Metrics)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var views = providers.Select(MapProvider).ToList();
        var enabledModels = views
            .SelectMany(provider => provider.Models
                .Where(model => provider.IsEnabled && model.IsEnabled)
                .Select(model => new AgentProviderOptionView(
                    model.ModelId,
                    model.DisplayName,
                    provider.Name,
                    provider.ProviderKind)))
            .ToList();

        return new AiProviderCatalogView(views, enabledModels);
    }

    public async Task<AiProviderEditorView> SaveProviderAsync(SaveAiProvider command, CancellationToken cancellationToken)
    {
        var name = NormalizeName(command.Name);
        var baseEndpoint = NormalizeEndpoint(command.BaseEndpoint, command.ProviderKind);
        var now = DateTime.UtcNow;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var duplicate = await dbContext.AiProviders.AnyAsync(
            x => x.Name == name && (!command.ProviderId.HasValue || x.Id != command.ProviderId.Value),
            cancellationToken);
        if (duplicate)
            throw new InvalidOperationException($"Saving provider '{name}' failed because another provider already uses that name.");

        AiProvider provider;
        if (command.ProviderId.HasValue)
        {
            provider = await dbContext.AiProviders
                .Include(x => x.Models)
                .Include(x => x.Metrics)
                .FirstOrDefaultAsync(x => x.Id == command.ProviderId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Saving provider '{name}' failed because the provider could not be found.");
        }
        else
        {
            provider = new AiProvider
            {
                Id = Guid.NewGuid(),
                Name = name,
                ProviderKind = command.ProviderKind,
                BaseEndpoint = baseEndpoint,
                CreatedUtc = now,
                UpdatedUtc = now,
                SortOrder = await dbContext.AiProviders.CountAsync(cancellationToken)
            };
            dbContext.AiProviders.Add(provider);
        }

        provider.Name = name;
        provider.ProviderKind = command.ProviderKind;
        provider.BaseEndpoint = baseEndpoint;
        provider.ApiKey = command.ApiKey.Trim();
        provider.ManagementApiKey = command.ManagementApiKey.Trim();
        provider.AccountId = NormalizeOptional(command.AccountId);
        provider.ProjectId = NormalizeOptional(command.ProjectId);
        provider.TeamId = NormalizeOptional(command.TeamId);
        provider.IsEnabled = command.IsEnabled;
        provider.UpdatedUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return MapProvider(provider);
    }

    public async Task DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var provider = await dbContext.AiProviders.FirstOrDefaultAsync(x => x.Id == providerId, cancellationToken)
            ?? throw new InvalidOperationException("Deleting provider failed because the provider could not be found.");

        dbContext.AiProviders.Remove(provider);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
    }

    public async Task<AiProviderEditorView> SaveModelAsync(SaveAiModel command, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var model = await dbContext.AiModels
            .Include(x => x.Provider)
            .FirstOrDefaultAsync(x => x.Id == command.ModelId, cancellationToken)
            ?? throw new InvalidOperationException("Saving model failed because the selected model could not be found.");

        var displayName = command.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Saving model failed because the display name was empty.");

        var endpoint = NormalizeOptional(command.Endpoint);
        if (!string.IsNullOrWhiteSpace(endpoint)
            && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Saving model '{displayName}' failed because the endpoint must start with http:// or https://.");

        model.DisplayName = displayName;
        model.Endpoint = endpoint;
        model.IsEnabled = command.IsEnabled;
        model.UseJsonSchemaResponseFormat = command.UseJsonSchemaResponseFormat;
        model.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return await GetProviderViewAsync(model.ProviderId, cancellationToken);
    }

    public async Task<AiProviderEditorView> DiscoverModelsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var provider = await dbContext.AiProviders
            .Include(x => x.Models)
            .Include(x => x.Metrics)
            .FirstOrDefaultAsync(x => x.Id == providerId, cancellationToken)
            ?? throw new InvalidOperationException("Discovering models failed because the provider could not be found.");

        try
        {
            var discovered = await DiscoverModelsAsync(provider, cancellationToken);
            UpsertDiscoveredModels(provider, discovered);
            provider.LastDiscoveredUtc = DateTime.UtcNow;
            provider.LastDiscoveryError = null;
        }
        catch (Exception exception)
        {
            provider.LastDiscoveredUtc = DateTime.UtcNow;
            provider.LastDiscoveryError = UserFacingErrorMessageBuilder.Build($"Discovering models for '{provider.Name}' failed.", exception);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(provider.LastDiscoveryError, exception);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return MapProvider(provider);
    }

    public async Task<AiProviderEditorView> RefreshMetricsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var provider = await dbContext.AiProviders
            .Include(x => x.Models)
            .Include(x => x.Metrics)
            .FirstOrDefaultAsync(x => x.Id == providerId, cancellationToken)
            ?? throw new InvalidOperationException("Refreshing provider metrics failed because the provider could not be found.");

        try
        {
            var metrics = await LoadMetricsAsync(provider, cancellationToken);
            provider.Metrics.Clear();
            foreach (var metric in metrics)
                provider.Metrics.Add(metric);

            provider.LastMetricsRefreshUtc = DateTime.UtcNow;
            provider.LastMetricsError = null;
        }
        catch (Exception exception)
        {
            provider.LastMetricsRefreshUtc = DateTime.UtcNow;
            provider.LastMetricsError = UserFacingErrorMessageBuilder.Build($"Refreshing metrics for '{provider.Name}' failed.", exception);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(provider.LastMetricsError, exception);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return MapProvider(provider);
    }

    public async Task TestDraftProviderAsync(AiProviderDraft draft, CancellationToken cancellationToken)
    {
        var provider = BuildDraftProvider(draft);

        if (provider.ProviderKind == AiProviderKind.OpenAiCompatible)
        {
            await TestOpenAiCompatibleConnectionAsync(provider, cancellationToken);
            return;
        }

        var models = await DiscoverModelsAsync(provider, cancellationToken);
        if (models.Count == 0)
            throw new InvalidOperationException($"Testing {provider.Name} failed because the provider did not return any models.");
    }

    public async Task<AiProviderDraftDiscoveryResult> DiscoverDraftModelsAsync(AiProviderDraft draft, CancellationToken cancellationToken)
    {
        var provider = BuildDraftProvider(draft);

        try
        {
            var discovered = provider.ProviderKind == AiProviderKind.OpenAiCompatible
                ? await DiscoverOpenAiCompatibleModelsAsync(provider, cancellationToken)
                : await DiscoverModelsAsync(provider, cancellationToken);
            var views = AiModelPresentation.OrderDraftModels(
                provider.ProviderKind,
                discovered.Select(x => new AiProviderDraftModelView(x.ProviderModelId, x.DisplayName, x.Endpoint, x.Repository)));
            if (views.Count > 0)
                return new AiProviderDraftDiscoveryResult(views, false, null);

            return provider.ProviderKind == AiProviderKind.OpenAiCompatible
                ? new AiProviderDraftDiscoveryResult([], true, "No models were listed by the endpoint. Enter a model name manually.")
                : new AiProviderDraftDiscoveryResult([], false, "No models were discovered.");
        }
        catch (Exception exception) when (provider.ProviderKind == AiProviderKind.OpenAiCompatible)
        {
            return new AiProviderDraftDiscoveryResult(
                [],
                true,
                $"Model discovery was unavailable for {provider.Name}. Enter a model name manually. Details: {exception.Message}");
        }
    }

    public async Task TestDraftModelAsync(AiProviderDraft draft, string modelId, CancellationToken cancellationToken)
    {
        var provider = BuildDraftProvider(draft);
        var normalizedModelId = modelId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModelId))
            throw new InvalidOperationException($"Testing {provider.Name} failed because the model name was empty.");

        if (provider.ProviderKind != AiProviderKind.OpenAiCompatible)
            return;

        using var client = CreateBearerClient(provider.ApiKey);
        var request = new
        {
            model = normalizedModelId,
            messages = new[]
            {
                new { role = "user", content = "Reply with OK." }
            },
            max_tokens = 2,
            temperature = 0
        };
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(provider.BaseEndpoint), "chat/completions"), request, JsonSerializerOptions, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = UserFacingErrorMessageBuilder.BuildExternalHttpFailure(
            $"Testing model '{normalizedModelId}' for {provider.Name}",
            response.StatusCode,
            responseBody);
        throw new ExternalServiceFailureException(message, response.StatusCode, responseBody);
    }

    public async Task<AiProviderEditorView> CreateProviderFromDraftAsync(CreateAiProviderFromDraft command, CancellationToken cancellationToken)
    {
        var provider = BuildDraftProvider(command.Provider);
        var selectedModels = command.Models.Where(x => x.IsEnabled).ToList();
        if (selectedModels.Count == 0)
            throw new InvalidOperationException($"Saving {provider.Name} failed because at least one model must be selected.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var duplicate = await dbContext.AiProviders.AnyAsync(x => x.Name == provider.Name, cancellationToken);
        if (duplicate)
            throw new InvalidOperationException($"Saving provider '{provider.Name}' failed because another provider already uses that name.");

        var now = DateTime.UtcNow;
        provider.Id = Guid.NewGuid();
        provider.CreatedUtc = now;
        provider.UpdatedUtc = now;
        provider.LastDiscoveredUtc = now;
        provider.SortOrder = await dbContext.AiProviders.CountAsync(cancellationToken);

        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in command.Models)
        {
            var providerModelId = model.ProviderModelId.Trim();
            if (string.IsNullOrWhiteSpace(providerModelId))
                throw new InvalidOperationException($"Saving {provider.Name} failed because a selected model id was empty.");

            if (!modelIds.Add(providerModelId))
                continue;

            provider.Models.Add(new AiModel
            {
                Id = Guid.NewGuid(),
                ProviderId = provider.Id,
                ProviderModelId = providerModelId,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? providerModelId : model.DisplayName.Trim(),
                Endpoint = NormalizeOptional(model.Endpoint),
                Repository = NormalizeOptional(model.Repository),
                IsEnabled = model.IsEnabled,
                UseJsonSchemaResponseFormat = provider.ProviderKind != AiProviderKind.Claude,
                SortOrder = provider.Models.Count,
                PlanningSettingsJson = StoryGenerationSettingsService.CreateDefaultStageSettingsJson(StoryGenerationStage.Planning),
                WritingSettingsJson = StoryGenerationSettingsService.CreateDefaultStageSettingsJson(StoryGenerationStage.Writing),
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }

        dbContext.AiProviders.Add(provider);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return MapProvider(provider);
    }

    public string ExportCatalogJson(AiProviderCatalogView catalog)
    {
        var package = new AiProviderCatalogTransferPackage(
            1,
            DateTime.UtcNow,
            catalog.Providers.Select(provider => new AiProviderTransferDocument(
                provider.Name,
                provider.ProviderKind,
                provider.BaseEndpoint,
                provider.ApiKey,
                provider.ManagementApiKey,
                provider.AccountId,
                provider.ProjectId,
                provider.TeamId,
                provider.IsEnabled,
                provider.Models.Select(model => new AiModelTransferDocument(
                    model.ProviderModelId,
                    model.DisplayName,
                    model.Endpoint,
                    model.Repository,
                    model.IsEnabled,
                    model.UseJsonSchemaResponseFormat,
                    model.Settings.Planning,
                    model.Settings.Writing)).ToList())).ToList());

        return JsonSerializer.Serialize(package, JsonSerializerOptions);
    }

    public async Task<AiProviderImportResult> ImportCatalogAsync(string json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Importing providers failed because the selected file was empty.");

        AiProviderCatalogTransferPackage package;
        try
        {
            package = JsonSerializer.Deserialize<AiProviderCatalogTransferPackage>(json, JsonSerializerOptions)
                ?? throw new InvalidOperationException("Importing providers failed because the file did not contain a provider package.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Importing providers failed because the file was not valid JSON.", exception);
        }

        if (package.SchemaVersion != 1)
            throw new InvalidOperationException($"Importing providers failed because schema version {package.SchemaVersion} is not supported.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in package.Providers)
        {
            var name = NormalizeName(provider.Name);
            if (!names.Add(name))
                throw new InvalidOperationException($"Importing providers failed because provider '{name}' appears more than once.");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AiProviders.RemoveRange(dbContext.AiProviders);
        var now = DateTime.UtcNow;
        var providerIndex = 0;
        var modelCount = 0;

        foreach (var providerDocument in package.Providers)
        {
            var provider = new AiProvider
            {
                Id = Guid.NewGuid(),
                Name = NormalizeName(providerDocument.Name),
                ProviderKind = providerDocument.ProviderKind,
                BaseEndpoint = NormalizeEndpoint(providerDocument.BaseEndpoint, providerDocument.ProviderKind),
                ApiKey = providerDocument.ApiKey?.Trim() ?? string.Empty,
                ManagementApiKey = providerDocument.ManagementApiKey?.Trim() ?? string.Empty,
                AccountId = NormalizeOptional(providerDocument.AccountId),
                ProjectId = NormalizeOptional(providerDocument.ProjectId),
                TeamId = NormalizeOptional(providerDocument.TeamId),
                IsEnabled = providerDocument.IsEnabled,
                SortOrder = providerIndex++,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var modelDocument in providerDocument.Models)
            {
                var modelId = modelDocument.ProviderModelId.Trim();
                if (string.IsNullOrWhiteSpace(modelId))
                    throw new InvalidOperationException($"Importing provider '{provider.Name}' failed because a model id was empty.");

                if (!modelIds.Add(modelId))
                    throw new InvalidOperationException($"Importing provider '{provider.Name}' failed because model '{modelId}' appears more than once.");

                provider.Models.Add(new AiModel
                {
                    Id = Guid.NewGuid(),
                    ProviderModelId = modelId,
                    DisplayName = string.IsNullOrWhiteSpace(modelDocument.DisplayName) ? modelId : modelDocument.DisplayName.Trim(),
                    Endpoint = NormalizeOptional(modelDocument.Endpoint),
                    Repository = NormalizeOptional(modelDocument.Repository),
                    IsEnabled = modelDocument.IsEnabled,
                    UseJsonSchemaResponseFormat = modelDocument.UseJsonSchemaResponseFormat,
                    SortOrder = provider.Models.Count,
                    PlanningSettingsJson = JsonSerializer.Serialize(
                        modelDocument.Planning ?? StoryGenerationSettingsService.CreateDefaultStageSettings(StoryGenerationStage.Planning),
                        JsonSerializerOptions),
                    WritingSettingsJson = JsonSerializer.Serialize(
                        modelDocument.Writing ?? StoryGenerationSettingsService.CreateDefaultStageSettings(StoryGenerationStage.Writing),
                        JsonSerializerOptions),
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
                modelCount++;
            }

            dbContext.AiProviders.Add(provider);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishRefresh();
        return new AiProviderImportResult(package.Providers.Count, modelCount);
    }

    private async Task<AiProviderEditorView> GetProviderViewAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var provider = await dbContext.AiProviders
            .AsNoTracking()
            .Include(x => x.Models)
            .Include(x => x.Metrics)
            .FirstOrDefaultAsync(x => x.Id == providerId, cancellationToken)
            ?? throw new InvalidOperationException("Loading provider failed because the provider could not be found.");

        return MapProvider(provider);
    }

    private static AiProvider BuildDraftProvider(AiProviderDraft draft) => new()
    {
        Id = Guid.Empty,
        Name = NormalizeName(draft.Name),
        ProviderKind = draft.ProviderKind,
        BaseEndpoint = NormalizeEndpoint(draft.BaseEndpoint, draft.ProviderKind),
        ApiKey = draft.ApiKey.Trim(),
        ManagementApiKey = draft.ManagementApiKey.Trim(),
        AccountId = NormalizeOptional(draft.AccountId),
        ProjectId = NormalizeOptional(draft.ProjectId),
        TeamId = NormalizeOptional(draft.TeamId),
        IsEnabled = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private async Task TestOpenAiCompatibleConnectionAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateBearerClient(provider.ApiKey);
        try
        {
            using var response = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "models"), cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = UserFacingErrorMessageBuilder.BuildExternalHttpFailure(
                    $"Testing {provider.Name}",
                    response.StatusCode,
                    responseBody);
                throw new ExternalServiceFailureException(message, response.StatusCode, responseBody);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Testing {provider.Name} failed because the endpoint could not be reached.", exception);
        }
    }

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(AiProvider provider, CancellationToken cancellationToken) =>
        provider.ProviderKind switch
        {
            AiProviderKind.OpenAI => await DiscoverOpenAiModelsAsync(provider, cancellationToken),
            AiProviderKind.Grok => await DiscoverGrokModelsAsync(provider, cancellationToken),
            AiProviderKind.Claude => await DiscoverClaudeModelsAsync(provider, cancellationToken),
            AiProviderKind.HuggingFaceInferenceEndpoint => await DiscoverHuggingFaceModelsAsync(provider, cancellationToken),
            AiProviderKind.OpenAiCompatible => await DiscoverOpenAiCompatibleModelsAsync(provider, cancellationToken),
            _ => []
        };

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverOpenAiCompatibleModelsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateBearerClient(provider.ApiKey);
        using var response = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "models"), cancellationToken);
        var json = await ReadJsonAsync(response, $"Discovering OpenAI-compatible models for '{provider.Name}'", cancellationToken);
        return json["data"]?.AsArray()
            .Select(x => x?["id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new DiscoveredModel(x!, x!, null, null))
            .ToList()
            ?? [];
    }

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverOpenAiModelsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateBearerClient(provider.ApiKey);
        using var response = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "models"), cancellationToken);
        var json = await ReadJsonAsync(response, $"Discovering OpenAI models for '{provider.Name}'", cancellationToken);
        return json["data"]?.AsArray()
            .Select(x => x?["id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new DiscoveredModel(x!, x!, null, null))
            .ToList()
            ?? [];
    }

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverGrokModelsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateBearerClient(provider.ApiKey);
        using var response = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "language-models"), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(JsonSerializerOptions, cancellationToken)
                ?? new JsonObject();
            var languageModels = json["models"]?.AsArray()
                .SelectMany(x => BuildGrokModels(x))
                .ToList();
            if (languageModels is { Count: > 0 })
                return languageModels;
        }

        using var fallbackResponse = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "models"), cancellationToken);
        var fallbackJson = await ReadJsonAsync(fallbackResponse, $"Discovering Grok models for '{provider.Name}'", cancellationToken);
        return fallbackJson["data"]?.AsArray()
            .Select(x => x?["id"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new DiscoveredModel(x!, x!, null, null))
            .ToList()
            ?? [];
    }

    private static IEnumerable<DiscoveredModel> BuildGrokModels(JsonNode? node)
    {
        var id = node?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
            yield break;

        yield return new DiscoveredModel(id, id, null, null);
        foreach (var alias in node?["aliases"]?.AsArray() ?? [])
        {
            var aliasId = alias?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(aliasId))
                yield return new DiscoveredModel(aliasId, aliasId, null, null);
        }
    }

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverClaudeModelsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateApiKeyClient(provider.ApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        using var response = await client.GetAsync(new Uri(new Uri(provider.BaseEndpoint), "models"), cancellationToken);
        var json = await ReadJsonAsync(response, $"Discovering Claude models for '{provider.Name}'", cancellationToken);
        return json["data"]?.AsArray()
            .Select(x =>
            {
                var id = x?["id"]?.GetValue<string>();
                var displayName = x?["display_name"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(id)
                    ? null
                    : new DiscoveredModel(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName, null, null);
            })
            .Where(x => x is not null)
            .Cast<DiscoveredModel>()
            .ToList()
            ?? [];
    }

    private async Task<IReadOnlyList<DiscoveredModel>> DiscoverHuggingFaceModelsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        using var client = CreateBearerClient(provider.ApiKey);
        using var whoAmIResponse = await client.GetAsync(HuggingFaceWhoAmIUri, cancellationToken);
        var whoAmI = await ReadJsonAsync(whoAmIResponse, $"Discovering Hugging Face namespaces for '{provider.Name}'", cancellationToken);
        var namespaces = new List<string>();
        AddNamespace(namespaces, whoAmI["name"]?.GetValue<string>());
        foreach (var org in whoAmI["orgs"]?.AsArray() ?? [])
            AddNamespace(namespaces, org?["name"]?.GetValue<string>());

        var models = new List<DiscoveredModel>();
        foreach (var @namespace in namespaces)
        {
            var requestUri = new Uri(HuggingFaceManagementBaseUri, $"endpoint/{Uri.EscapeDataString(@namespace)}?limit=100");
            using var response = await client.GetAsync(requestUri, cancellationToken);
            var json = await ReadJsonAsync(response, $"Discovering Hugging Face endpoints for '{provider.Name}'", cancellationToken);
            foreach (var item in json["items"]?.AsArray() ?? [])
            {
                var endpointName = item?["name"]?.GetValue<string>();
                var repository = item?["model"]?["repository"]?.GetValue<string>();
                var endpoint = item?["status"]?["url"]?.GetValue<string>();
                var modelId = string.IsNullOrWhiteSpace(repository) ? endpointName : repository;
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                var display = string.IsNullOrWhiteSpace(endpointName) ? modelId : $"{endpointName} ({modelId})";
                models.Add(new DiscoveredModel(modelId, display, endpoint, repository));
            }
        }

        return models;
    }

    private static void UpsertDiscoveredModels(AiProvider provider, IReadOnlyList<DiscoveredModel> discoveredModels)
    {
        var now = DateTime.UtcNow;
        var existing = provider.Models.ToDictionary(x => x.ProviderModelId, StringComparer.Ordinal);

        foreach (var discovered in discoveredModels.DistinctBy(x => x.ProviderModelId))
        {
            if (existing.TryGetValue(discovered.ProviderModelId, out var model))
            {
                model.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? discovered.DisplayName : model.DisplayName;
                model.Endpoint = string.IsNullOrWhiteSpace(discovered.Endpoint) ? model.Endpoint : discovered.Endpoint;
                model.Repository = string.IsNullOrWhiteSpace(discovered.Repository) ? model.Repository : discovered.Repository;
                model.UpdatedUtc = now;
                continue;
            }

            provider.Models.Add(new AiModel
            {
                Id = Guid.NewGuid(),
                ProviderId = provider.Id,
                ProviderModelId = discovered.ProviderModelId,
                DisplayName = discovered.DisplayName,
                Endpoint = discovered.Endpoint,
                Repository = discovered.Repository,
                IsEnabled = provider.Models.Count == 0,
                UseJsonSchemaResponseFormat = provider.ProviderKind != AiProviderKind.Claude,
                SortOrder = provider.Models.Count,
                PlanningSettingsJson = StoryGenerationSettingsService.CreateDefaultStageSettingsJson(StoryGenerationStage.Planning),
                WritingSettingsJson = StoryGenerationSettingsService.CreateDefaultStageSettingsJson(StoryGenerationStage.Writing),
                CreatedUtc = now,
                UpdatedUtc = now
            });
        }
    }

    private async Task<IReadOnlyList<AiProviderMetric>> LoadMetricsAsync(AiProvider provider, CancellationToken cancellationToken)
    {
        var metrics = new List<AiProviderMetric>
        {
            CreateMetric("connection", "Connection", provider.LastDiscoveryError is null ? "Ready" : "Needs attention", provider.LastDiscoveryError)
        };

        if (provider.Models.Count > 0)
            metrics.Add(CreateMetric("models", "Models", $"{provider.Models.Count} discovered", $"{provider.Models.Count(x => x.IsEnabled)} enabled for quick switch"));

        if (provider.ProviderKind == AiProviderKind.Claude && string.IsNullOrWhiteSpace(provider.ManagementApiKey))
        {
            metrics.Add(CreateMetric("usage", "Usage", "Admin key required", "Claude usage and cost metrics require an Anthropic Admin API key."));
            return metrics;
        }

        if (provider.ProviderKind == AiProviderKind.Grok && string.IsNullOrWhiteSpace(provider.TeamId))
        {
            metrics.Add(CreateMetric("billing", "Billing", "Team ID required", "xAI billing metrics require the team id from xAI Console."));
            return metrics;
        }

        if (provider.ProviderKind == AiProviderKind.HuggingFaceInferenceEndpoint)
        {
            metrics.Add(CreateMetric("billing", "Billing", "Endpoint runtime based", "Hugging Face Inference Endpoints are billed by deployed runtime; pause endpoints to stop billing."));
            return metrics;
        }

        await TryLoadRemoteMetricsAsync(provider, metrics, cancellationToken);
        return metrics;
    }

    private async Task TryLoadRemoteMetricsAsync(AiProvider provider, List<AiProviderMetric> metrics, CancellationToken cancellationToken)
    {
        if (provider.ProviderKind == AiProviderKind.Grok && !string.IsNullOrWhiteSpace(provider.TeamId))
        {
            using var client = CreateBearerClient(string.IsNullOrWhiteSpace(provider.ManagementApiKey) ? provider.ApiKey : provider.ManagementApiKey);
            using var response = await client.GetAsync(new Uri($"https://management-api.x.ai/v1/billing/teams/{Uri.EscapeDataString(provider.TeamId)}/prepaid/balance"), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                metrics.Add(CreateMetric("prepaid-balance", "Prepaid Balance", "Available", json));
            }
            else
            {
                metrics.Add(CreateMetric("billing", "Billing", "Unavailable", $"xAI returned {(int)response.StatusCode} ({response.StatusCode})."));
            }
        }

        if (provider.ProviderKind == AiProviderKind.Claude && !string.IsNullOrWhiteSpace(provider.ManagementApiKey))
        {
            var start = DateTime.UtcNow.Date.AddDays(-7).ToString("O");
            var end = DateTime.UtcNow.Date.AddDays(1).ToString("O");
            using var client = CreateApiKeyClient(provider.ManagementApiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var uri = new Uri($"https://api.anthropic.com/v1/organizations/usage_report/messages?starting_at={Uri.EscapeDataString(start)}&ending_at={Uri.EscapeDataString(end)}&bucket_width=1d");
            using var response = await client.GetAsync(uri, cancellationToken);
            metrics.Add(response.IsSuccessStatusCode
                ? CreateMetric("usage", "Usage", "Last 7 days available", await response.Content.ReadAsStringAsync(cancellationToken))
                : CreateMetric("usage", "Usage", "Unavailable", $"Anthropic returned {(int)response.StatusCode} ({response.StatusCode})."));
        }

        if (provider.ProviderKind == AiProviderKind.OpenAI)
            metrics.Add(CreateMetric("usage", "Usage", "Use dashboard/API", "OpenAI usage and costs are available from the Usage and Costs APIs for projects with permission."));
    }

    private static AiProviderMetric CreateMetric(string kind, string label, string value, string? detail) => new()
    {
        Id = Guid.NewGuid(),
        MetricKind = kind,
        Label = label,
        Value = value,
        Detail = detail,
        RefreshedUtc = DateTime.UtcNow
    };

    private HttpClient CreateBearerClient(string apiKey)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return client;
    }

    private HttpClient CreateApiKeyClient(string apiKey)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

        return client;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<JsonNode>(JsonSerializerOptions, cancellationToken)
                ?? new JsonObject();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = UserFacingErrorMessageBuilder.BuildExternalHttpFailure(operation, response.StatusCode, responseBody);
        throw new ExternalServiceFailureException(message, response.StatusCode, responseBody);
    }

    private static AiProviderEditorView MapProvider(AiProvider provider) => new(
        provider.Id,
        provider.Name,
        provider.ProviderKind,
        provider.BaseEndpoint,
        provider.ApiKey,
        provider.ManagementApiKey,
        provider.AccountId,
        provider.ProjectId,
        provider.TeamId,
        provider.IsEnabled,
        provider.CreatedUtc,
        provider.UpdatedUtc,
        provider.LastDiscoveredUtc,
        provider.LastDiscoveryError,
        provider.LastMetricsRefreshUtc,
        provider.LastMetricsError,
        AiModelPresentation.OrderEditorModels(
            provider.ProviderKind,
            provider.Models.Select(model => MapModel(provider, model))),
        provider.Metrics
            .OrderBy(x => x.Label)
            .Select(x => new AiProviderMetricView(x.MetricKind, x.Label, x.Value, x.Detail, x.RefreshedUtc))
            .ToList());

    private static AiModelEditorView MapModel(AiProvider provider, AiModel model) => new(
        model.Id,
        provider.Id,
        provider.Name,
        provider.ProviderKind,
        model.ProviderModelId,
        model.DisplayName,
        model.Endpoint,
        model.Repository,
        model.IsEnabled,
        model.UseJsonSchemaResponseFormat,
        new StoryGenerationSettingsView(
            DeserializeStage(model.PlanningSettingsJson, StoryGenerationStage.Planning),
            DeserializeStage(model.WritingSettingsJson, StoryGenerationStage.Writing)),
        model.UpdatedUtc);

    private static StoryModelStageSettingsView DeserializeStage(string json, StoryGenerationStage stage)
    {
        if (string.IsNullOrWhiteSpace(json))
            return StoryGenerationSettingsService.CreateDefaultStageSettings(stage);

        try
        {
            return JsonSerializer.Deserialize<StoryModelStageSettingsView>(json, JsonSerializerOptions)
                ?? StoryGenerationSettingsService.CreateDefaultStageSettings(stage);
        }
        catch (JsonException)
        {
            return StoryGenerationSettingsService.CreateDefaultStageSettings(stage);
        }
    }

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        throw new InvalidOperationException("Saving provider failed because the provider name was empty.");
    }

    private static string NormalizeEndpoint(string? endpoint, AiProviderKind kind)
    {
        var normalized = endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = kind switch
            {
                AiProviderKind.OpenAI => "https://api.openai.com/v1/",
                AiProviderKind.Grok => "https://api.x.ai/v1/",
                AiProviderKind.Claude => "https://api.anthropic.com/v1/",
                AiProviderKind.HuggingFaceInferenceEndpoint => "https://api.endpoints.huggingface.cloud/v2/",
                _ => string.Empty
            };

        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Saving provider failed because the endpoint was empty.");

        if (!normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Saving provider failed because the endpoint must start with http:// or https://.");

        return normalized.EndsWith('/') ? normalized : $"{normalized}/";
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static void AddNamespace(List<string> namespaces, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || namespaces.Any(x => string.Equals(x, candidate, StringComparison.Ordinal)))
            return;

        namespaces.Add(candidate);
    }

    private void PublishRefresh()
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, null, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, null, occurredUtc));
    }

    private sealed record DiscoveredModel(
        string ProviderModelId,
        string DisplayName,
        string? Endpoint,
        string? Repository);

    private sealed record AiProviderCatalogTransferPackage(
        int SchemaVersion,
        DateTime ExportedUtc,
        IReadOnlyList<AiProviderTransferDocument> Providers);

    private sealed record AiProviderTransferDocument(
        string Name,
        AiProviderKind ProviderKind,
        string BaseEndpoint,
        string ApiKey,
        string ManagementApiKey,
        string? AccountId,
        string? ProjectId,
        string? TeamId,
        bool IsEnabled,
        IReadOnlyList<AiModelTransferDocument> Models);

    private sealed record AiModelTransferDocument(
        string ProviderModelId,
        string DisplayName,
        string? Endpoint,
        string? Repository,
        bool IsEnabled,
        bool UseJsonSchemaResponseFormat,
        StoryModelStageSettingsView Planning,
        StoryModelStageSettingsView Writing);
}
