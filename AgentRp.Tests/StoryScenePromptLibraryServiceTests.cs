using AgentRp.Data;
using AgentRp.Services;
using Microsoft.EntityFrameworkCore;
using DbAppContext = AgentRp.Data.AppContext;

namespace AgentRp.Tests;

public sealed class StoryScenePromptLibraryServiceTests
{
    [Fact]
    public void BuildContextSummary_MatchesAssembledDefaultSections()
    {
        var context = CreateGenerationContext();

        var builder = new System.Text.StringBuilder();
        foreach (var section in StorySceneContextSectionDefaults.OrderedSections)
            StorySceneSharedPromptBuilder.AppendContextSection(builder, context, section);

        Assert.Equal(StorySceneSharedPromptBuilder.BuildContextSummary(context), builder.ToString().TrimEnd());
    }

    [Fact]
    public async Task RenderPlanningPromptAsync_DefaultMatchesCurrentBuilder()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var context = CreateGenerationContext();
        var request = new PostStorySceneMessage(
            threadId,
            context.Actor.CharacterId,
            StoryScenePostMode.GuidedAi,
            null,
            "Lean into the reveal.",
            RequestedTurnShape: StoryTurnShape.Brief);

        var rendered = await service.RenderPlanningPromptAsync(threadId, request, context, CancellationToken.None);

