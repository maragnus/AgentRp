using AgentRp.Components;
using AgentRp.Data;
using AgentRp.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContextFactory<AgentRp.Data.AppContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("agentrp-db")
        ?? throw new InvalidOperationException("Connection string 'agentrp-db' was not found.");

    options.UseSqlServer(connectionString);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<AgentOptions>(builder.Configuration);
builder.Services.AddSingleton<IActivityNotifier, ActivityNotifier>();
builder.Services.AddSingleton<IUserFeedbackService, UserFeedbackService>();
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
builder.Services.AddSingleton<IAgentTurnComposer, AgentTurnComposer>();
builder.Services.AddSingleton<IAgentCatalog, AgentCatalog>();
builder.Services.AddScoped<IThreadAgentService, ThreadAgentService>();
builder.Services.AddScoped<IChatWorkspaceService, ChatWorkspaceService>();
builder.Services.AddScoped<IChatStoryService, ChatStoryService>();
builder.Services.AddScoped<IStoryChatSnapshotService, StoryChatSnapshotService>();
builder.Services.AddScoped<IStoryChatAppearanceService, StoryChatAppearanceService>();
builder.Services.AddScoped<IStorySceneChatService, StorySceneChatService>();
builder.Services.AddScoped<IStoryEntityAiAssistService, StoryEntityAiAssistService>();
builder.Services.AddScoped<IStoryFieldGuidanceService, StoryFieldGuidanceService>();

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
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
