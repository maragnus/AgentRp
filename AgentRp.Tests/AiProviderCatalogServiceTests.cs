using System.Net;
using System.Text;
using AgentRp.Data;
using AgentRp.Services;
using Microsoft.EntityFrameworkCore;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Tests;

public sealed class AiProviderCatalogServiceTests
{
    [Fact]
    public async Task DiscoverDraftModelsAsync_OpenAi_ReturnsListedModels()
    {
        var service = CreateService(_ => Json("""
            {
              "data": [
                { "id": "gpt-5.4" },
                { "id": "gpt-5.4-mini" }
              ]
            }
            """));

        var result = await service.DiscoverDraftModelsAsync(
            Draft(AiProviderKind.OpenAI),
            CancellationToken.None);

        Assert.False(result.RequiresManualModel);
        Assert.Equal(["gpt-5.4", "gpt-5.4-mini", "gpt-image-1"], result.Models.Select(x => x.ProviderModelId).ToArray());
    }

    [Fact]
    public async Task DiscoverDraftModelsAsync_OpenAiCompatible_WhenModelsUnavailable_AllowsManualModel()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        });

        var result = await service.DiscoverDraftModelsAsync(
            Draft(AiProviderKind.OpenAiCompatible),
            CancellationToken.None);

        Assert.True(result.RequiresManualModel);
        Assert.Empty(result.Models);
        Assert.Contains("Enter a model name manually", result.Message);
    }

    [Fact]
    public async Task TestDraftProviderAsync_GrokInvalidApiKey_IncludesActionableProviderReason()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"code":"Client specified an invalid argument","error":"Incorrect API key provided: xa-this-is-a-long-secret-yP. You can obtain an API key from https://console.x.ai."}""",
                Encoding.UTF8,
                "application/json")
        });

        var exception = await Assert.ThrowsAsync<ExternalServiceFailureException>(() =>
            service.TestDraftProviderAsync(
                Draft(AiProviderKind.Grok) with { Name = "Grok / xAI" },
                CancellationToken.None));

        Assert.Contains("Discovering Grok models for 'Grok / xAI' failed", exception.Message);
        Assert.Contains("xAI rejected the configured API key", exception.Message);
        Assert.Contains("Incorrect API key provided", exception.Message);
        Assert.DoesNotContain("xa-this-is-a-long-secret-yP", exception.Message);
        Assert.DoesNotContain("Response:", exception.Message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_Build_IncludesInnerExternalReason()
    {
        var exception = new InvalidOperationException(
            "Outer wrapper failed.",
            new InvalidOperationException("Remote provider said the quota is exhausted."));

        var message = UserFacingErrorMessageBuilder.Build("Generating the scene message failed.", exception);

        Assert.Equal("Generating the scene message failed: Remote provider said the quota is exhausted.", message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_Build_IncludesInternalExceptionReason()
    {
        var exception = new InvalidOperationException("The selected chapter could not be found.");

        var message = UserFacingErrorMessageBuilder.Build("Saving the scene bookmark failed.", exception);

        Assert.Equal("Saving the scene bookmark failed: The selected chapter could not be found.", message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_Build_EmptyOrNoisyExceptionFallsBackToOperation()
    {
        var exception = new Exception();

        var message = UserFacingErrorMessageBuilder.Build("Saving the scene bookmark failed.", exception);

        Assert.Equal("Saving the scene bookmark failed.", message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_Build_RedactsLongSecrets()
    {
        var exception = new InvalidOperationException(
            "Provider rejected api_key=sk-proj-this-is-a-very-long-secret-value-that-should-not-display.");

        var message = UserFacingErrorMessageBuilder.Build("Testing provider failed.", exception);

        Assert.Contains("Testing provider failed", message);
        Assert.Contains("api_key=", message);
        Assert.Contains("***", message);
        Assert.DoesNotContain("this-is-a-very-long-secret-value", message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_Build_RemovesStackTraceFragments()
    {
        var exception = new InvalidOperationException("""
            The widget parser rejected row 7.
               at AgentRp.Services.WidgetParser.Parse()
               at AgentRp.Services.WidgetImporter.Import()
            """);

        var message = UserFacingErrorMessageBuilder.Build("Importing widgets failed.", exception);

        Assert.Equal("Importing widgets failed: The widget parser rejected row 7.", message);
        Assert.DoesNotContain("AgentRp.Services.WidgetParser", message);
        Assert.DoesNotContain(" at ", message);
    }

    [Fact]
    public void UserFacingErrorMessageBuilder_BuildExternalHttpFailure_ExtractsJsonReason()
    {
        var message = UserFacingErrorMessageBuilder.BuildExternalHttpFailure(
            "Discovering Grok models for 'Grok / xAI'",
            HttpStatusCode.BadRequest,
            """{"error":"Incorrect API key provided: xa-this-is-a-long-secret-yP."}""");

        Assert.Contains("xAI rejected the configured API key", message);
        Assert.Contains("Incorrect API key provided", message);
        Assert.DoesNotContain("xa-this-is-a-long-secret-yP", message);
    }

    [Fact]
    public async Task CreateProviderFromDraftAsync_SavesSelectedModelsOnlyAsEnabled()
    {
        var factory = CreateDbFactory();
        var service = CreateService(_ => Json("{}"), factory);

        var provider = await service.CreateProviderFromDraftAsync(
            new CreateAiProviderFromDraft(
                Draft(AiProviderKind.OpenAI),
                [
                    new("gpt-5.4", "GPT 5.4", null, null, true),
                    new("gpt-5.4-mini", "GPT 5.4 Mini", null, null, false)
                ]),
            CancellationToken.None);

        await using var dbContext = await factory.CreateDbContextAsync();
        var saved = await dbContext.AiProviders.Include(x => x.Models).SingleAsync();
        Assert.Equal(provider.ProviderId, saved.Id);
        Assert.Equal(2, saved.Models.Count);
        Assert.Contains(saved.Models, x => x.ProviderModelId == "gpt-5.4" && x.IsEnabled);
        Assert.Contains(saved.Models, x => x.ProviderModelId == "gpt-5.4-mini" && !x.IsEnabled);
        Assert.All(saved.Models, x => Assert.True(x.IsTextModelEnabled));
        Assert.DoesNotContain(saved.Models, x => x.IsImageModelEnabled);
    }

    [Fact]
    public async Task TestDraftModelAsync_OpenAiCompatible_PostsChatCompletion()
    {
        HttpRequestMessage? capturedRequest = null;
        var service = CreateService(request =>
        {
            capturedRequest = request;
            return Json("""
                {
                  "choices": [
                    { "message": { "content": "OK" } }
                  ]
                }
                """);
        });

        await service.TestDraftModelAsync(
            Draft(AiProviderKind.OpenAiCompatible),
            "local-model",
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.EndsWith("/chat/completions", capturedRequest.RequestUri?.AbsolutePath);
    }

    private static AiProviderDraft Draft(AiProviderKind kind) => new(
        "Test Provider",
        kind,
        kind == AiProviderKind.OpenAiCompatible ? "https://example.test/v1/" : string.Empty,
        "key",
        string.Empty,
        null,
        null,
        null);

    private static AiProviderCatalogService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        TestDbContextFactory? factory = null) =>
        new(
            factory ?? CreateDbFactory(),
            new TestHttpClientFactory(handler),
            new ActivityNotifier());

    private static TestDbContextFactory CreateDbFactory() =>
        new(new DbContextOptionsBuilder<DbAppContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class TestDbContextFactory(DbContextOptions<DbAppContext> options) : IDbContextFactory<DbAppContext>
    {
        public DbAppContext CreateDbContext() => new(options);

        public Task<DbAppContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DelegatingHandlerStub(handler));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