        Assert.Equal(StoryScenePlanningPromptBuilder.BuildSystemPrompt(), rendered.SystemPrompt);
        Assert.Equal(StoryScenePlanningPromptBuilder.BuildUserPrompt(request, context), rendered.UserPrompt);
    }

    [Fact]
    public async Task RenderAppearancePromptAsync_DefaultMatchesCurrentBuilder()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var characters = new[]
        {
            new StorySceneAppearancePromptCharacter("Ava", "Gray coat."),
            new StorySceneAppearancePromptCharacter("Ben", "Rolled sleeves.")
        };
        var transcript = CreateGenerationContext().TranscriptSinceSnapshot;

        var rendered = await service.RenderAppearancePromptAsync(
            threadId,
            characters,
            transcript,
            StoryContentIntensity.Allowed,
            StoryContentIntensity.Forbidden,
            CancellationToken.None);

        Assert.Equal(StorySceneAppearancePromptBuilder.BuildSystemPrompt(), rendered.SystemPrompt);
        Assert.Equal(
            StorySceneAppearancePromptBuilder.BuildUserPrompt(characters, transcript, StoryContentIntensity.Allowed, StoryContentIntensity.Forbidden),
            rendered.UserPrompt);
    }

    [Fact]
    public async Task RenderSelectionPromptAsync_DefaultMatchesCurrentBuilder()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var context = CreateGenerationContext();
        var activeSpeaker = context.Actor;
        var candidates = context.Characters.Where(x => x.CharacterId != activeSpeaker.CharacterId).ToList();
        var appearance = new StorySceneAppearanceResolution(
            null,
            context.Characters.Select(x => new StorySceneCharacterAppearanceView(x.CharacterId, x.Name, x.CurrentAppearance)).ToList(),
            context.TranscriptSinceSnapshot);

        var rendered = await service.RenderSelectionPromptAsync(
            threadId,
            activeSpeaker,
            candidates,
            context.StoryContext,
            context.CurrentLocation,
            context.TranscriptSinceSnapshot,
            appearance,
            "Let Ben answer.",
            CancellationToken.None);

        Assert.Equal(StorySceneResponderSelectionPromptBuilder.BuildSystemPrompt(), rendered.SystemPrompt);
        Assert.Equal(
            StorySceneResponderSelectionPromptBuilder.BuildUserPrompt(
                activeSpeaker,
                candidates,
                context.StoryContext,
                context.CurrentLocation,
                context.TranscriptSinceSnapshot,
                appearance,
                "Let Ben answer."),
            rendered.UserPrompt);
    }

    [Fact]
    public async Task RenderProsePromptAsync_DefaultMatchesCurrentBuilder()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var request = new StoryMessageProseRequest(
            StoryScenePostMode.GuidedAi,
            "Lean into the reveal.",
            StoryTurnShape.Silent,
            CreateGenerationContext(),
            new StoryMessagePlannerResult(
                StoryTurnShape.Silent,
                "hesitate",
                "hide the truth",
                "make the silence uncomfortable",
                "the other character just asked directly",
                "the room tightens",
                "Ava is afraid Ben already knows.",
                ["Do not answer directly"],
                []));

        var rendered = await service.RenderProsePromptAsync(threadId, request, CancellationToken.None);

        Assert.Equal(StorySceneProsePromptBuilder.BuildSystemPrompt(request), rendered.SystemPrompt);
        Assert.Equal(StorySceneProsePromptBuilder.BuildUserPrompt(request), rendered.UserPrompt);
    }

    [Fact]
    public async Task SaveLibraryAsync_PreservesComments_AndRenderStripsThem()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var library = service.GetDefaultLibrary();
        var updated = library with
        {
            Planning = library.Planning with
            {
                UserPromptTemplate = "Before /* editor note */ {actor.name}"
            }
        };

        await service.SaveLibraryAsync(new UpdateStoryScenePromptLibrary(threadId, updated), CancellationToken.None);
        var saved = await service.GetLibraryAsync(threadId, CancellationToken.None);
        var rendered = await service.RenderPlanningPromptAsync(
            threadId,
            new PostStorySceneMessage(threadId, CreateGenerationContext().Actor.CharacterId, StoryScenePostMode.AutomaticAi, null, null),
            CreateGenerationContext(),
            CancellationToken.None);

        Assert.Contains("/* editor note */", saved.Planning.UserPromptTemplate);
        Assert.Equal("Before  Ava", rendered.UserPrompt);
    }

    [Fact]
    public async Task SaveLibraryAsync_RejectsUnknownPlaceholder()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var library = service.GetDefaultLibrary() with
        {
            Planning = service.GetDefaultLibrary().Planning with { UserPromptTemplate = "{nope}" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveLibraryAsync(new UpdateStoryScenePromptLibrary(threadId, library), CancellationToken.None));

        Assert.Contains("{nope}", exception.Message);
    }

    [Fact]
    public async Task SaveLibraryAsync_RejectsPlaceholderFromWrongStage()
    {
        var threadId = Guid.NewGuid();
        var service = await CreateServiceAsync(threadId);
        var library = service.GetDefaultLibrary() with
        {
            Appearance = service.GetDefaultLibrary().Appearance with { UserPromptTemplate = "{planner.beat}" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveLibraryAsync(new UpdateStoryScenePromptLibrary(threadId, library), CancellationToken.None));

        Assert.Contains("not available", exception.Message);
    }

    [Fact]
    public async Task SaveLibraryAsync_IsPerChat()
    {
        var firstThreadId = Guid.NewGuid();
        var secondThreadId = Guid.NewGuid();
        var service = await CreateServiceAsync(firstThreadId, secondThreadId);
        var firstLibrary = service.GetDefaultLibrary() with
        {
            Planning = service.GetDefaultLibrary().Planning with { SystemPrompt = "custom planning" }
        };

        await service.SaveLibraryAsync(new UpdateStoryScenePromptLibrary(firstThreadId, firstLibrary), CancellationToken.None);

        Assert.Equal("custom planning", (await service.GetLibraryAsync(firstThreadId, CancellationToken.None)).Planning.SystemPrompt);
        Assert.Equal(service.GetDefaultLibrary().Planning.SystemPrompt, (await service.GetLibraryAsync(secondThreadId, CancellationToken.None)).Planning.SystemPrompt);
    }

    private static async Task<StoryScenePromptLibraryService> CreateServiceAsync(params Guid[] threadIds)
    {
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<DbAppContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        await using var dbContext = await factory.CreateDbContextAsync();
        foreach (var threadId in threadIds)
        {
            var thread = new ChatThread
            {
                Id = threadId,
                Title = "Test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            dbContext.ChatThreads.Add(thread);
            dbContext.ChatStories.Add(new ChatStory
            {
                ChatThreadId = threadId,
                Thread = thread,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        await dbContext.SaveChangesAsync();
        return new StoryScenePromptLibraryService(factory);
    }

    private static StorySceneGenerationContext CreateGenerationContext()
    {
        var actorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        return new StorySceneGenerationContext(
            new StorySceneActorContext(
                actorId,
                "Ava",
                false,
                "A guarded detective.",
                "Gray coat.",
                "Dry and clipped.",
                "The missing ledger.",
                "Deflects pressure.",
                "Never lies outright.",
                "Trusts Ben reluctantly.",
                "Justice matters.",
                "Find the ledger first.",
                string.Empty,
                "Ava found a torn page."),
            new StorySceneLocationContext(Guid.NewGuid(), "Archive", "Dusty records room.", "One window is cracked."),
            [
                new StorySceneCharacterContext(
                    actorId,
                    "Ava",
                    "A guarded detective.",
                    "Gray coat.",
                    "Ava stands by the cabinet.",
                    "Dry and clipped.",
                    "The missing ledger.",
                    "Deflects pressure.",
                    "Never lies outright.",
                    "Trusts Ben reluctantly.",
                    "Justice matters.",
                    "Find the ledger first.",
                    true),
                new StorySceneCharacterContext(
                    otherId,
                    "Ben",
                    "A nervous archivist.",
                    "Rolled sleeves.",
                    "Ben grips a folder.",
                    "Soft-spoken.",
                    "A copied key.",
                    "Fidgets.",
                    "Avoids police.",
                    "Owes Ava.",
                    "Preservation matters.",
                    "Keep his job.",
                    true)
            ],
            [new StorySceneObjectContext(Guid.NewGuid(), "Ledger", "A black book.", "Its lock is broken.")],
            new StoryNarrativeSettingsView("Noir", "Rainy city", "Tense", "Find the ledger.", StoryContentIntensity.Allowed, StoryContentIntensity.Forbidden),
            "A ledger went missing.",
            new StoryChatSnapshotSummaryView(Guid.NewGuid(), "They reached the archive.", DateTime.UtcNow, messageId, messageId, DateTime.UtcNow, 1, 1, 0),
            [new StorySceneTranscriptMessage(messageId, DateTime.UtcNow, "Ben", false, "\"You already know, don't you?\"", otherId, "Ben hopes Ava leaves.")],
            [new StorySceneTranscriptMessage(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-5), "Ava", false, "*Ava pocketed the torn page.*", actorId, "Ava wants to compare the page later.")]);
    }

    private sealed class TestDbContextFactory(DbContextOptions<DbAppContext> options) : IDbContextFactory<DbAppContext>
    {
        public DbAppContext CreateDbContext() => new(options);

        public Task<DbAppContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
