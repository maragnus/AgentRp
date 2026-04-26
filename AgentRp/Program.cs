using AgentRp.Components;
using AgentRp.Data;
using AgentRp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContextFactory<AgentRp.Data.AppContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("agentrp-db")
        ?? throw new InvalidOperationException("Connection string 'agentrp-db' was not found.");

    options.UseSqlServer(connectionString);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<IActivityNotifier, ActivityNotifier>();
builder.Services.AddSingleton<IUserFeedbackService, UserFeedbackService>();
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
builder.Services.AddSingleton<IModelOperationRegistry, ModelOperationRegistry>();
builder.Services.AddSingleton<IAgentTurnComposer, AgentTurnComposer>();
builder.Services.AddScoped<IAgentCatalog, AgentCatalog>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IAgentEndpointManagementService, AgentEndpointManagementService>();
builder.Services.AddScoped<IAiProviderCatalogService, AiProviderCatalogService>();
builder.Services.AddScoped<IThreadAgentService, ThreadAgentService>();
builder.Services.AddScoped<IChatWorkspaceService, ChatWorkspaceService>();
builder.Services.AddScoped<IChatTransferService, ChatTransferService>();
builder.Services.AddScoped<IChatStoryService, ChatStoryService>();
builder.Services.AddScoped<IStoryChatSnapshotService, StoryChatSnapshotService>();
builder.Services.AddScoped<IStoryChatAppearanceService, StoryChatAppearanceService>();
builder.Services.AddScoped<IStoryChatAppearanceReplayService, StoryChatAppearanceReplayService>();
builder.Services.AddScoped<IStoryScenePromptLibraryService, StoryScenePromptLibraryService>();
builder.Services.AddScoped<IStorySceneChatService, StorySceneChatService>();
builder.Services.AddScoped<IStoryImageOptimizationService, StoryImageOptimizationService>();
builder.Services.AddScoped<IStoryImageService, StoryImageService>();
builder.Services.AddScoped<IStoryEntityAiAssistService, StoryEntityAiAssistService>();
builder.Services.AddScoped<IStoryCharacterModelSheetService, StoryCharacterModelSheetService>();
builder.Services.AddScoped<IStoryCharacterPrivateMotivationsService, StoryCharacterPrivateMotivationsService>();
builder.Services.AddScoped<IStoryFieldGuidanceService, StoryFieldGuidanceService>();
builder.Services.AddScoped<IStoryGenerationSettingsService, StoryGenerationSettingsService>();
builder.Services.AddScoped<IStorySceneChatDisplayPreferencesService, StorySceneChatDisplayPreferencesService>();
builder.Services.AddHostedService<StoryImageOptimizationWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AgentRp.Data.AppContext>>();
    await using var dbContext = await dbContextFactory.CreateDbContextAsync();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapGet("/story-images/{imageId:guid}", async (
    Guid imageId,
    HttpContext httpContext,
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    CancellationToken cancellationToken) =>
{
    await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
    var image = await dbContext.StoryImageAssets
        .AsNoTracking()
        .Where(x => x.Id == imageId)
        .Select(x => new
        {
            x.Id,
            x.Bytes,
            x.ContentType,
            x.FileName,
            x.CreatedUtc,
            x.OptimizedUtc
        })
        .FirstOrDefaultAsync(cancellationToken);
    if (image is null)
        return Results.NotFound();

    var lastChangedUtc = image.OptimizedUtc ?? image.CreatedUtc;
    var entityTag = new EntityTagHeaderValue($"\"{image.Id:N}-{lastChangedUtc.Ticks:x}\"");
    if (httpContext.Request.Headers.IfNoneMatch.Any(x => string.Equals(x, entityTag.Tag.Value, StringComparison.Ordinal)))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    httpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(
        image.Bytes,
        image.ContentType,
        image.FileName,
        lastModified: new DateTimeOffset(DateTime.SpecifyKind(lastChangedUtc, DateTimeKind.Utc)),
        entityTag: entityTag,
        enableRangeProcessing: true);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
