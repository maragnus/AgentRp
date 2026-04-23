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
    IStoryGenerationSettingsService storyGenerationSettingsService,
    IAgentCatalog agentCatalog,
    IModelOperationRegistry modelOperationRegistry,
    ILogger<StorySceneChatService> logger) : IStorySceneChatService
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private const string StoppedPlaceholderMessage = "Generation stopped before any scene text was written.";
    private const string StoppedBranchPreview = "Stopped before writing";

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
        var processStatusMap = runs
            .Where(x => x.TargetMessageId.HasValue)
            .ToDictionary(x => x.TargetMessageId!.Value, x => x.Status);
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(messages, thread.ActiveLeafMessageId);
        var path = selectedLeafMessageId.HasValue
            ? BuildSelectedPath(messages, selectedLeafMessageId.Value)
            : [];
        var effectivePath = ExcludeStoppedPlaceholderMessages(path, processStatusMap);
        var effectiveLeafMessageId = effectivePath.LastOrDefault()?.Id;
        var latestSnapshot = effectiveLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetLatestSnapshotAsync(threadId, effectiveLeafMessageId.Value, cancellationToken)
            : null;
        var snapshots = effectiveLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetSnapshotsForPathAsync(threadId, effectiveLeafMessageId.Value, cancellationToken)
            : [];
        var appearanceEntries = effectivePath.Count == 0
            ? []
            : await storyChatAppearanceService.GetEntriesForPathAsync(threadId, effectivePath, story, cancellationToken);
        var appearanceEntriesById = appearanceEntries.ToDictionary(x => x.AppearanceEntryId);
        var processMap = runs
            .Where(x => x.TargetMessageId.HasValue)
            .ToDictionary(x => x.TargetMessageId!.Value, x => MapProcess(x, appearanceEntriesById));
        var snapshotCandidateMessageIds = StoryChatSnapshotService.GetSnapshotCandidateMessageIds(effectivePath, latestSnapshot).ToHashSet();
        var childrenLookup = messages
            .OrderBy(x => x.CreatedUtc)
            .ToLookup(x => x.ParentMessageId);
        var descendantCounts = BuildDescendantCounts(messages);
        var transcript = BuildTranscript(path, messages, childrenLookup, descendantCounts, selectedSpeakerId, characters, processMap, snapshotCandidateMessageIds, snapshots, appearanceEntries);

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

    public async Task PostMessageAsync(
        PostStorySceneMessage request,
        StorySceneMessageStreamHandler? streamHandler,
        CancellationToken cancellationToken)
    {
        if (request.Mode == StoryScenePostMode.Manual)
        {
            await PostManualMessageAsync(request, cancellationToken);
            return;
        }

        await PostGeneratedMessageAsync(request, streamHandler, cancellationToken);
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

    public async Task RegenerateProseAsync(
        RegenerateStorySceneProse request,
        StorySceneMessageStreamHandler? streamHandler,
        CancellationToken cancellationToken)
    {
        ConfiguredAgent agent;
        Guid messageId;
        Guid runId;
        StoryMessageProseRequest proseRequest;
        StorySceneAppearanceResolution appearance;
        string sourceSpeakerName;
        string failedPartialProseText = string.Empty;

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var thread = await dbContext.ChatThreads
                .Include(x => x.Messages)
                .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the selected chat could not be found.");
            agent = agentCatalog.GetAgentOrDefault(thread.SelectedAgentName)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because no AI provider is configured for this chat.");
            var sourceMessage = thread.Messages.FirstOrDefault(x => x.Id == request.SourceMessageId)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the source message could not be found.");
            var selectedLeafMessageId = ResolveSelectedLeafMessageId(thread.Messages, thread.ActiveLeafMessageId);

            if (!IsMessageOnSelectedPath(thread.Messages, selectedLeafMessageId, sourceMessage.Id))
                throw new InvalidOperationException("Regenerating the scene prose failed because only messages in the current chat log can be regenerated.");

            if (sourceMessage.GenerationMode == StoryScenePostMode.Manual)
                throw new InvalidOperationException("Regenerating the scene prose failed because direct messages do not have reusable AI planning.");

            if (!sourceMessage.SourceProcessRunId.HasValue)
                throw new InvalidOperationException("Regenerating the scene prose failed because the source message does not have a saved generation process.");

            var sourceRun = await dbContext.ProcessRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == sourceMessage.SourceProcessRunId.Value && x.ThreadId == thread.Id, cancellationToken)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the saved generation process could not be found.");

            if (sourceRun.Status == ProcessRunStatus.Running)
                throw new InvalidOperationException("Regenerating the scene prose failed because the source generation process is still running.");

            var sourceContext = DeserializeContext(sourceRun.ContextJson)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the saved generation process did not include reusable planning details.");
            var generationContext = sourceContext.GenerationContext
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the saved generation context could not be reused.");
            appearance = sourceContext.Appearance
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the saved appearance context could not be reused.");
            var planner = sourceContext.Planner
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the saved planner result could not be reused.");

            proseRequest = new StoryMessageProseRequest(sourceMessage.GenerationMode, sourceContext.GuidancePrompt, generationContext, planner);
            sourceSpeakerName = proseRequest.Context.Actor.Name;

            var now = DateTime.UtcNow;
            var targetMessage = new DbChatMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Thread = thread,
                Role = sourceMessage.Role,
                MessageKind = sourceMessage.MessageKind,
                Content = string.Empty,
                CreatedUtc = now,
                SpeakerCharacterId = sourceMessage.SpeakerCharacterId,
                GenerationMode = sourceMessage.GenerationMode,
                ParentMessageId = sourceMessage.ParentMessageId,
                EditedFromMessageId = sourceMessage.Id
            };

            var processRun = new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Thread = thread,
                UserMessageId = targetMessage.Id,
                AssistantMessageId = targetMessage.Id,
                TargetMessageId = targetMessage.Id,
                ActorCharacterId = sourceMessage.SpeakerCharacterId,
                Summary = $"Regenerating prose from the saved plan as {sourceSpeakerName}.",
                Stage = "Writing",
                ContextJson = SerializeContext(new StoryMessageProcessContext(
                    proseRequest.Mode,
                    proseRequest.GuidancePrompt,
                    proseRequest.Context,
                    appearance,
                    proseRequest.Planner,
                    proseRequest,
                    null,
                    BuildPlanningCompletedStepArtifacts(proseRequest.Mode, proseRequest.GuidancePrompt, proseRequest.Context, appearance, proseRequest.Planner))),
                Status = ProcessRunStatus.Running,
                StartedUtc = now,
                PlanningStartedUtc = now,
                PlanningCompletedUtc = now,
                ProseStartedUtc = now,
                Steps =
                [
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 0,
                        Title = "Appearance",
                        Summary = "Reused the source message's saved appearance context.",
                        Detail = TruncateProcessDetail(BuildAppearanceDetail(appearance)),
                        IconCssClass = "fa-regular fa-shirt",
                        Status = ProcessStepStatus.Completed,
                        StartedUtc = now,
                        CompletedUtc = now
                    },
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 1,
                        Title = "Planning",
                        Summary = $"Reused saved plan: {BuildPlannerSummary(proseRequest.Planner)}",
                        Detail = TruncateProcessDetail(BuildPlannerDetail(proseRequest.Planner)),
                        IconCssClass = "fa-regular fa-map",
                        Status = ProcessStepStatus.Completed,
                        StartedUtc = now,
                        CompletedUtc = now
                    },
                    new ProcessStep
                    {
                        Id = Guid.NewGuid(),
                        SortOrder = 2,
                        Title = "Writing",
                        Summary = "Drafting a fresh version of the scene message from the saved plan.",
                        Detail = "The prose stage is reusing the saved planner result to write a new branch.",
                        IconCssClass = "fa-regular fa-pen-line",
                        Status = ProcessStepStatus.Running,
                        StartedUtc = now
                    }
                ]
            };

            targetMessage.SourceProcessRunId = processRun.Id;
            dbContext.ChatMessages.Add(targetMessage);
            dbContext.ProcessRuns.Add(processRun);

            thread.SelectedSpeakerCharacterId = sourceMessage.SpeakerCharacterId;
            thread.ActiveLeafMessageId = targetMessage.Id;
            thread.UpdatedUtc = now;

            await dbContext.SaveChangesAsync(cancellationToken);

            messageId = targetMessage.Id;
            runId = processRun.Id;
        }

        PublishWorkspaceRefresh(request.ThreadId);

        using var operation = StartRunOperation(runId, cancellationToken);

        try
        {
            var generationSettings = await storyGenerationSettingsService.GetSettingsAsync(operation.CancellationToken);
            var proseText = await StreamProseStageAsync(
                agent,
                request.ThreadId,
                messageId,
                proseRequest,
                generationSettings,
                streamHandler,
                partialProseText => failedPartialProseText = partialProseText,
                operation.CancellationToken);
            await CompleteRunAsync(request.ThreadId, runId, messageId, proseRequest, appearance, proseText, cancellationToken);
            if (streamHandler is not null)
                await streamHandler(new StorySceneMessageStreamUpdate(request.ThreadId, messageId, proseText, true), cancellationToken);

            PublishWorkspaceRefresh(request.ThreadId);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogInformation(exception, "Regenerating story scene prose was stopped for thread {ThreadId}, source message {SourceMessageId}, and speaker {SpeakerName}.", request.ThreadId, request.SourceMessageId, sourceSpeakerName);
            await CancelRunAsync(
                request.ThreadId,
                runId,
                proseRequest,
                appearance,
                failedPartialProseText,
                CancellationToken.None);
            PublishWorkspaceRefresh(request.ThreadId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Regenerating story scene prose failed for thread {ThreadId}, source message {SourceMessageId}, and speaker {SpeakerName}.", request.ThreadId, request.SourceMessageId, sourceSpeakerName);
            await FailRunAsync(
                request.ThreadId,
                runId,
                proseRequest,
                appearance,
                failedPartialProseText,
                exception,
                cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);
            throw new InvalidOperationException($"Regenerating prose for {sourceSpeakerName} failed while writing the new branch.", exception);
        }
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
        var updatedAppearance = await storyChatAppearanceService.UpdateLatestEntryAsync(request, cancellationToken);
        await RefreshProcessAppearanceAsync(request.ThreadId, updatedAppearance, cancellationToken);
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
        var postTarget = await ResolvePostTargetAsync(
            dbContext,
            thread,
            request,
            "Posting the retried manual scene message",
            cancellationToken);

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
            ParentMessageId = postTarget.ParentMessageId,
            EditedFromMessageId = postTarget.EditedFromMessageId
        };
        dbContext.ChatMessages.Add(message);

        thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
        thread.UpdatedUtc = now;
        thread.ActiveLeafMessageId = message.Id;
        if (thread.Title == "New Chat")
            thread.Title = BuildThreadTitle(manualText);
        else if (ShouldRetitleRootRetry(thread.Title, postTarget))
            thread.Title = BuildThreadTitle(manualText);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    private async Task PostGeneratedMessageAsync(
        PostStorySceneMessage request,
        StorySceneMessageStreamHandler? streamHandler,
        CancellationToken cancellationToken)
    {
        var trimmedGuidancePrompt = request.GuidancePrompt?.Trim();

        if (request.Mode == StoryScenePostMode.GuidedAi && string.IsNullOrWhiteSpace(trimmedGuidancePrompt))
            throw new InvalidOperationException("Planning the guided scene message failed because the guidance prompt was empty.");

        StorySceneGenerationContext generationContext;
        StorySceneAppearanceResolution appearanceResolution;
        ConfiguredAgent agent;
        Guid messageId;
        Guid runId;
        Guid? contextLeafMessageId;
        StoryMessageProseRequest? proseRequest = null;
        StorySceneAppearanceResolution? failedAppearanceResolution = null;
        string failedPartialProseText = string.Empty;

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var thread = await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
                ?? throw new InvalidOperationException("Generating the scene message failed because the selected chat could not be found.");
            agent = agentCatalog.GetAgentOrDefault(thread.SelectedAgentName)
                ?? throw new InvalidOperationException("Generating the scene message failed because no AI provider is configured for this chat.");
            var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);

            ValidateSpeaker(story, request.SpeakerCharacterId);
            var postTarget = await ResolvePostTargetAsync(
                dbContext,
                thread,
                request,
                "Generating the retried scene message",
                cancellationToken);
            contextLeafMessageId = postTarget.ParentMessageId;
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
                ParentMessageId = postTarget.ParentMessageId,
                EditedFromMessageId = postTarget.EditedFromMessageId
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
                        Summary = "Determining the beat, intent, change, and guardrails for the next message.",
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
            else if (ShouldRetitleRootRetry(thread.Title, postTarget))
                thread.Title = BuildThreadTitle(BuildThreadSeedText(trimmedGuidancePrompt, actor));

            await dbContext.SaveChangesAsync(cancellationToken);

            messageId = targetMessage.Id;
            runId = processRun.Id;
        }

        PublishWorkspaceRefresh(request.ThreadId);

        using var operation = StartRunOperation(runId, cancellationToken);

        try
        {
            var generationBuild = await BuildGenerationContextAsync(request.ThreadId, request.SpeakerCharacterId, contextLeafMessageId, operation.CancellationToken);
            generationContext = generationBuild.Context;
            appearanceResolution = generationBuild.Appearance;
            await UpdateAppearanceCompletionAsync(request.ThreadId, runId, request.Mode, trimmedGuidancePrompt, generationContext, appearanceResolution, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            var generationSettings = await storyGenerationSettingsService.GetSettingsAsync(operation.CancellationToken);
            var planner = await RunPlannerStageAsync(agent, request, generationContext, generationSettings, operation.CancellationToken);
            await UpdatePlannerCompletionAsync(request.ThreadId, runId, planner, generationContext, appearanceResolution, trimmedGuidancePrompt, request.Mode, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            proseRequest = new StoryMessageProseRequest(request.Mode, trimmedGuidancePrompt, generationContext, planner);
            failedAppearanceResolution = appearanceResolution;
            var proseText = await StreamProseStageAsync(
                agent,
                request.ThreadId,
                messageId,
                proseRequest,
                generationSettings,
                streamHandler,
                partialProseText => failedPartialProseText = partialProseText,
                operation.CancellationToken);
            await CompleteRunAsync(request.ThreadId, runId, messageId, proseRequest, appearanceResolution, proseText, cancellationToken);
            if (streamHandler is not null)
                await streamHandler(new StorySceneMessageStreamUpdate(request.ThreadId, messageId, proseText, true), cancellationToken);

            PublishWorkspaceRefresh(request.ThreadId);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogInformation(exception, "Generating the story scene message was stopped for thread {ThreadId} and speaker {SpeakerCharacterId}.", request.ThreadId, request.SpeakerCharacterId);
            await CancelRunAsync(
                request.ThreadId,
                runId,
                proseRequest,
                failedAppearanceResolution,
                failedPartialProseText,
                CancellationToken.None);
            PublishWorkspaceRefresh(request.ThreadId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Generating the story scene message failed for thread {ThreadId} and speaker {SpeakerCharacterId}.", request.ThreadId, request.SpeakerCharacterId);
            await FailRunAsync(
                request.ThreadId,
                runId,
                proseRequest,
                failedAppearanceResolution,
                failedPartialProseText,
                exception,
                cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);
            throw;
        }
    }

    private async Task<StoryScenePostTarget> ResolvePostTargetAsync(
        DbAppContext dbContext,
        ChatThread thread,
        PostStorySceneMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!request.RetrySourceMessageId.HasValue)
            return new StoryScenePostTarget(thread.ActiveLeafMessageId, null, null);

        var messages = await dbContext.ChatMessages
            .Where(x => x.ThreadId == thread.Id)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
        var sourceMessage = messages.FirstOrDefault(x => x.Id == request.RetrySourceMessageId.Value)
            ?? throw new InvalidOperationException($"{operation} failed because the source message could not be found.");
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(messages, thread.ActiveLeafMessageId);

        if (!IsMessageOnSelectedPath(messages, selectedLeafMessageId, sourceMessage.Id))
            throw new InvalidOperationException($"{operation} failed because only messages in the current chat log can be retried.");

        return new StoryScenePostTarget(sourceMessage.ParentMessageId, sourceMessage.Id, sourceMessage);
    }

    private static bool ShouldRetitleRootRetry(string currentTitle, StoryScenePostTarget postTarget) =>
        postTarget.SourceMessage is not null
        && !postTarget.SourceMessage.ParentMessageId.HasValue
        && string.Equals(currentTitle, BuildThreadTitle(postTarget.SourceMessage.Content), StringComparison.Ordinal);

    private async Task<StoryMessagePlannerResult> RunPlannerStageAsync(
        ConfiguredAgent agent,
        PostStorySceneMessage request,
        StorySceneGenerationContext context,
        StoryGenerationSettingsView generationSettings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildPlannerSystemPrompt()),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildPlannerUserPrompt(request, context))
        ];

        var response = await agent.ChatClient.GetResponseAsync<PlannerStageResponse>(
            messages,
            options: BuildPlannerOptions(generationSettings),
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var planner = response.Result;

        return new StoryMessagePlannerResult(
            NormalizeTurnShape(planner.TurnShape),
            RequireValue(planner.Beat, "planner beat"),
            RequireValue(planner.Intent, "planner intent"),
            RequireValue(planner.ImmediateGoal, "planner immediate goal"),
            RequireValue(planner.WhyNow, "planner why now"),
            RequireValue(planner.ChangeIntroduced, "planner change introduced"),
            RequireItems(planner.Guardrails, "planner guardrails"));
    }

    private async Task<string> StreamProseStageAsync(
        ConfiguredAgent agent,
        Guid threadId,
        Guid messageId,
        StoryMessageProseRequest request,
        StoryGenerationSettingsView generationSettings,
        StorySceneMessageStreamHandler? streamHandler,
        Action<string> partialProseTextChanged,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildProseSystemPrompt(request)),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildProseUserPrompt(request))
        ];

        var rawProseBuilder = new StringBuilder();
        await foreach (var update in agent.ChatClient.GetStreamingResponseAsync(
            messages,
            options: new ChatOptions { Temperature = (float)generationSettings.ProseTemperature },
            cancellationToken: cancellationToken))
        {
            if (string.IsNullOrEmpty(update.Text))
                continue;

            rawProseBuilder.Append(update.Text);
            var partialProseText = NormalizeProseForDisplay(rawProseBuilder.ToString(), request.Context.Actor, isFinal: false);
            partialProseTextChanged(partialProseText);
            if (streamHandler is not null)
                await streamHandler(new StorySceneMessageStreamUpdate(threadId, messageId, partialProseText, false), cancellationToken);
        }

        var normalizedProse = NormalizeProseForDisplay(rawProseBuilder.ToString(), request.Context.Actor, isFinal: true);
        partialProseTextChanged(normalizedProse);

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
        run.Summary = BuildPlannerSummary(planner);
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
                CompleteProcessStep(step, now, BuildPlannerSummary(planner), BuildPlannerDetail(planner));
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

    private async Task CompleteRunAsync(
        Guid threadId,
        Guid runId,
        Guid messageId,
        StoryMessageProseRequest proseRequest,
        StorySceneAppearanceResolution appearance,
        string finalMessage,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var message = await dbContext.ChatMessages
            .FirstAsync(x => x.Id == messageId && x.ThreadId == threadId, cancellationToken);
        var thread = await dbContext.ChatThreads
            .FirstAsync(x => x.Id == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        message.Content = finalMessage;
        thread.UpdatedUtc = now;
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

    private async Task CancelRunAsync(
        Guid threadId,
        Guid runId,
        StoryMessageProseRequest? proseRequest,
        StorySceneAppearanceResolution? appearance,
        string partialMessage,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        if (run is null)
            return;

        var now = DateTime.UtcNow;
        var stage = run.Stage;
        run.Stage = null;
        run.Status = ProcessRunStatus.Canceled;
        run.CompletedUtc = now;
        run.Summary = $"The {stage?.ToLowerInvariant() ?? "message generation"} stage was stopped.";

        if (string.Equals(stage, "Writing", StringComparison.OrdinalIgnoreCase))
            run.ProseCompletedUtc = now;

        if (run.TargetMessageId.HasValue)
        {
            var message = await dbContext.ChatMessages
                .FirstOrDefaultAsync(x => x.Id == run.TargetMessageId.Value && x.ThreadId == threadId, cancellationToken);
            var thread = await dbContext.ChatThreads
                .FirstAsync(x => x.Id == threadId, cancellationToken);

            if (message is not null)
                message.Content = partialMessage;

            thread.UpdatedUtc = now;
        }

        if (proseRequest is not null && appearance is not null)
        {
            run.ContextJson = SerializeContext(new StoryMessageProcessContext(
                proseRequest.Mode,
                proseRequest.GuidancePrompt,
                proseRequest.Context,
                appearance,
                proseRequest.Planner,
                proseRequest,
                string.IsNullOrWhiteSpace(partialMessage) ? null : partialMessage,
                BuildCompletedStepArtifacts(
                    proseRequest,
                    appearance,
                    partialMessage,
                    string.IsNullOrWhiteSpace(partialMessage) ? "Stopped Before Writing" : "Partial Message")));
        }

        foreach (var step in run.Steps.OrderBy(x => x.SortOrder))
        {
            if (step.Status == ProcessStepStatus.Completed)
                continue;

            if (step.Status == ProcessStepStatus.Running)
            {
                CancelProcessStep(
                    step,
                    now,
                    $"{step.Title} was stopped.",
                    step.SortOrder == 2 && string.IsNullOrWhiteSpace(partialMessage)
                        ? StoppedPlaceholderMessage
                        : $"{step.Title} was stopped before completion.");
                continue;
            }

            CancelProcessStep(
                step,
                now,
                $"{step.Title} did not run.",
                $"{step.Title} was stopped before it started.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailRunAsync(
        Guid threadId,
        Guid runId,
        StoryMessageProseRequest? proseRequest,
        StorySceneAppearanceResolution? appearance,
        string partialMessage,
        Exception exception,
        CancellationToken cancellationToken)
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

        if (!string.IsNullOrWhiteSpace(partialMessage) && run.TargetMessageId.HasValue)
        {
            var message = await dbContext.ChatMessages
                .FirstOrDefaultAsync(x => x.Id == run.TargetMessageId.Value && x.ThreadId == threadId, cancellationToken);
            var thread = await dbContext.ChatThreads
                .FirstAsync(x => x.Id == threadId, cancellationToken);

            if (message is not null)
                message.Content = partialMessage;

            thread.UpdatedUtc = now;

            if (proseRequest is not null && appearance is not null)
            {
                run.ContextJson = SerializeContext(new StoryMessageProcessContext(
                    proseRequest.Mode,
                    proseRequest.GuidancePrompt,
                    proseRequest.Context,
                    appearance,
                    proseRequest.Planner,
                    proseRequest,
                    partialMessage,
                    BuildCompletedStepArtifacts(proseRequest, appearance, partialMessage, "Partial Message")));
            }
        }

        var activeStep = run.Steps
            .OrderBy(x => x.SortOrder)
            .FirstOrDefault(x => x.Status is ProcessStepStatus.Running or ProcessStepStatus.Pending);
        if (activeStep is not null)
        {
            FailProcessStep(activeStep, now, $"{activeStep.Title} failed while generating the scene message. Cause: {exception.Message}");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private RunOperationScope StartRunOperation(Guid runId, CancellationToken cancellationToken) =>
        new(modelOperationRegistry.Start(runId), cancellationToken);

    private async Task<GenerationBuildResult> BuildGenerationContextAsync(
        Guid threadId,
        Guid? speakerCharacterId,
        Guid? contextLeafMessageId,
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
        var processMap = await dbContext.ProcessRuns
            .AsNoTracking()
            .Where(x => x.ThreadId == thread.Id && x.TargetMessageId.HasValue)
            .ToDictionaryAsync(x => x.TargetMessageId!.Value, x => x.Status, cancellationToken);
        var selectedLeafMessageId = contextLeafMessageId.HasValue && allMessages.Any(x => x.Id == contextLeafMessageId.Value)
            ? contextLeafMessageId
            : null;
        var selectedPath = selectedLeafMessageId.HasValue
            ? BuildSelectedPath(allMessages, selectedLeafMessageId.Value)
            : [];
        var effectivePath = ExcludeStoppedPlaceholderMessages(selectedPath, processMap);
        var effectiveLeafMessageId = effectivePath.LastOrDefault()?.Id;
        var latestSnapshot = effectiveLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetLatestSnapshotAsync(thread.Id, effectiveLeafMessageId.Value, cancellationToken)
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
            ? effectivePath
            : effectivePath.Where(x => x.CreatedUtc > latestSnapshot.CoveredThroughUtc).ToList();
        var appearance = await storyChatAppearanceService.ResolveLatestAppearanceAsync(
            threadId,
            effectivePath,
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
            MapNarrativeSettings(story.StoryContext),
            BuildHistorySummary(story.History),
            latestSnapshot,
            transcriptSinceSnapshot.Select(message => new StorySceneTranscriptMessage(
                    message.Id,
                    message.CreatedUtc,
                    ResolveSpeakerName(message, characters),
                    message.MessageKind == ChatMessageKind.Narration,
                    message.Content))
                .ToList());

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
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "Speak in concise descriptive prose. Introduce or clarify facts without inventing contradictions.",
                BuildNarratorHiddenKnowledge(characters));
        }

        var character = characters.FirstOrDefault(x => x.Id == speakerCharacterId.Value)
            ?? throw new InvalidOperationException("Building the scene context failed because the selected speaker could not be found.");

        return new StorySceneActorContext(
            character.Id,
            character.Name,
            false,
            character.Summary,
            character.GeneralAppearance,
            character.CorePersonality,
            character.Relationships,
            character.PreferencesBeliefs,
            character.PrivateMotivations,
            string.Empty,
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
        IReadOnlyList<DbChatMessage> allMessages,
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
            var branchMessages = childrenLookup[message.ParentMessageId].ToList();
            var children = childrenLookup[message.Id].ToList();
            var branchNavigator = BuildBranchNavigator(
                message.ParentMessageId,
                message.Id,
                branchMessages,
                allMessages,
                characters,
                processMap);
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
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap)
    {
        if (branchMessages.Count <= 1)
            return null;

        var options = branchMessages
            .Select(branchMessage => new StorySceneBranchOptionView(
                branchMessage.Id,
                FindLatestLeafInSubtree(allMessages, branchMessage.Id),
                BuildBranchPreview(
                    branchMessage.Content,
                    branchMessage.SourceProcessRunId.HasValue && processMap.TryGetValue(branchMessage.Id, out var process) ? process : null),
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

    private async Task RefreshProcessAppearanceAsync(
        Guid threadId,
        StorySceneAppearanceEntryView updatedAppearance,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await dbContext.ProcessRuns
            .Where(x => x.ThreadId == threadId && x.ContextJson != null)
            .Include(x => x.Steps)
            .ToListAsync(cancellationToken);
        var hasChanges = false;

        foreach (var run in runs)
        {
            var processContext = DeserializeContext(run.ContextJson);
            if (processContext?.Appearance?.LatestEntry?.AppearanceEntryId != updatedAppearance.AppearanceEntryId)
                continue;

            var refreshedAppearance = processContext.Appearance with
            {
                LatestEntry = updatedAppearance,
                EffectiveCharacters = updatedAppearance.Characters
            };

            run.ContextJson = SerializeContext(processContext with { Appearance = refreshedAppearance });

            var appearanceStep = run.Steps.FirstOrDefault(step =>
                step.SortOrder == 0
                || string.Equals(step.Title, "Appearance", StringComparison.OrdinalIgnoreCase));
            if (appearanceStep is not null)
            {
                appearanceStep.Summary = updatedAppearance.Summary;
                appearanceStep.Detail = TruncateProcessDetail(BuildAppearanceDetail(refreshedAppearance));
            }

            hasChanges = true;
        }

        if (hasChanges)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static StorySceneMessageProcessView MapProcess(
        ProcessRun source,
        IReadOnlyDictionary<Guid, StorySceneAppearanceEntryView> appearanceEntriesById)
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

        var process = new StorySceneMessageProcessView(
            source.Id,
            source.Summary,
            source.Status,
            source.Status switch
            {
                ProcessRunStatus.Canceled => "Stopped",
                ProcessRunStatus.Completed => "Completed",
                ProcessRunStatus.Failed => "Failed",
                _ => "Running"
            },
            source.Stage,
            source.StartedUtc,
            source.CompletedUtc,
            steps,
            processContext);

        return RehydrateProcessAppearance(process, appearanceEntriesById);
    }

    private static StorySceneMessageProcessView RehydrateProcessAppearance(
        StorySceneMessageProcessView process,
        IReadOnlyDictionary<Guid, StorySceneAppearanceEntryView> appearanceEntriesById)
    {
        if (process.Context?.Appearance?.LatestEntry is not { } latestEntry
            || !appearanceEntriesById.TryGetValue(latestEntry.AppearanceEntryId, out var updatedAppearanceEntry))
            return process;

        var refreshedAppearance = process.Context.Appearance with
        {
            LatestEntry = updatedAppearanceEntry,
            EffectiveCharacters = updatedAppearanceEntry.Characters
        };
        var refreshedContext = process.Context with { Appearance = refreshedAppearance };
        var refreshedSteps = process.Steps
            .Select(step => string.Equals(step.Title, "Appearance", StringComparison.OrdinalIgnoreCase)
                ? step with
                {
                    Summary = updatedAppearanceEntry.Summary,
                    Detail = TruncateProcessDetail(BuildAppearanceDetail(refreshedAppearance))
                }
                : step)
            .ToList();

        return process with
        {
            Steps = refreshedSteps,
            Context = refreshedContext
        };
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

    private static void CancelProcessStep(ProcessStep step, DateTime completedUtc, string summary, string detail)
    {
        step.Status = ProcessStepStatus.Canceled;
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

    private static string BuildPlannerSystemPrompt() =>
    """
    You are the planning stage for a story scene message generator.
    Decide the next turn before any prose is written.
    Return only a concise structured plan.

    Stay grounded in the provided story context, scene state, character facts, and transcript.
    Plan one turn only.
    Choose one immediate beat, not a sequence.

    Build the plan using these fields:
    - Turn shape: choose exactly one of compact, brief, monologue, or silent.
    - Beat: the kind of move being made in this turn.
    - Intent: the actor's immediate intention.
    - Immediate goal: what this turn tries to achieve right now.
    - Why now: why this beat fits this exact moment in the transcript.
    - Change introduced: what becomes different after this turn.
    - Guardrails: what the prose should avoid.

    Turn shape definitions:
    - compact = one action beat, one or two phrases, optional short tag (always preferred)
    - silent = action/subtext only, no spoken lines (common)
    - brief = one action beat, one to two short lines with a tag in between (rare)
    - monologue = short monologue allowed (only when asked)

    Prioritize compact and silent almost always.
    Use brief or monologue only when the turn naturally needs recounting or explanation for an open-ended prompt such as "how was your day".

    Pick the most valuable next beat, not the safest or most literal reply.
    If a direct reaction is needed, react.
    If no direct reaction is needed, introduce a small new beat that moves the scene.

    A strong beat changes something.
    It may shift pressure, test a boundary, redirect attention, create a question, add discomfort, add intimacy, or force a reply.

    Avoid empty beats.
    Do not only restate rules, confirm the current situation, paraphrase the last line, or preserve the same tension without adding value.

    Keep the beat playable and local.
    Do not fast-forward.
    Do not resolve the whole exchange.
    Do not plan follow-up beats.
    Stop where the next person would naturally answer or act.

    Respect the supplied story context and content guidance.
    If content is forbidden, do not plan beats that introduce it.
    If content is encouraged, you may lean into it when the current scene supports it.

    Do not write the final message text.
    """;

    private static string BuildPlannerUserPrompt(PostStorySceneMessage request, StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildContextSummary(context));
        builder.AppendLine();

        if (request.Mode == StoryScenePostMode.GuidedAi)
            builder.AppendLine($"Use this guidance to compose the next message: {request.GuidancePrompt?.Trim()}");

        builder.AppendLine();
        AppendTurnScopeRules(builder, context.Actor, false);
        return builder.ToString().TrimEnd();
    }

    private static string BuildProseSystemPrompt(StoryMessageProseRequest request)
    {
        var context = request.Context;
        var speaker = context.Actor.IsNarrator ? "the narrator" : $"{context.Actor.Name}";
        var inScene = context.Characters
            .Where(x => x.IsPresentInScene)
            .Where(x => x.CharacterId != context.Actor.CharacterId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine(
            $"""
            You are {speaker} in a fictional chat between {string.Join(", ", inScene.Select(x => x.Name))} and yourself.
            
            Write {speaker}'s next message only.

            Follow the planner's beat. 
            Make one playable move, then stop.

            Priority order:
            1. Fulfill the beat
            2. Stay true to {speaker}, the current scene, and recent transcript
            3. Use as few words as possible
            4. Stop at the first natural pause

            Respect the supplied story context and content guidance.

            """);

        if (context.Actor.IsNarrator)
        {
            builder.AppendLine("You are speaking as the story narrator guiding the narrative, write a descriptive narration instead of dialogue.");
            return builder.ToString().TrimEnd();
        }

        switch (request.Planner.TurnShape)
        {
            case StoryTurnShape.Compact:
                builder.AppendLine(
                    """
                    This turn has a compact shape, fulfill the beat with one sharp move.
                    - Keep this very short.
                    - Use one brief visible action or reaction.
                    - Use one or two short spoken phrases.
                    - You may add one very short trailing tag if needed.
                    - Stop as soon as the beat lands.
                    - Do not add a second move.
                    """);
                break;                
            case StoryTurnShape.Brief:
                builder.AppendLine(
                    """
                    This turn has a brief shape, fulfill the beat with a quick move that may need a little setup or follow-through.
                    - Keep this short.
                    - Use one brief action or reaction.
                    - Use one or two short spoken lines separated by simple action.
                    - Let the beat breathe slightly, but stop once the main move is clear.
                    - Do not add a new topic or second emotional turn.
                    """);
                break;
            case StoryTurnShape.Monologue:
                builder.AppendLine(
                    """
                    This turn has a monologue shape, fulfill the beat with a longer move.
                    - A longer reply is allowed here.
                    - Up to three sentenses maximum of spoken words with simple actions in between.
                    - Still focus on one beat only.
                    - Stop at the first clear landing point.
                    - Do not ramble, recap, or drift into a second move.
                    """);
                break;
            case StoryTurnShape.Silent:
                builder.AppendLine(
                    """
                    This turn has a silent shape, fulfill the beat with a nonverbal move or subtext and no verbal component.
                    - Prefer action, expression, posture, or a small physical response.
                    - Do not use dialogue unless a word or two is necessary to land the beat.
                    - Keep it restrained and readable.
                    - Stop early once action is clear.
                    """);
                break;
        }

        builder.AppendLine(
            $"""
            Rules:
            - Write only as {speaker}
            - Stay inside the current moment
            - Do not fast-forward
            - Do not resolve the whole exchange
            - Do not add a second move after the beat lands
            - Do not restate the same beat in another form
            - Prefer implication over explanation
            - Prefer one strong signal over several similar ones
            - Do not add meta text, labels, or turn markers
            - Do not write unwrapped narration

            Format:
            - Actions and non-spoken beats in *asterisks*
            - Spoken dialogue in "double quotes"
            - You may combine action and dialogue in the same line
            """);

        return builder.ToString().TrimEnd();
    }

    private static string BuildProseUserPrompt(StoryMessageProseRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildContextSummary(request.Context));
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.GuidancePrompt))
        {
            builder.AppendLine("Guidance to follow strictly:");
            builder.AppendLine(request.GuidancePrompt.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("**Planner result:**");
        builder.AppendLine(BuildPlannerDetail(request.Planner));
        builder.AppendLine();
        builder.AppendLine("**Turn shape template:**");
        builder.AppendLine(BuildTurnShapeTemplate(request.Planner.TurnShape, request.Context.Actor.IsNarrator));

        // Turn scope reminder
        builder.AppendLine().AppendLine(
            """
            Write the turn by fulfilling only:
            1. the beat
            2. the intent
            3. the immediate goal
            4. the change introduced
            Honor why now and the guardrails.
            Do not expand beyond them unless necessary for coherence.
            Stop early to prevent ramble, recap, or repeating yourself.
            """).AppendLine();
        AppendTurnScopeRules(builder, request.Context.Actor, true);

        switch (request.Planner.TurnShape)
        {
            case StoryTurnShape.Compact:
                builder.AppendLine(
                    """
                    Write only a very short compact turn with:
                    - One brief visible action or reaction.
                    - One or two short spoken phrases.
                    - One very short trailing tag if needed.
                    """);
                break;                
            case StoryTurnShape.Brief:
                builder.AppendLine(
                    """
                    Write only a very brief turn with:
                    - One brief action or reaction.
                    - One or two short spoken lines separated by simple action.
                    """);
                break;
            case StoryTurnShape.Monologue:
                builder.AppendLine(
                    """
                    Write only a very short monologue turn with:
                    - Up to three sentences maximum of spoken words with simple actions in between.
                    - Strict focus on compactness.
                    - Stop at the first clear landing point.
                    - Do not ramble, recap, or repeat.
                    """);
                break;
            case StoryTurnShape.Silent:
                builder.AppendLine(
                    """
                    Write only a quick silent turn with:
                    - Nonverbal move or subtext and no verbal component.
                    - Prefer action, expression, posture, or a small physical response.
                    - Do not use dialogue unless a word or two is necessary to land the beat.
                    - Keep it restrained and readable.
                    """);
                break;
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendTurnScopeRules(StringBuilder builder, StorySceneActorContext actor, bool proseMode)
    {
        builder
            .AppendLine("Turn scope rules:")
            .AppendLine($"- {actor.Name} only");

        if (proseMode)
        {
            builder.AppendLine(
            $"""
            - one playable move
            - stop eagerly
            - keep speech natural and brief
            - no repeated beat
            - no meta text

            Format reminder: Always wrap actions in *asterisks* and speech in "quotes". Never output unwrapped output.
            """);
        }
        else
        {
            builder.AppendLine(
            """
            - Choose one immediate beat, not a sequence.
            - React to the last turn only if it truly requires a response.
            - Otherwise introduce a small new beat that adds value.
            - The beat should change something: pressure, focus, distance, tone, or uncertainty.
            - Avoid empty turns that only restate rules or repeat the current tension.
            - Keep it grounded and playable.
            """);
        }
    }

    private static string BuildContextSummary(StorySceneGenerationContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Actor:** {context.Actor.Name}");
        builder.AppendLine($"- Summary: {PromptInlineText(context.Actor.Summary)}");
        if (context.Actor.IsNarrator)
            builder.AppendLine($"- Narrator guidance: {PromptInlineText(context.Actor.NarratorGuidance, "None")}");
        else
        {
            builder.AppendLine($"- General appearance: {PromptInlineText(context.Actor.GeneralAppearance, "None")}");
            builder.AppendLine($"- Core personality: {PromptInlineText(context.Actor.CorePersonality, "None")}");
            builder.AppendLine($"- Relationships: {PromptInlineText(context.Actor.Relationships, "None")}");
            builder.AppendLine($"- Preferences / beliefs: {PromptInlineText(context.Actor.PreferencesBeliefs, "None")}");
            builder.AppendLine($"- Private motivations: {PromptInlineText(context.Actor.PrivateMotivations, "None")}");
        }

        if (!string.IsNullOrWhiteSpace(context.Actor.HiddenKnowledge))
            builder.AppendLine($"- Hidden knowledge: {PromptInlineText(context.Actor.HiddenKnowledge)}");

        builder.AppendLine();

        if (context.CurrentLocation is not null)
        {
            builder.AppendLine($"**Location:** {PromptInlineText(context.CurrentLocation.Name)}");
            if (!string.IsNullOrWhiteSpace(context.CurrentLocation.Summary))
                builder.AppendLine($"- Summary: {PromptInlineText(context.CurrentLocation.Summary)}");
            if (!string.IsNullOrWhiteSpace(context.CurrentLocation.Details))
                builder.AppendLine($"- Details: {PromptInlineText(context.CurrentLocation.Details)}");
            builder.AppendLine();
        }

        var nonActorCharacters = context.Actor.CharacterId.HasValue
            ? context.Characters.Where(x => x.CharacterId != context.Actor.CharacterId.Value).ToList()
            : context.Characters.ToList();
        var sceneCharacters = nonActorCharacters.Where(x => x.IsPresentInScene).ToList();
        if (sceneCharacters.Count > 0)
        {
            builder.AppendLine("**Characters in the scene:**")
                .AppendLine($"- **{context.Actor.Name}:** current actor");
            foreach (var character in sceneCharacters)
                builder.AppendLine($"- **{character.Name}:** {PromptInlineText(character.Summary)} | General appearance: {PromptInlineText(character.GeneralAppearance, "None")}");
            builder.AppendLine();
        }

        var otherCharacters = nonActorCharacters.Where(x => !x.IsPresentInScene).ToList();
        if (otherCharacters.Count > 0)
        {
            builder.AppendLine("**Other characters:**");
            foreach (var character in otherCharacters)
                builder.AppendLine($"- **{character.Name}:** {PromptInlineText(character.Summary)}");
            builder.AppendLine();
        }

        if (context.SceneObjects.Count > 0)
        {
            builder.AppendLine("**Objects in the scene:**");
            foreach (var item in context.SceneObjects)
                builder.AppendLine($"- {item.Name} | {PromptInlineText(item.Summary)} | Details: {PromptInlineText(item.Details, "None")}");
            builder.AppendLine();
        }

        AppendStoryContext(builder, context.StoryContext);
        AppendContentGuidance(builder, context.StoryContext);

        if (!string.IsNullOrEmpty(context.HistorySummary))
            builder.AppendLine($"**History summary:** {PromptInlineText(context.HistorySummary)}");

        if (!string.IsNullOrEmpty(context.LatestSnapshot?.Summary))
            builder.AppendLine($"**Snapshot:** {PromptInlineText(context.LatestSnapshot.Summary)}");

        builder.AppendLine("**Transcript:**");
        if (context.TranscriptSinceSnapshot.Count > 0)
        {
            foreach (var message in context.TranscriptSinceSnapshot)
                builder.AppendLine($"- {message.SpeakerName}: {PromptInlineText(message.Content, "None")}");
        }
        else
        {
            builder.AppendLine("- None");
        }
        builder.AppendLine();

        var currentAppearanceCharacters = context.Characters
            .Where(x => x.IsPresentInScene && !string.IsNullOrWhiteSpace(x.CurrentAppearance))
            .ToList();
        if (currentAppearanceCharacters.Count > 0)
        {
            builder.AppendLine("**Character appearances:**");
            foreach (var character in currentAppearanceCharacters)
                builder.AppendLine($"- {character.Name}: {PromptInlineText(character.CurrentAppearance, "None")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPlannerDetail(StoryMessagePlannerResult planner)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"**Turn shape:** {FormatTurnShape(planner.TurnShape)}");
        builder.AppendLine($"**Beat:** {planner.Beat}");
        builder.AppendLine($"**Intent:** {planner.Intent}");
        builder.AppendLine($"**Immediate goal:** {planner.ImmediateGoal}");
        builder.AppendLine($"**Why now:** {planner.WhyNow}");
        builder.AppendLine($"**Change introduced:** {planner.ChangeIntroduced}");
        builder.AppendLine($"**Guardrails:** {FormatList(planner.Guardrails)}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildProseDetail(StoryMessageProseRequest request, string finalMessage)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.GuidancePrompt))
            builder.AppendLine($"**Guidance prompt:** {request.GuidancePrompt}");

        builder.AppendLine($"**Actor:** {request.Context.Actor.Name}");
        builder.AppendLine($"**Plan:** {BuildPlannerSummary(request.Planner)}");
        builder.AppendLine("**Final message:**");
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
            builder.AppendLine($"**Latest appearance block:** {appearance.LatestEntry?.Summary ?? "None"}");
            builder.AppendLine("**Current appearances:**");
            foreach (var character in appearance.EffectiveCharacters)
                builder.AppendLine($"- {character.CharacterName}: {PromptInlineText(character.CurrentAppearance, "None captured yet")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeProseForDisplay(string text, StorySceneActorContext actor, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalizedText = isFinal ? text.Trim() : text.TrimStart();
        if (actor.IsNarrator)
            return normalizedText;

        if (!normalizedText.StartsWith(actor.Name, StringComparison.Ordinal))
            return normalizedText;

        var labelEndIndex = actor.Name.Length;
        if (normalizedText.Length == labelEndIndex)
            return isFinal ? normalizedText : string.Empty;

        if (normalizedText[labelEndIndex] != ':')
            return normalizedText;

        return normalizedText[(labelEndIndex + 1)..].TrimStart();
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

    private static string BuildPlannerSummary(StoryMessagePlannerResult planner) =>
        $"{FormatTurnShape(planner.TurnShape)} turn, {planner.Beat}: {planner.ImmediateGoal}";

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

    private static bool IsMessageOnSelectedPath(
        IReadOnlyList<DbChatMessage> messages,
        Guid? selectedLeafMessageId,
        Guid messageId)
    {
        if (!selectedLeafMessageId.HasValue)
            return false;

        return BuildSelectedPath(messages, selectedLeafMessageId.Value).Any(x => x.Id == messageId);
    }

    private static IReadOnlyList<DbChatMessage> ExcludeStoppedPlaceholderMessages(
        IReadOnlyList<DbChatMessage> path,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap) =>
        path.Where(message => !IsStoppedPlaceholderMessage(message, processMap)).ToList();

    private static IReadOnlyList<DbChatMessage> ExcludeStoppedPlaceholderMessages(
        IReadOnlyList<DbChatMessage> path,
        IReadOnlyDictionary<Guid, ProcessRunStatus> processMap) =>
        path.Where(message => !IsStoppedPlaceholderMessage(message, processMap)).ToList();

    private static bool IsStoppedPlaceholderMessage(
        DbChatMessage message,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap) =>
        string.IsNullOrWhiteSpace(message.Content)
        && message.SourceProcessRunId.HasValue
        && processMap.TryGetValue(message.Id, out var process)
        && process.Status == ProcessRunStatus.Canceled;

    private static bool IsStoppedPlaceholderMessage(
        DbChatMessage message,
        IReadOnlyDictionary<Guid, ProcessRunStatus> processMap) =>
        string.IsNullOrWhiteSpace(message.Content)
        && message.SourceProcessRunId.HasValue
        && processMap.TryGetValue(message.Id, out var status)
        && status == ProcessRunStatus.Canceled;

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

    private static string BuildBranchPreview(string content, StorySceneMessageProcessView? process)
    {
        var condensed = string.Join(
            " ",
            content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(condensed))
        {
            return process?.Status switch
            {
                ProcessRunStatus.Canceled => StoppedBranchPreview,
                ProcessRunStatus.Running => "Generating...",
                _ => "Empty message"
            };
        }

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
                BuildAppearanceInputBlocks(context, appearance),
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
                BuildAppearanceInputBlocks(context, appearance),
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
        string finalMessage,
        string messageTitle = "Final Message")
    {
        return
        [
            new(
                "appearance",
                "Appearance",
                BuildAppearanceInputBlocks(proseRequest.Context, appearance),
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
                [new StoryMessageProcessTextBlock(messageTitle, finalMessage)])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildPlanningOutputBlocks(StoryMessagePlannerResult planner) =>
        [new StoryMessageProcessTextBlock("Planning Outcome", BuildPlannerDetail(planner))];

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildAppearanceInputBlocks(
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance)
    {
        var storyContext = context.StoryContext ?? CreateDefaultStoryContext();
        var promptCharacters = context.Characters
            .Where(x => x.IsPresentInScene)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoryChatAppearancePromptCharacter(x.Name, x.CurrentAppearance))
            .ToList();

        return
        [
            ..BuildPromptBlocks(
                StoryChatAppearancePromptBuilder.BuildSystemPrompt(),
                StoryChatAppearancePromptBuilder.BuildUserPrompt(
                    promptCharacters,
                    appearance.TranscriptSinceLatestEntry,
                    storyContext.ExplicitContent,
                    storyContext.ViolentContent)),
            new StoryMessageProcessTextBlock("Appearance Context", BuildAppearanceContextSummary(context, appearance))
        ];
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildWritingInputBlocks(StoryMessageProseRequest proseRequest) =>
    [
        new StoryMessageProcessTextBlock("Planning Summary", BuildPlannerSummary(proseRequest.Planner)),
        new StoryMessageProcessTextBlock("Planning Full Details", BuildPlannerDetail(proseRequest.Planner)),
        ..BuildPromptBlocks(BuildProseSystemPrompt(proseRequest), BuildProseUserPrompt(proseRequest))
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

    private static string PromptInlineText(string? value, string fallback = "Unknown") =>
        string.IsNullOrWhiteSpace(value) ? fallback : CollapseWhitespace(value);

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string BuildAppearanceContextSummary(StorySceneGenerationContext context, StorySceneAppearanceResolution appearance)
    {
        var builder = new StringBuilder();
        AppendContentGuidance(builder, context.StoryContext);
        builder.AppendLine("Characters currently in the scene:");
        foreach (var character in context.Characters.Where(x => x.IsPresentInScene))
            builder.AppendLine($"- {character.Name} | General appearance: {PromptInlineText(character.GeneralAppearance, "None")} | Prior current appearance: {PromptInlineText(character.CurrentAppearance, "None")}");

        builder.AppendLine("Transcript:");
        if (appearance.TranscriptSinceLatestEntry.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var message in appearance.TranscriptSinceLatestEntry)
                builder.AppendLine($"- {message.SpeakerName}: {PromptInlineText(message.Content, "None")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static StoryNarrativeSettingsView MapNarrativeSettings(ChatStoryContextDocument document) => new(
        document.Genre,
        document.Setting,
        document.Tone,
        document.StoryDirection,
        document.ExplicitContent,
        document.ViolentContent);

    private static void AppendStoryContext(StringBuilder builder, StoryNarrativeSettingsView? storyContext)
    {
        var context = storyContext ?? CreateDefaultStoryContext();
        var hasGenre = !string.IsNullOrWhiteSpace(context.Genre);
        var hasSetting = !string.IsNullOrWhiteSpace(context.Setting);
        var hasTone = !string.IsNullOrWhiteSpace(context.Tone);
        var hasDirection = !string.IsNullOrWhiteSpace(context.StoryDirection);

        if (!hasGenre && !hasSetting && !hasTone && !hasDirection)
            return;

        builder.AppendLine("**Story context:**");
        if (hasGenre)
            builder.AppendLine($"- Genre: {PromptInlineText(context.Genre)}");
        if (hasSetting)
            builder.AppendLine($"- Setting: {PromptInlineText(context.Setting)}");
        if (hasTone)
            builder.AppendLine($"- Tone: {PromptInlineText(context.Tone)}");
        if (hasDirection)
            builder.AppendLine($"- Story premise / direction: {PromptInlineText(context.StoryDirection)}");
        builder.AppendLine();
    }

    private static void AppendContentGuidance(StringBuilder builder, StoryNarrativeSettingsView? storyContext)
    {
        var context = storyContext ?? CreateDefaultStoryContext();
        builder.AppendLine("**Content guidance:**");
        builder.AppendLine($"- Explicit content: {FormatContentIntensity(context.ExplicitContent)}");
        builder.AppendLine($"- Violent content: {FormatContentIntensity(context.ViolentContent)}");
        builder.AppendLine();
    }

    private static string FormatContentIntensity(StoryContentIntensity intensity) => intensity switch
    {
        StoryContentIntensity.Forbidden => "Forbidden. Do not introduce or describe this content.",
        StoryContentIntensity.Encouraged => "Encouraged when supported and scene-relevant. Lean into it without inventing it.",
        _ => "Allowed when naturally supported by the scene."
    };

    private static StoryNarrativeSettingsView CreateDefaultStoryContext() => new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        StoryContentIntensity.Allowed,
        StoryContentIntensity.Allowed);

    private static string TruncateProcessDetail(string detail) =>
        detail.Length <= 4000 ? detail : $"{detail[..3997].TrimEnd()}...";

    private static IReadOnlyList<string> RequireItems(IReadOnlyList<string>? values, string fieldName)
    {
        var items = NormalizeItems(values);
        if (items.Count > 0)
            return items;

        throw new InvalidOperationException($"Planning the scene message failed because the model returned empty {fieldName}.");
    }

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
    private static void ValidateSpeaker(ChatStory story, Guid? speakerCharacterId)
    {
        if (!speakerCharacterId.HasValue)
            return;

        var exists = story.Characters.Entries.Any(x => !x.IsArchived && x.Id == speakerCharacterId.Value);
        if (!exists)
            throw new InvalidOperationException("Selecting the scene speaker failed because the chosen character could not be found.");
    }

    private ChatOptions BuildPlannerOptions(StoryGenerationSettingsView generationSettings)
    {
        [Description("Get the actor details, scene state, snapshot summary, and transcript details for the current generation context.")]
        string GetContextDetails() => "The full context is already present in the planner prompt.";

        return new ChatOptions
        {
            Temperature = (float)generationSettings.PlannerTemperature,
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

    private sealed class RunOperationScope(IModelOperationHandle operationHandle, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            operationHandle.CancellationToken,
            cancellationToken);

        public CancellationToken CancellationToken => _linkedCancellationTokenSource.Token;

        public void Dispose()
        {
            _linkedCancellationTokenSource.Dispose();
            operationHandle.Dispose();
        }
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

    private sealed record StoryScenePostTarget(
        Guid? ParentMessageId,
        Guid? EditedFromMessageId,
        DbChatMessage? SourceMessage);

    private sealed record GenerationBuildResult(
        StorySceneGenerationContext Context,
        StorySceneAppearanceResolution Appearance);

    private sealed class PlannerStageResponse
    {
        public string TurnShape { get; set; } = string.Empty;

        public string Beat { get; set; } = string.Empty;

        public string Intent { get; set; } = string.Empty;

        public string ImmediateGoal { get; set; } = string.Empty;

        public string WhyNow { get; set; } = string.Empty;

        public string ChangeIntroduced { get; set; } = string.Empty;

        public IReadOnlyList<string>? Guardrails { get; set; }
    }

    private static StoryTurnShape NormalizeTurnShape(string? value)
    {
        var normalized = value?.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized?.ToLowerInvariant() switch
        {
            "compact" => StoryTurnShape.Compact,
            "brief" => StoryTurnShape.Brief,
            "monologue" => StoryTurnShape.Monologue,
            "silent" => StoryTurnShape.Silent,
            _ => throw new InvalidOperationException("Planning the scene message failed because the planner returned an invalid turn shape.")
        };
    }

    private static string FormatTurnShape(StoryTurnShape turnShape) => turnShape switch
    {
        StoryTurnShape.Compact => "compact",
        StoryTurnShape.Brief => "brief",
        StoryTurnShape.Monologue => "monologue",
        StoryTurnShape.Silent => "silent",
        _ => turnShape.ToString().ToLowerInvariant()
    };

    private static string BuildTurnShapeTemplate(StoryTurnShape turnShape, bool isNarrator) => turnShape switch
    {
        StoryTurnShape.Compact => isNarrator
            ? "- Use one action beat and one short narration line.\n- An optional short tag is allowed if it sharpens the beat.\n- Stop as soon as the move lands."
            : "- Use one action beat and at most one spoken line.\n- An optional short trailing tag is allowed.\n- Stop as soon as the move lands.",
        StoryTurnShape.Brief => isNarrator
            ? "- Use one to two short narration lines.\n- Keep the turn focused on a single beat.\n- Do not drift into explanation."
            : "- Use one to two short lines total.\n- Keep any action beat brief and supportive.\n- Do not drift into explanation.",
        StoryTurnShape.Monologue => isNarrator
            ? "- A short monologue is allowed.\n- Use it only to recount or explain something open-ended.\n- Keep it contained to one concise turn."
            : "- A short monologue is allowed.\n- Use it only to recount or explain something open-ended.\n- Keep it contained to one concise turn.",
        StoryTurnShape.Silent => isNarrator
            ? "- Use action, gesture, atmosphere, or subtext only.\n- Do not add spoken dialogue.\n- Add words only if silence would make the beat unclear."
            : "- Use action, gesture, or subtext only.\n- Do not add a spoken line unless silence would make the beat unclear.\n- Let the silence itself carry pressure.",
        _ => throw new InvalidOperationException("Building the prose prompt failed because the turn shape was invalid.")
    };
}
