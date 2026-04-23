using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentRp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DbAppContext = AgentRp.Data.AppContext;
using DbChatMessage = AgentRp.Data.ChatMessage;

namespace AgentRp.Services;

public sealed class StorySceneChatService(
    IDbContextFactory<DbAppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IStoryChatSnapshotService storyChatSnapshotService,
    IStoryChatAppearanceService storyChatAppearanceService,
    IAgentCatalog agentCatalog,
    ILogger<StorySceneChatService> logger) : IStorySceneChatService
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<StorySceneChatState?> GetChatStateAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken);
        if (thread is null)
            return null;

        var story = await dbContext.ChatStories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken)
            ?? new ChatStory
            {
                ChatThreadId = threadId,
                CreatedUtc = thread.CreatedUtc,
                UpdatedUtc = thread.UpdatedUtc
            };
        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var runs = await dbContext.ProcessRuns
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .Include(x => x.Steps)
            .OrderBy(x => x.StartedUtc)
            .ToListAsync(cancellationToken);
        var characters = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedSpeakerId = ResolveSelectedSpeakerId(thread.SelectedSpeakerCharacterId, characters);
        var speakers = BuildSpeakers(story, characters, selectedSpeakerId);
        var selectedSpeaker = speakers.First(x => x.IsSelected);
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(messages, thread.ActiveLeafMessageId);
        var path = selectedLeafMessageId.HasValue
            ? BuildSelectedPath(messages, selectedLeafMessageId.Value)
            : [];
        var latestSnapshot = selectedLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetLatestSnapshotAsync(threadId, selectedLeafMessageId.Value, cancellationToken)
            : null;
        var snapshots = selectedLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetSnapshotsForPathAsync(threadId, selectedLeafMessageId.Value, cancellationToken)
            : [];
        var appearanceEntries = path.Count == 0
            ? []
            : await storyChatAppearanceService.GetEntriesForPathAsync(threadId, path, story, cancellationToken);
        var snapshotCandidateMessageIds = StoryChatSnapshotService.GetSnapshotCandidateMessageIds(path, latestSnapshot).ToHashSet();
        var processMap = runs
            .Where(x => x.TargetMessageId.HasValue)
            .ToDictionary(x => x.TargetMessageId!.Value, MapProcess);
        var childrenLookup = messages
            .OrderBy(x => x.CreatedUtc)
            .ToLookup(x => x.ParentMessageId);
        var descendantCounts = BuildDescendantCounts(messages);
        var transcript = BuildTranscript(path, childrenLookup, descendantCounts, selectedSpeakerId, characters, processMap, snapshotCandidateMessageIds, snapshots, appearanceEntries);
        var rootBranchNavigator = BuildBranchNavigator(
            parentMessageId: null,
            selectedBranchMessageId: path.FirstOrDefault()?.Id,
            branchMessages: childrenLookup[null].ToList(),
            allMessages: messages,
            characters: characters);

        var currentLocationName = story.Scene.CurrentLocationId.HasValue
            ? story.Locations.Entries.FirstOrDefault(x => x.Id == story.Scene.CurrentLocationId.Value)?.Name
            : null;
        var selectedAgentName = agentCatalog.NormalizeSelectedAgentName(thread.SelectedAgentName);

        return new StorySceneChatState(
            thread.Id,
            thread.Title,
            currentLocationName,
            selectedLeafMessageId,
            selectedSpeaker,
            speakers,
            rootBranchNavigator,
            transcript,
            selectedAgentName,
            agentCatalog.HasEnabledAgents);
    }

    public async Task SelectSpeakerAsync(Guid threadId, Guid? speakerCharacterId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Selecting the active speaker failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken);

        ValidateSpeaker(story, speakerCharacterId);
        thread.SelectedSpeakerCharacterId = speakerCharacterId;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(threadId);
    }

    public async Task PostMessageAsync(PostStorySceneMessage request, CancellationToken cancellationToken)
    {
        if (request.Mode == StoryScenePostMode.Manual)
        {
            await PostManualMessageAsync(request, cancellationToken);
            return;
        }

        await PostGeneratedMessageAsync(request, cancellationToken);
    }

    public async Task UpdateMessageAsync(ChatMessageUpdate request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new InvalidOperationException("Saving the edited scene message failed because the message text was empty.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Saving the edited scene message failed because the selected chat could not be found.");
        var message = thread.Messages.FirstOrDefault(x => x.Id == request.MessageId)
            ?? throw new InvalidOperationException("Saving the edited scene message failed because the selected message could not be found.");

        var trimmedContent = request.Content.Trim();
        var previousContent = message.Content;
        message.Content = trimmedContent;
        message.SourceProcessRunId = null;
        thread.UpdatedUtc = DateTime.UtcNow;

        if (!message.ParentMessageId.HasValue
            && string.Equals(thread.Title, BuildThreadTitle(previousContent), StringComparison.Ordinal))
            thread.Title = BuildThreadTitle(trimmedContent);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    public async Task CreateBranchAsync(BranchStorySceneMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new InvalidOperationException("Creating the edited scene branch failed because the message text was empty.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Creating the edited scene branch failed because the selected chat could not be found.");
        var sourceMessage = thread.Messages.FirstOrDefault(x => x.Id == request.SourceMessageId)
            ?? throw new InvalidOperationException("Creating the edited scene branch failed because the source message could not be found.");

        var now = DateTime.UtcNow;
        var branchedMessage = new DbChatMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            Role = sourceMessage.Role,
            MessageKind = sourceMessage.MessageKind,
            Content = request.Content.Trim(),
            CreatedUtc = now,
            SpeakerCharacterId = sourceMessage.SpeakerCharacterId,
            GenerationMode = StoryScenePostMode.Manual,
            SourceProcessRunId = null,
            ParentMessageId = sourceMessage.ParentMessageId,
            EditedFromMessageId = sourceMessage.Id
        };

        dbContext.ChatMessages.Add(branchedMessage);
        thread.ActiveLeafMessageId = branchedMessage.Id;
        thread.SelectedSpeakerCharacterId = branchedMessage.SpeakerCharacterId;
        thread.UpdatedUtc = now;

        if (!sourceMessage.ParentMessageId.HasValue
            && string.Equals(thread.Title, BuildThreadTitle(sourceMessage.Content), StringComparison.Ordinal))
            thread.Title = BuildThreadTitle(branchedMessage.Content);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    public async Task SelectBranchAsync(Guid threadId, Guid leafMessageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Selecting the branch failed because the selected chat could not be found.");
        var exists = await dbContext.ChatMessages.AnyAsync(x => x.ThreadId == threadId && x.Id == leafMessageId, cancellationToken);
        if (!exists)
            throw new InvalidOperationException("Selecting the branch failed because the selected continuation could not be found.");

        thread.ActiveLeafMessageId = leafMessageId;
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(threadId);
    }

    public async Task DeleteMessageAsync(DeleteStorySceneMessage request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Deleting the scene message failed because the selected chat could not be found.");
        var messages = await dbContext.ChatMessages
            .Where(x => x.ThreadId == request.ThreadId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var targetMessage = messages.FirstOrDefault(x => x.Id == request.MessageId)
            ?? throw new InvalidOperationException("Deleting the scene message failed because the selected message could not be found.");

        var childrenLookup = messages.ToLookup(x => x.ParentMessageId);
        var descendantIds = CollectDescendantIds(targetMessage.Id, childrenLookup);
        var deletedIds = request.Mode == StorySceneDeleteMode.Branch
            ? descendantIds
            : [targetMessage.Id];

        if (request.Mode == StorySceneDeleteMode.SingleMessage)
        {
            foreach (var child in messages.Where(x => x.ParentMessageId == targetMessage.Id))
                child.ParentMessageId = targetMessage.ParentMessageId;
        }

        var deletedIdSet = deletedIds.ToHashSet();
        var affectedRunIds = await dbContext.ProcessRuns
            .Where(x => x.ThreadId == request.ThreadId
                && (deletedIdSet.Contains(x.UserMessageId)
                    || (x.AssistantMessageId.HasValue && deletedIdSet.Contains(x.AssistantMessageId.Value))
                    || (x.TargetMessageId.HasValue && deletedIdSet.Contains(x.TargetMessageId.Value))))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (affectedRunIds.Count > 0)
        {
            var runsToDelete = await dbContext.ProcessRuns
                .Where(x => affectedRunIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            dbContext.ProcessRuns.RemoveRange(runsToDelete);
        }

        var messagesToDelete = messages.Where(x => deletedIdSet.Contains(x.Id)).ToList();
        dbContext.ChatMessages.RemoveRange(messagesToDelete);

        var survivingMessages = messages.Where(x => !deletedIdSet.Contains(x.Id)).ToList();
        thread.ActiveLeafMessageId = ResolveActiveLeafAfterDeletion(thread.ActiveLeafMessageId, targetMessage, deletedIdSet, survivingMessages);
        thread.SelectedSpeakerCharacterId = ResolveSelectedSpeakerAfterDeletion(thread.SelectedSpeakerCharacterId, survivingMessages, thread.ActiveLeafMessageId);
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(request.ThreadId);
    }

    public async Task UpdateAppearanceEntryAsync(UpdateStorySceneAppearanceEntry request, CancellationToken cancellationToken)
    {
        await storyChatAppearanceService.UpdateLatestEntryAsync(request, cancellationToken);
        PublishWorkspaceRefresh(request.ThreadId);
    }

    private async Task PostManualMessageAsync(PostStorySceneMessage request, CancellationToken cancellationToken)
    {
        var manualText = request.ManualText?.Trim();
        if (string.IsNullOrWhiteSpace(manualText))
            throw new InvalidOperationException("Posting the manual scene message failed because the message text was empty.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Posting the manual scene message failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);

        ValidateSpeaker(story, request.SpeakerCharacterId);

        var now = DateTime.UtcNow;
        var message = new DbChatMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            Role = AgentRp.Data.ChatRole.User,
            MessageKind = ResolveMessageKind(request.SpeakerCharacterId),
            Content = manualText,
            CreatedUtc = now,
            SpeakerCharacterId = request.SpeakerCharacterId,
            GenerationMode = StoryScenePostMode.Manual,
            ParentMessageId = thread.ActiveLeafMessageId
        };
        dbContext.ChatMessages.Add(message);

        thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
        thread.UpdatedUtc = now;
        thread.ActiveLeafMessageId = message.Id;
        if (thread.Title == "New Chat")
            thread.Title = BuildThreadTitle(manualText);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    private async Task PostGeneratedMessageAsync(PostStorySceneMessage request, CancellationToken cancellationToken)
    {
        var trimmedGuidancePrompt = request.GuidancePrompt?.Trim();

        if (request.Mode == StoryScenePostMode.GuidedAi && string.IsNullOrWhiteSpace(trimmedGuidancePrompt))
            throw new InvalidOperationException("Planning the guided scene message failed because the guidance prompt was empty.");

        StorySceneGenerationContext generationContext;
        StorySceneAppearanceResolution appearanceResolution;
        ConfiguredAgent agent;
        Guid messageId;
        Guid runId;

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
                ?? throw new InvalidOperationException("Generating the scene message failed because the selected chat could not be found.");
            agent = agentCatalog.GetAgentOrDefault(thread.SelectedAgentName)
                ?? throw new InvalidOperationException("Generating the scene message failed because no AI provider is configured for this chat.");
            var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);

            ValidateSpeaker(story, request.SpeakerCharacterId);
            var actor = BuildActorContext(
                request.SpeakerCharacterId,
                story.Characters.Entries
                    .Where(x => !x.IsArchived)
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

            var now = DateTime.UtcNow;
            var targetMessage = new DbChatMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Thread = thread,
                Role = AgentRp.Data.ChatRole.Assistant,
                MessageKind = ResolveMessageKind(request.SpeakerCharacterId),
                Content = string.Empty,
                CreatedUtc = now,
                SpeakerCharacterId = request.SpeakerCharacterId,
                GenerationMode = request.Mode,
                ParentMessageId = thread.ActiveLeafMessageId
            };

            var processContext = new StoryMessageProcessContext(
                request.Mode,
                trimmedGuidancePrompt,
                null,
                null,
                null,
                null,
                null,
                BuildInitialStepArtifacts(request));
            var processRun = new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Thread = thread,
                UserMessageId = targetMessage.Id,
                AssistantMessageId = targetMessage.Id,
                TargetMessageId = targetMessage.Id,
                ActorCharacterId = request.SpeakerCharacterId,
                Summary = BuildInitialRunSummary(actor, request.Mode),
                Stage = "Appearance",
                ContextJson = SerializeContext(processContext),
                Status = ProcessRunStatus.Running,
                StartedUtc = now,
                Steps =
                [
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 0,
                        Title = "Appearance",
                        Summary = "Resolving the current appearance of characters in the active scene.",
                        Detail = "The appearance stage is reviewing the latest branch-local appearance block and recent transcript.",
                        IconCssClass = "fa-regular fa-shirt",
                        Status = ProcessStepStatus.Running,
                        StartedUtc = now
                    },
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 1,
                        Title = "Planning",
                        Summary = "Determining the intent and goals of the next message.",
                        Detail = "The planner will run after the latest current appearance is resolved.",
                        IconCssClass = "fa-regular fa-map",
                        Status = ProcessStepStatus.Pending
                    },
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 2,
                        Title = "Writing",
                        Summary = "Drafting the scene message in the actor's voice.",
                        Detail = "The prose stage will turn the planner result into the final message.",
                        IconCssClass = "fa-regular fa-pen-line",
                        Status = ProcessStepStatus.Pending
                    }
                ]
            };

            targetMessage.SourceProcessRunId = processRun.Id;
            dbContext.ChatMessages.Add(targetMessage);
            dbContext.ProcessRuns.Add(processRun);

            thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
            thread.UpdatedUtc = now;
            thread.ActiveLeafMessageId = targetMessage.Id;
            if (thread.Title == "New Chat")
                thread.Title = BuildThreadTitle(BuildThreadSeedText(trimmedGuidancePrompt, actor));

            await dbContext.SaveChangesAsync(cancellationToken);

            messageId = targetMessage.Id;
            runId = processRun.Id;
        }

        PublishWorkspaceRefresh(request.ThreadId);

        try
        {
            var generationBuild = await BuildGenerationContextAsync(request.ThreadId, request.SpeakerCharacterId, cancellationToken);
            generationContext = generationBuild.Context;
            appearanceResolution = generationBuild.Appearance;
            await UpdateAppearanceCompletionAsync(request.ThreadId, runId, request.Mode, trimmedGuidancePrompt, generationContext, appearanceResolution, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            var planner = await RunPlannerStageAsync(agent, request, generationContext, cancellationToken);
            await UpdatePlannerCompletionAsync(request.ThreadId, runId, planner, generationContext, appearanceResolution, trimmedGuidancePrompt, request.Mode, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            var proseRequest = new StoryMessageProseRequest(request.Mode, trimmedGuidancePrompt, generationContext, planner);
            var proseText = await RunProseStageAsync(agent, proseRequest, cancellationToken);
            await StreamMessageAsync(request.ThreadId, messageId, proseText, cancellationToken);
            await CompleteRunAsync(request.ThreadId, runId, proseRequest, appearanceResolution, proseText, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Generating the story scene message failed for thread {ThreadId} and speaker {SpeakerCharacterId}.", request.ThreadId, request.SpeakerCharacterId);
            await FailRunAsync(request.ThreadId, runId, exception, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);
            throw;
        }
    }

    private async Task<StoryMessagePlannerResult> RunPlannerStageAsync(
        ConfiguredAgent agent,
        PostStorySceneMessage request,
        StorySceneGenerationContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildPlannerSystemPrompt()),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildPlannerUserPrompt(request, context))
        ];

        var response = await agent.ChatClient.GetResponseAsync<PlannerStageResponse>(
            messages,
            options: BuildPlannerOptions(),
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var planner = response.Result;

        return new StoryMessagePlannerResult(
            RequireValue(planner.Intent, "planner intent"),
            RequireValue(planner.ImmediateGoal, "planner immediate goal"),
            RequireItems(planner.EmotionalStance, "planner emotional stance"),
            NormalizeItems(planner.TargetAddressees),
            NormalizeItems(planner.RequiredFactualBeats),
            NormalizeItems(planner.Guardrails),
            RequireValue(planner.PlanningSummary, "planner summary"));
    }

    private async Task<string> RunProseStageAsync(ConfiguredAgent agent, StoryMessageProseRequest request, CancellationToken cancellationToken)
    {        
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildProseSystemPrompt(request.Context.Actor.IsNarrator)),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildProseUserPrompt(request))
        ];

        var response = await agent.ChatClient.GetResponseAsync(
            messages,
            options: new ChatOptions { Temperature = 0.9f },
            cancellationToken: cancellationToken);
        var prose = response.Text?.Trim();
        var normalizedProse = string.IsNullOrWhiteSpace(prose)
            ? string.Empty
            : StripLeadingActorLabel(prose, request.Context.Actor);

        if (!string.IsNullOrWhiteSpace(normalizedProse))
            return normalizedProse;

        throw new InvalidOperationException($"Writing the scene message as {request.Context.Actor.Name} failed because the prose stage returned an empty message.");
    }

    private async Task UpdateAppearanceCompletionAsync(
        Guid threadId,
        Guid runId,
        StoryScenePostMode mode,
        string? guidancePrompt,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        run.Stage = "Planning";
        run.Summary = appearance.LatestEntry?.Summary ?? $"Resolved current appearance for the active scene as {context.Actor.Name}.";
        run.PlanningStartedUtc = now;
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            mode,
            guidancePrompt,
            context,
            appearance,
            null,
            null,
            null,
            BuildAppearanceCompletedStepArtifacts(context, appearance)));

        foreach (var step in run.Steps)
        {
            if (step.SortOrder == 0)
            {
                CompleteProcessStep(
                    step,
                    now,
                    appearance.LatestEntry?.Summary ?? "Current appearance was resolved for the active branch.",
                    BuildAppearanceDetail(appearance));
            }
            else if (step.SortOrder == 1)
            {
                StartProcessStep(
                    step,
                    now,
                    "The planner is reviewing the actor, scene, appearance state, snapshot summary, and recent transcript.");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdatePlannerCompletionAsync(
        Guid threadId,
        Guid runId,
        StoryMessagePlannerResult planner,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        string? guidancePrompt,
        StoryScenePostMode mode,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        run.Stage = "Writing";
        run.Summary = planner.PlanningSummary;
        run.PlanningCompletedUtc = now;
        run.ProseStartedUtc = now;
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            mode,
            guidancePrompt,
            context,
            appearance,
            planner,
            null,
            null,
            BuildPlanningCompletedStepArtifacts(mode, guidancePrompt, context, appearance, planner)));

        foreach (var step in run.Steps)
        {
            if (step.SortOrder == 0)
            {
                step.Summary = appearance.LatestEntry?.Summary ?? "The latest branch-local appearance was reused without changes.";
                step.Detail = TruncateProcessDetail(BuildAppearanceDetail(appearance));
            }
            else if (step.SortOrder == 1)
            {
                CompleteProcessStep(step, now, planner.PlanningSummary, BuildPlannerDetail(planner));
            }
            else if (step.SortOrder == 2)
            {
                StartProcessStep(
                    step,
                    now,
                    "The prose stage is turning the approved plan into the final scene message.");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task StreamMessageAsync(Guid threadId, Guid messageId, string proseText, CancellationToken cancellationToken)
    {
        var chunks = ChunkMessage(proseText).ToList();
        var builder = new StringBuilder();

        foreach (var chunk in chunks)
        {
            builder.Append(chunk);
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var message = await dbContext.ChatMessages
                .FirstAsync(x => x.Id == messageId && x.ThreadId == threadId, cancellationToken);
            var thread = await dbContext.ChatThreads
                .FirstAsync(x => x.Id == threadId, cancellationToken);

            message.Content = builder.ToString();
            thread.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            PublishWorkspaceRefresh(threadId);
        }
    }

    private async Task CompleteRunAsync(
        Guid threadId,
        Guid runId,
        StoryMessageProseRequest proseRequest,
        StorySceneAppearanceResolution appearance,
        string finalMessage,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        run.Stage = null;
        run.Status = ProcessRunStatus.Completed;
        run.ProseCompletedUtc = now;
        run.CompletedUtc = now;
        run.Summary = $"Completed a {DescribeMode(proseRequest.Mode).ToLowerInvariant()} message as {proseRequest.Context.Actor.Name}.";
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            proseRequest.Mode,
            proseRequest.GuidancePrompt,
            proseRequest.Context,
            appearance,
            proseRequest.Planner,
            proseRequest,
            finalMessage,
            BuildCompletedStepArtifacts(proseRequest, appearance, finalMessage)));

        foreach (var step in run.Steps)
        {
            if (step.SortOrder == 2)
                CompleteProcessStep(step, now, "The final message was written and saved to the transcript.", BuildProseDetail(proseRequest, finalMessage));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailRunAsync(Guid threadId, Guid runId, Exception exception, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        if (run is null)
            return;

        var now = DateTime.UtcNow;
        run.Status = ProcessRunStatus.Failed;
        run.CompletedUtc = now;
        run.Summary = $"The {run.Stage?.ToLowerInvariant() ?? "message generation"} stage failed.";

        var activeStep = run.Steps
            .OrderBy(x => x.SortOrder)
            .FirstOrDefault(x => x.Status is ProcessStepStatus.Running or ProcessStepStatus.Pending);
        if (activeStep is not null)
        {
            FailProcessStep(activeStep, now, $"{activeStep.Title} failed while generating the scene message. Cause: {exception.Message}");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<GenerationBuildResult> BuildGenerationContextAsync(
        Guid threadId,
        Guid? speakerCharacterId,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("Generating the scene message failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, threadId, cancellationToken);
        var allMessages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == thread.Id)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(allMessages, thread.ActiveLeafMessageId);
        var selectedPath = selectedLeafMessageId.HasValue
            ? BuildSelectedPath(allMessages, selectedLeafMessageId.Value)
            : [];
        var latestSnapshot = selectedLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetLatestSnapshotAsync(thread.Id, selectedLeafMessageId.Value, cancellationToken)
            : null;
        var characters = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locations = story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = story.Items.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actor = BuildActorContext(speakerCharacterId, characters);
        var currentLocation = story.Scene.CurrentLocationId.HasValue
            ? locations.FirstOrDefault(x => x.Id == story.Scene.CurrentLocationId.Value)
            : null;
        var transcriptSinceSnapshot = latestSnapshot is null
            ? selectedPath
            : selectedPath.Where(x => x.CreatedUtc > latestSnapshot.CoveredThroughUtc).ToList();
        var appearance = await storyChatAppearanceService.ResolveLatestAppearanceAsync(
            threadId,
            selectedPath,
            story,
            writeChanges: true,
            cancellationToken);
        var currentAppearanceLookup = appearance.EffectiveCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);

        var context = new StorySceneGenerationContext(
            actor,
            currentLocation is null
                ? null
                : new StorySceneLocationContext(currentLocation.Id, currentLocation.Name, currentLocation.Summary, currentLocation.Details),
            characters.Select(character => new StorySceneCharacterContext(
                    character.Id,
                    character.Name,
                    character.Summary,
                    character.GeneralAppearance,
                    currentAppearanceLookup.TryGetValue(character.Id, out var currentAppearance) ? currentAppearance : string.Empty,
                    character.CorePersonality,
                    character.Relationships,
                    character.PreferencesBeliefs,
                    story.Scene.PresentCharacterIds.Contains(character.Id)))
                .ToList(),
            items.Where(item => story.Scene.PresentItemIds.Contains(item.Id))
                .Select(item => new StorySceneObjectContext(item.Id, item.Name, item.Summary, item.Details))
                .ToList(),
            BuildHistorySummary(story.History),
            latestSnapshot,
            transcriptSinceSnapshot.Select(message => new StorySceneTranscriptMessage(
                    message.Id,
                    message.CreatedUtc,
                    ResolveSpeakerName(message, characters),
                    message.MessageKind == ChatMessageKind.Narration,
                    message.Content))
                .ToList(),
            appearance.TranscriptSinceLatestEntry);

        return new GenerationBuildResult(context, appearance);
    }

    private static StorySceneActorContext BuildActorContext(Guid? speakerCharacterId, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (!speakerCharacterId.HasValue)
        {
            return new StorySceneActorContext(
                null,
                "Narrator",
                true,
                "An always-present narrator who injects reliable scene facts and framing details.",
                "Speak in concise descriptive prose. Introduce or clarify facts without inventing contradictions.",
                BuildNarratorHiddenKnowledge(characters));
        }

        var character = characters.FirstOrDefault(x => x.Id == speakerCharacterId.Value)
            ?? throw new InvalidOperationException("Building the scene context failed because the selected speaker could not be found.");

        var details = new StringBuilder();
        details.AppendLine($"Summary: {character.Summary}");
        details.AppendLine($"General appearance: {character.GeneralAppearance}");
        details.AppendLine($"Core personality: {character.CorePersonality}");
        details.AppendLine($"Relationships: {character.Relationships}");
        details.AppendLine($"Preferences / beliefs: {character.PreferencesBeliefs}");
        if (!string.IsNullOrWhiteSpace(character.PrivateMotivations))
            details.AppendLine($"Private motivations: {character.PrivateMotivations}");

        return new StorySceneActorContext(
            character.Id,
            character.Name,
            false,
            character.Summary,
            details.ToString().TrimEnd(),
            string.Empty);
    }

    private static string BuildNarratorHiddenKnowledge(IReadOnlyList<StoryCharacterDocument> characters)
    {
        var hiddenDetails = characters
            .Where(x => !string.IsNullOrWhiteSpace(x.PrivateMotivations))
            .Select(x => $"{x.Name}: {x.PrivateMotivations}")
            .ToList();

        return hiddenDetails.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, hiddenDetails);
    }

    private static IReadOnlyList<StorySceneSpeakerView> BuildSpeakers(
        ChatStory story,
        IReadOnlyList<StoryCharacterDocument> characters,
        Guid? selectedSpeakerId)
    {
        var speakers = new List<StorySceneSpeakerView>
        {
            new(
                null,
                "Narrator",
                "Injects scene facts and framing details.",
                true,
                true,
                !selectedSpeakerId.HasValue)
        };

        speakers.AddRange(characters.Select(character => new StorySceneSpeakerView(
            character.Id,
            character.Name,
            character.Summary,
            false,
            story.Scene.PresentCharacterIds.Contains(character.Id),
            selectedSpeakerId == character.Id)));

        return speakers;
    }

    private static StorySceneMessageView MapMessage(
        DbChatMessage message,
        int directChildCount,
        int descendantCount,
        Guid? selectedSpeakerId,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap,
        IReadOnlySet<Guid> snapshotCandidateMessageIds,
        StorySceneBranchNavigatorView? branchNavigator)
    {
        var canonicalSpeakerName = ResolveSpeakerName(message, characters);
        var isSelectedSpeaker = selectedSpeakerId == message.SpeakerCharacterId
            || (!selectedSpeakerId.HasValue && message.MessageKind == ChatMessageKind.Narration);
        var canSaveInPlace = snapshotCandidateMessageIds.Contains(message.Id);

        return new StorySceneMessageView(
            message.Id,
            message.MessageKind,
            message.GenerationMode,
            message.Content,
            message.CreatedUtc,
            message.SpeakerCharacterId,
            canonicalSpeakerName,
            isSelectedSpeaker ? "You" : canonicalSpeakerName,
            message.MessageKind == ChatMessageKind.Narration,
            isSelectedSpeaker,
            canSaveInPlace,
            canSaveInPlace,
            branchNavigator,
            new StorySceneDeleteCapabilitiesView(
                DirectChildCount: directChildCount,
                DescendantCount: descendantCount,
                CanDeleteSingleMessage: true,
                CanDeleteBranch: descendantCount > 0),
            message.SourceProcessRunId.HasValue && processMap.TryGetValue(message.Id, out var process) ? process : null);
    }

    private static IReadOnlyList<StorySceneTranscriptNodeView> BuildTranscript(
        IReadOnlyList<DbChatMessage> selectedPath,
        ILookup<Guid?, DbChatMessage> childrenLookup,
        IReadOnlyDictionary<Guid, int> descendantCounts,
        Guid? selectedSpeakerId,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap,
        IReadOnlySet<Guid> snapshotCandidateMessageIds,
        IReadOnlyList<StorySceneSnapshotView> snapshots,
        IReadOnlyList<StorySceneAppearanceEntryView> appearanceEntries)
    {
        var transcript = new List<StorySceneTranscriptNodeView>(selectedPath.Count + snapshots.Count);
        var snapshotsByMessageId = snapshots
            .GroupBy(x => x.CoveredThroughMessageId)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.CreatedUtc).ToList());
        var appearanceByMessageId = appearanceEntries
            .GroupBy(x => x.CoveredThroughMessageId)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.CreatedUtc).ToList());
        var sequence = 1;

        for (var index = 0; index < selectedPath.Count; index++)
        {
            var message = selectedPath[index];
            var selectedBranchMessageId = index < selectedPath.Count - 1 ? selectedPath[index + 1].Id : (Guid?)null;
            var children = childrenLookup[message.Id].ToList();
            var branchNavigator = BuildBranchNavigator(
                message.Id,
                selectedBranchMessageId,
                children,
                childrenLookup.SelectMany(x => x).DistinctBy(x => x.Id).OrderBy(x => x.CreatedUtc).ToList(),
                characters);
            var messageAppearance = appearanceByMessageId.TryGetValue(message.Id, out var appearancesForMessage)
                ? appearancesForMessage.LastOrDefault()
                : null;

            transcript.Add(new StorySceneTranscriptNodeView(
                sequence++,
                MapMessage(
                    message,
                    children.Count,
                    descendantCounts.GetValueOrDefault(message.Id),
                    selectedSpeakerId,
                    characters,
                    processMap,
                    snapshotCandidateMessageIds,
                    branchNavigator),
                null,
                messageAppearance));

            var messageSnapshots = snapshotsByMessageId.TryGetValue(message.Id, out var snapshotsForMessage)
                ? snapshotsForMessage.Select(snapshot => new TranscriptArtifact(snapshot.CreatedUtc, snapshot, null)).ToList()
                : [];

            foreach (var artifact in messageSnapshots.OrderBy(x => x.CreatedUtc))
            {
                transcript.Add(new StorySceneTranscriptNodeView(
                    sequence++,
                    null,
                    artifact.Snapshot,
                    artifact.Appearance));
            }
        }

        return transcript;
    }

    private static StorySceneBranchNavigatorView? BuildBranchNavigator(
        Guid? parentMessageId,
        Guid? selectedBranchMessageId,
        IReadOnlyList<DbChatMessage> branchMessages,
        IReadOnlyList<DbChatMessage> allMessages,
        IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (branchMessages.Count <= 1)
            return null;

        var options = branchMessages
            .Select(branchMessage => new StorySceneBranchOptionView(
                branchMessage.Id,
                FindLatestLeafInSubtree(allMessages, branchMessage.Id),
                BuildBranchPreview(branchMessage.Content),
                ResolveSpeakerName(branchMessage, characters),
                branchMessage.CreatedUtc,
                selectedBranchMessageId == branchMessage.Id))
            .ToList();
        var selectedOptionIndex = options.FindIndex(x => x.IsSelected);

        return new StorySceneBranchNavigatorView(
            parentMessageId,
            options,
            selectedOptionIndex < 0 ? 1 : selectedOptionIndex + 1);
    }

    private static StorySceneMessageProcessView MapProcess(ProcessRun source)
    {
        var processContext = DeserializeContext(source.ContextJson);
        var steps = source.Steps
            .OrderBy(step => step.SortOrder)
            .Select(step => new StorySceneProcessStepView(
                step.Id,
                step.Title,
                step.Summary,
                step.Detail,
                step.IconCssClass,
                step.Status,
                step.Status == ProcessStepStatus.Running,
                step.StartedUtc,
                step.CompletedUtc,
                ResolveStepArtifact(processContext, step)))
            .ToList();

        return new StorySceneMessageProcessView(
            source.Id,
            source.Summary,
            source.Status,
            source.Status switch
            {
                ProcessRunStatus.Completed => "Completed",
                ProcessRunStatus.Failed => "Failed",
                _ => "Running"
            },
            source.Stage,
            source.StartedUtc,
            source.CompletedUtc,
            steps,
            processContext);
    }

    private static void StartProcessStep(ProcessStep step, DateTime startedUtc, string detail)
    {
        step.Status = ProcessStepStatus.Running;
        step.StartedUtc ??= startedUtc;
        step.CompletedUtc = null;
        step.Detail = detail;
    }

    private static void CompleteProcessStep(ProcessStep step, DateTime completedUtc, string summary, string detail)
    {
        step.Status = ProcessStepStatus.Completed;
        step.StartedUtc ??= completedUtc;
        step.CompletedUtc = completedUtc;
        step.Summary = summary;
        step.Detail = TruncateProcessDetail(detail);
    }

    private static void FailProcessStep(ProcessStep step, DateTime completedUtc, string detail)
    {
        step.Status = ProcessStepStatus.Failed;
        step.StartedUtc ??= completedUtc;
        step.CompletedUtc = completedUtc;
        step.Detail = detail;
    }

    private static string BuildPlannerSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the planning stage for a story scene message generator.");
        builder.AppendLine("Decide the intent and goals of the next message before any prose is written.");
        builder.AppendLine("Return a concise structured plan only.");
        builder.AppendLine("Keep the plan grounded in the provided story context, snapshot summary, and transcript.");
        builder.AppendLine("Plan one turn only.");
        builder.AppendLine("Choose one immediate beat, not a sequence.");
        builder.AppendLine("A direct reaction from another character is allowed only if it happens immediately.");
        builder.AppendLine("Do not plan follow-up beats.");
        builder.AppendLine("Stop where the next person would naturally answer or act.");
        builder.AppendLine("Do not write the final message text.");
        return builder.ToString().TrimEnd();
    }

    private static string BuildPlannerUserPrompt(PostStorySceneMessage request, StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildContextSummary(context));
        builder.AppendLine();
        builder.AppendLine($"Posting mode: {DescribeMode(request.Mode)}");

        if (request.Mode == StoryScenePostMode.GuidedAi)
            builder.AppendLine($"Guidance prompt: {request.GuidancePrompt?.Trim()}");
        else
            builder.AppendLine("Guidance prompt: None. Infer the most natural next beat from the scene.");

        builder.AppendLine();
        AppendTurnScopeRules(builder);
        return builder.ToString().TrimEnd();
    }

    private static string BuildProseSystemPrompt(bool narrator)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the prose stage for a story scene chat.");
        builder.AppendLine("Write only the final message content for the selected actor.");
        builder.AppendLine("Stay faithful to the character, planner output, current scene facts, snapshot summary, and recent transcript.");
        builder.AppendLine("Write one turn only.");
        builder.AppendLine("Advance the scene one beat.");
        builder.AppendLine("Do not fast-forward.");
        builder.AppendLine("Do not resolve the whole exchange.");
        builder.AppendLine("Do not make other characters take major new actions unless it is an immediate reaction.");
        builder.AppendLine("Stop at the first natural pause.");
        if (narrator)
        {
            builder.AppendLine("You are speaking as the story narrator guiding the narrative, write a descriptive narration instead of dialogue.");
        }
        else 
        {
            builder.AppendLine("Write as that character speaking or acting in-scene.");
            builder.AppendLine("Include any actions, body language, hidden emotional state, and internal monologue in *asterisks* in the message.");
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildProseUserPrompt(StoryMessageProseRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildContextSummary(request.Context));
        builder.AppendLine();
        builder.AppendLine("Planner result:");
        builder.AppendLine(BuildPlannerDetail(request.Planner));
        builder.AppendLine();
        AppendTurnScopeRules(builder);
        return builder.ToString().TrimEnd();
    }

    private static void AppendTurnScopeRules(StringBuilder builder)
    {
        builder.AppendLine("Turn scope rules:");
        builder.AppendLine("- One turn only.");
        builder.AppendLine("- Advance the scene one beat.");
        builder.AppendLine("- Direct reaction is okay if it happens immediately.");
        builder.AppendLine("- Do not fast-forward into follow-up beats.");
        builder.AppendLine("- Stop at the next natural pause.");
    }

    private static string BuildContextSummary(StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Actor: {context.Actor.Name}");
        builder.AppendLine($"Narrator actor: {context.Actor.IsNarrator}");
        builder.AppendLine($"Actor summary: {context.Actor.Summary}");
        builder.AppendLine($"Actor details: {context.Actor.Details}");
        if (!string.IsNullOrWhiteSpace(context.Actor.HiddenKnowledge))
            builder.AppendLine($"Hidden knowledge: {context.Actor.HiddenKnowledge}");
        builder.AppendLine($"Current location: {context.CurrentLocation?.Name ?? "None"}");

        if (context.CurrentLocation is not null)
        {
            builder.AppendLine($"Location summary: {context.CurrentLocation.Summary}");
            builder.AppendLine($"Location details: {context.CurrentLocation.Details}");
        }

        builder.AppendLine("Characters in the story:");
        foreach (var character in context.Characters)
            builder.AppendLine($"- {character.Name} | {(character.IsPresentInScene ? "In current scene" : "Not present")} | {character.Summary} | General appearance: {character.GeneralAppearance} | Current appearance: {FallbackText(character.CurrentAppearance)}");

        if (context.SceneObjects.Count > 0)
        {
            builder.AppendLine("Objects in the scene:");
            foreach (var item in context.SceneObjects)
                builder.AppendLine($"- {item.Name} | {item.Summary}");
        }

        builder.AppendLine($"History summary: {context.HistorySummary}");
        
        if (!string.IsNullOrEmpty(context.LatestSnapshot?.Summary))
            builder.AppendLine($"Latest snapshot summary: {context.LatestSnapshot.Summary}");

        if (context.TranscriptSinceSnapshot.Count > 0)
        {
            builder.AppendLine("Transcript since latest snapshot:");
            foreach (var message in context.TranscriptSinceSnapshot)
                builder.AppendLine($"- {message.SpeakerName}: {message.Content}");
        }

        if (context.TranscriptSinceLatestAppearance.Count > 0)
        {
            builder.AppendLine("Transcript since latest appearance entry:");
            foreach (var message in context.TranscriptSinceLatestAppearance)
                builder.AppendLine($"- {message.SpeakerName}: {message.Content}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPlannerDetail(StoryMessagePlannerResult planner)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Intent: {planner.Intent}");
        builder.AppendLine($"Immediate goal: {planner.ImmediateGoal}");
        builder.AppendLine($"Emotional stance: {planner.EmotionalStance}");
        builder.AppendLine($"Target addressees: {FormatList(planner.TargetAddressees)}");
        builder.AppendLine($"Required factual beats: {FormatList(planner.RequiredFactualBeats)}");
        builder.AppendLine($"Guardrails: {FormatList(planner.Guardrails)}");
        builder.AppendLine($"Planning summary: {planner.PlanningSummary}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildProseDetail(StoryMessageProseRequest request, string finalMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Mode: {DescribeMode(request.Mode)}");
        if (!string.IsNullOrWhiteSpace(request.GuidancePrompt))
            builder.AppendLine($"Guidance prompt: {request.GuidancePrompt}");

        builder.AppendLine($"Actor: {request.Context.Actor.Name}");
        builder.AppendLine($"Planner summary: {request.Planner.PlanningSummary}");
        builder.AppendLine("Final message:");
        builder.AppendLine(finalMessage);
        return builder.ToString().TrimEnd();
    }

    private static string BuildAppearanceDetail(StorySceneAppearanceResolution appearance)
    {
        var builder = new StringBuilder();
        if (appearance.EffectiveCharacters.Count == 0)
        {
            builder.AppendLine("No current appearance or physical state details have been captured for this scene yet.");
        }
        else
        {
            builder.AppendLine($"Latest appearance block: {appearance.LatestEntry?.Summary ?? "None"}");
            builder.AppendLine("Effective current appearance:");
            foreach (var character in appearance.EffectiveCharacters)
                builder.AppendLine($"- {character.CharacterName}: {FallbackText(character.CurrentAppearance)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static IEnumerable<string> ChunkMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return string.Empty;
            yield break;
        }

        var index = 0;
        while (index < text.Length)
        {
            var length = Math.Min(180, text.Length - index);
            yield return text.Substring(index, length);
            index += length;
        }
    }

    private static string StripLeadingActorLabel(string text, StorySceneActorContext actor)
    {
        if (actor.IsNarrator)
            return text;

        if (!text.StartsWith(actor.Name, StringComparison.Ordinal))
            return text;

        var labelEndIndex = actor.Name.Length;
        if (text.Length == labelEndIndex || text[labelEndIndex] != ':')
            return text;

        return text[(labelEndIndex + 1)..].TrimStart();
    }

    private static string BuildHistorySummary(ChatStoryHistoryDocument history)
    {
        var facts = history.Facts.Take(3).Select(x => $"{x.Title}: {x.Summary}");
        var timeline = history.TimelineEntries.Take(3).Select(x => $"{x.Title}: {x.Summary}");
        var combined = facts.Concat(timeline).ToList();
        return combined.Count == 0 ? "No history yet." : string.Join(" | ", combined);
    }

    private static string ResolveSpeakerName(DbChatMessage message, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (message.MessageKind == ChatMessageKind.Narration)
            return "Narrator";

        if (message.SpeakerCharacterId.HasValue)
            return characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)?.Name ?? "Unknown Character";

        return "System";
    }

    private static Guid? ResolveSelectedSpeakerId(Guid? selectedSpeakerCharacterId, IReadOnlyList<StoryCharacterDocument> characters) =>
        selectedSpeakerCharacterId.HasValue && characters.Any(x => x.Id == selectedSpeakerCharacterId.Value)
            ? selectedSpeakerCharacterId
            : null;

    private static string DescribeMode(StoryScenePostMode mode) => mode switch
    {
        StoryScenePostMode.Manual => "Manual",
        StoryScenePostMode.GuidedAi => "Guided AI",
        StoryScenePostMode.AutomaticAi => "Automatic AI",
        _ => mode.ToString()
    };

    private static ChatMessageKind ResolveMessageKind(Guid? speakerCharacterId) =>
        speakerCharacterId.HasValue ? ChatMessageKind.CharacterSpeech : ChatMessageKind.Narration;

    private static string BuildInitialRunSummary(StorySceneActorContext actor, StoryScenePostMode mode) =>
        $"Preparing a {DescribeMode(mode).ToLowerInvariant()} message as {actor.Name}.";

    private static string BuildThreadSeedText(string? guidancePrompt, StorySceneActorContext actor) =>
        !string.IsNullOrWhiteSpace(guidancePrompt) ? guidancePrompt : $"Scene message as {actor.Name}";

    private static string BuildThreadTitle(string seed)
    {
        var trimmed = seed.Trim();
        if (trimmed.Length <= 48)
            return trimmed;

        return $"{trimmed[..45].TrimEnd()}...";
    }

    private static string SerializeContext(StoryMessageProcessContext context) =>
        JsonSerializer.Serialize(context, JsonSerializerOptions);

    private static IReadOnlyDictionary<Guid, int> BuildDescendantCounts(IReadOnlyList<DbChatMessage> messages)
    {
        var childrenLookup = messages.ToLookup(x => x.ParentMessageId);
        var counts = new Dictionary<Guid, int>();

        foreach (var message in messages.OrderByDescending(x => x.CreatedUtc))
            counts[message.Id] = CountDescendants(message.Id, childrenLookup, counts);

        return counts;
    }

    private static int CountDescendants(Guid messageId, ILookup<Guid?, DbChatMessage> childrenLookup, IDictionary<Guid, int> counts)
    {
        var total = 0;

        foreach (var child in childrenLookup[messageId])
        {
            counts.TryGetValue(child.Id, out var childDescendantCount);
            total += 1 + childDescendantCount;
        }

        return total;
    }

    private static Guid? ResolveSelectedLeafMessageId(IReadOnlyList<DbChatMessage> messages, Guid? selectedLeafMessageId)
    {
        if (messages.Count == 0)
            return null;

        if (selectedLeafMessageId.HasValue && messages.Any(x => x.Id == selectedLeafMessageId.Value))
            return selectedLeafMessageId.Value;

        return FindLatestLeaf(messages);
    }

    private static IReadOnlyList<DbChatMessage> BuildSelectedPath(IReadOnlyList<DbChatMessage> messages, Guid selectedLeafMessageId)
    {
        var messageMap = messages.ToDictionary(x => x.Id);
        if (!messageMap.ContainsKey(selectedLeafMessageId))
            return [];

        var path = new List<DbChatMessage>();
        Guid? currentId = selectedLeafMessageId;

        while (currentId.HasValue && messageMap.TryGetValue(currentId.Value, out var current))
        {
            path.Add(current);
            currentId = current.ParentMessageId;
        }

        path.Reverse();
        return path;
    }

    private static List<Guid> CollectDescendantIds(Guid rootMessageId, ILookup<Guid?, DbChatMessage> childrenLookup)
    {
        var descendants = new List<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootMessageId);

        while (stack.Count > 0)
        {
            var currentId = stack.Pop();
            descendants.Add(currentId);

            var children = childrenLookup[currentId].OrderByDescending(x => x.CreatedUtc).ToList();
            foreach (var child in children)
                stack.Push(child.Id);
        }

        return descendants;
    }

    private static Guid? ResolveActiveLeafAfterDeletion(
        Guid? currentActiveLeafMessageId,
        DbChatMessage targetMessage,
        IReadOnlySet<Guid> deletedIds,
        IReadOnlyList<DbChatMessage> survivingMessages)
    {
        if (survivingMessages.Count == 0)
            return null;

        if (currentActiveLeafMessageId.HasValue && !deletedIds.Contains(currentActiveLeafMessageId.Value))
            return currentActiveLeafMessageId.Value;

        var survivingMap = survivingMessages.ToDictionary(x => x.Id);
        var siblingLeaf = FindNearestSiblingLeaf(targetMessage, deletedIds, survivingMessages);
        if (siblingLeaf.HasValue)
            return siblingLeaf.Value;

        Guid? ancestorParentId = targetMessage.ParentMessageId;
        while (ancestorParentId.HasValue && survivingMap.TryGetValue(ancestorParentId.Value, out var ancestor))
        {
            var ancestorSiblingLeaf = FindNearestSiblingLeaf(ancestor, deletedIds, survivingMessages);
            if (ancestorSiblingLeaf.HasValue)
                return ancestorSiblingLeaf.Value;

            ancestorParentId = ancestor.ParentMessageId;
        }

        return FindLatestLeaf(survivingMessages);
    }

    private static Guid? FindNearestSiblingLeaf(
        DbChatMessage message,
        IReadOnlySet<Guid> deletedIds,
        IReadOnlyList<DbChatMessage> survivingMessages)
    {
        var siblings = survivingMessages
            .Where(x => x.ParentMessageId == message.ParentMessageId && x.Id != message.Id && !deletedIds.Contains(x.Id))
            .OrderBy(x => x.CreatedUtc)
            .ToList();
        if (siblings.Count == 0)
            return null;

        var preferredSibling = siblings
            .Where(x => x.CreatedUtc <= message.CreatedUtc)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefault()
            ?? siblings.OrderBy(x => x.CreatedUtc).First();

        return FindLatestLeafInSubtree(survivingMessages, preferredSibling.Id);
    }

    private static Guid? ResolveSelectedSpeakerAfterDeletion(
        Guid? currentSelectedSpeakerCharacterId,
        IReadOnlyList<DbChatMessage> survivingMessages,
        Guid? activeLeafMessageId)
    {
        if (survivingMessages.Count == 0)
            return currentSelectedSpeakerCharacterId;

        if (currentSelectedSpeakerCharacterId.HasValue && survivingMessages.Any(x => x.SpeakerCharacterId == currentSelectedSpeakerCharacterId.Value))
            return currentSelectedSpeakerCharacterId.Value;

        if (activeLeafMessageId.HasValue)
        {
            var selectedPath = BuildSelectedPath(survivingMessages, activeLeafMessageId.Value);
            var selectedSpeaker = selectedPath
                .LastOrDefault(x => x.SpeakerCharacterId.HasValue)?
                .SpeakerCharacterId;
            if (selectedSpeaker.HasValue)
                return selectedSpeaker.Value;
        }

        return survivingMessages.LastOrDefault(x => x.SpeakerCharacterId.HasValue)?.SpeakerCharacterId;
    }

    private static Guid FindLatestLeaf(IReadOnlyList<DbChatMessage> messages)
    {
        var parentIds = messages
            .Where(x => x.ParentMessageId.HasValue)
            .Select(x => x.ParentMessageId!.Value)
            .ToHashSet();

        return messages
            .Where(x => !parentIds.Contains(x.Id))
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.Id)
            .First();
    }

    private static Guid FindLatestLeafInSubtree(IReadOnlyList<DbChatMessage> messages, Guid subtreeRootMessageId)
    {
        var messageMap = messages.ToDictionary(x => x.Id);
        var childrenLookup = messages
            .OrderBy(x => x.CreatedUtc)
            .ToLookup(x => x.ParentMessageId);
        if (!messageMap.ContainsKey(subtreeRootMessageId))
            return subtreeRootMessageId;

        Guid latestLeafId = subtreeRootMessageId;
        DateTime latestLeafCreatedUtc = messageMap[subtreeRootMessageId].CreatedUtc;
        var stack = new Stack<Guid>();
        stack.Push(subtreeRootMessageId);

        while (stack.Count > 0)
        {
            var currentId = stack.Pop();
            var children = childrenLookup[currentId].ToList();
            if (children.Count == 0)
            {
                var currentMessage = messageMap[currentId];
                if (currentMessage.CreatedUtc >= latestLeafCreatedUtc)
                {
                    latestLeafCreatedUtc = currentMessage.CreatedUtc;
                    latestLeafId = currentId;
                }

                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--)
                stack.Push(children[index].Id);
        }

        return latestLeafId;
    }

    private static string BuildBranchPreview(string content)
    {
        var condensed = string.Join(
            " ",
            content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (condensed.Length <= 42)
            return condensed;

        return $"{condensed[..39].TrimEnd()}...";
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildInitialStepArtifacts(
        PostStorySceneMessage request)
    {
        return
        [
            new(
                "appearance",
                "Appearance",
                [new StoryMessageProcessTextBlock("Starting Point", $"Preparing to resolve current appearance before planning a {DescribeMode(request.Mode)} message.")],
                []),
            new(
                "planning",
                "Planning",
                [],
                []),
            new(
                "writing",
                "Writing",
                [],
                [])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildPlanningCompletedStepArtifacts(
        StoryScenePostMode mode,
        string? guidancePrompt,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StoryMessagePlannerResult planner)
    {
        var proseRequest = new StoryMessageProseRequest(mode, guidancePrompt, context, planner);

        return
        [
            new(
                "appearance",
                "Appearance",
                [new StoryMessageProcessTextBlock("Appearance Context", BuildAppearanceContextSummary(context, appearance))],
                [new StoryMessageProcessTextBlock("Resolved Appearance", BuildAppearanceDetail(appearance))]),
            new(
                "planning",
                "Planning",
                BuildPromptBlocks(
                    BuildPlannerSystemPrompt(),
                    BuildPlannerUserPrompt(
                        new PostStorySceneMessage(Guid.Empty, context.Actor.CharacterId, mode, null, guidancePrompt),
                        context)),
                BuildPlanningOutputBlocks(planner)),
            new(
                "writing",
                "Writing",
                BuildWritingInputBlocks(proseRequest),
                [])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildAppearanceCompletedStepArtifacts(
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance)
    {
        return
        [
            new(
                "appearance",
                "Appearance",
                [new StoryMessageProcessTextBlock("Appearance Context", BuildAppearanceContextSummary(context, appearance))],
                [new StoryMessageProcessTextBlock("Resolved Appearance", BuildAppearanceDetail(appearance))]),
            new(
                "planning",
                "Planning",
                [],
                []),
            new(
                "writing",
                "Writing",
                [],
                [])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildCompletedStepArtifacts(
        StoryMessageProseRequest proseRequest,
        StorySceneAppearanceResolution appearance,
        string finalMessage)
    {
        return
        [
            new(
                "appearance",
                "Appearance",
                [new StoryMessageProcessTextBlock("Appearance Context", BuildAppearanceContextSummary(proseRequest.Context, appearance))],
                [new StoryMessageProcessTextBlock("Resolved Appearance", BuildAppearanceDetail(appearance))]),
            new(
                "planning",
                "Planning",
                BuildPromptBlocks(
                    BuildPlannerSystemPrompt(),
                    BuildPlannerUserPrompt(
                        new PostStorySceneMessage(Guid.Empty, proseRequest.Context.Actor.CharacterId, proseRequest.Mode, null, proseRequest.GuidancePrompt),
                        proseRequest.Context)),
                BuildPlanningOutputBlocks(proseRequest.Planner)),
            new(
                "writing",
                "Writing",
                BuildWritingInputBlocks(proseRequest),
                [new StoryMessageProcessTextBlock("Final Message", finalMessage)])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildPlanningOutputBlocks(StoryMessagePlannerResult planner) =>
        [new StoryMessageProcessTextBlock("Planning Outcome", BuildPlannerDetail(planner))];

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildWritingInputBlocks(StoryMessageProseRequest proseRequest) =>
    [
        new StoryMessageProcessTextBlock("Planning Summary", proseRequest.Planner.PlanningSummary),
        new StoryMessageProcessTextBlock("Planning Full Details", BuildPlannerDetail(proseRequest.Planner)),
        ..BuildPromptBlocks(BuildProseSystemPrompt(proseRequest.Context.Actor.IsNarrator), BuildProseUserPrompt(proseRequest))
    ];

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildPromptBlocks(string systemPrompt, string userPrompt) =>
        [
            new StoryMessageProcessTextBlock("System Prompt", systemPrompt),
            new StoryMessageProcessTextBlock("User Prompt", userPrompt)
        ];

    private static StoryMessageProcessStepArtifact? ResolveStepArtifact(StoryMessageProcessContext? context, ProcessStep step)
    {
        if (context is null)
            return null;

        var stepKey = step.SortOrder switch
        {
            0 => "appearance",
            1 => "planning",
            2 => "writing",
            _ => step.Title.Trim().ToLowerInvariant()
        };

        return context.StepArtifacts?.FirstOrDefault(x => x.StepKey == stepKey);
    }

    private static StoryMessageProcessContext? DeserializeContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoryMessageProcessContext>(json, JsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatList(IReadOnlyList<string> values) => values.Count == 0 ? "None" : string.Join("; ", values);

    private static string FallbackText(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private static string BuildAppearanceContextSummary(StorySceneGenerationContext context, StorySceneAppearanceResolution appearance)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Characters currently in the scene:");
        foreach (var character in context.Characters.Where(x => x.IsPresentInScene))
            builder.AppendLine($"- {character.Name} | General appearance: {character.GeneralAppearance} | Prior current appearance: {FallbackText(character.CurrentAppearance)}");

        builder.AppendLine("Transcript since latest appearance entry:");
        if (appearance.TranscriptSinceLatestEntry.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in appearance.TranscriptSinceLatestEntry)
                builder.AppendLine($"- {message.SpeakerName}: {message.Content}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string TruncateProcessDetail(string detail) =>
        detail.Length <= 4000 ? detail : $"{detail[..3997].TrimEnd()}...";

    private static IReadOnlyList<string> NormalizeItems(IReadOnlyList<string>? values) =>
        values?
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    private static string RequireValue(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        throw new InvalidOperationException($"Planning the scene message failed because the model returned an empty {fieldName}.");
    }

    private static string RequireItems(string? value, string fieldName) => RequireValue(value, fieldName);

    private static void ValidateSpeaker(ChatStory story, Guid? speakerCharacterId)
    {
        if (!speakerCharacterId.HasValue)
            return;

        var exists = story.Characters.Entries.Any(x => !x.IsArchived && x.Id == speakerCharacterId.Value);
        if (!exists)
            throw new InvalidOperationException("Selecting the scene speaker failed because the chosen character could not be found.");
    }

    private ChatOptions BuildPlannerOptions()
    {
        [Description("Get the actor details, scene state, snapshot summary, and transcript details for the current generation context.")]
        string GetContextDetails() => "The full context is already present in the planner prompt.";

        return new ChatOptions
        {
            Temperature = 0.4f,
            Tools = [AIFunctionFactory.Create(GetContextDetails)]
        };
    }

    private async Task<ChatStory> GetOrCreateStoryAsync(DbAppContext dbContext, Guid threadId, CancellationToken cancellationToken)
    {
        var story = await dbContext.ChatStories.FirstOrDefaultAsync(x => x.ChatThreadId == threadId, cancellationToken);
        if (story is not null)
            return story;

        story = new ChatStory
        {
            ChatThreadId = threadId,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.ChatStories.Add(story);
        await dbContext.SaveChangesAsync(cancellationToken);
        return story;
    }

    private void PublishWorkspaceRefresh(Guid threadId)
    {
        var occurredUtc = DateTime.UtcNow;
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, occurredUtc));
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarStory, "updated", null, threadId, occurredUtc));
    }

    private sealed record TranscriptArtifact(
        DateTime CreatedUtc,
        StorySceneSnapshotView? Snapshot,
        StorySceneAppearanceEntryView? Appearance);

    private sealed record GenerationBuildResult(
        StorySceneGenerationContext Context,
        StorySceneAppearanceResolution Appearance);

    private sealed class PlannerStageResponse
    {
        public string Intent { get; set; } = string.Empty;

        public string ImmediateGoal { get; set; } = string.Empty;

        public string EmotionalStance { get; set; } = string.Empty;

        public IReadOnlyList<string>? TargetAddressees { get; set; }

        public IReadOnlyList<string>? RequiredFactualBeats { get; set; }

        public IReadOnlyList<string>? Guardrails { get; set; }

        public string PlanningSummary { get; set; } = string.Empty;
    }
}
