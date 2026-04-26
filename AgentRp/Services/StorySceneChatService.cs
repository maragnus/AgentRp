using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    IStoryScenePromptLibraryService promptLibraryService,
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
        var primaryImages = await LoadPrimaryImagesAsync(dbContext, story, cancellationToken);

        var selectedSpeakerId = ResolveSelectedSpeakerId(thread.SelectedSpeakerCharacterId, characters);
        var speakers = BuildSpeakers(story, characters, selectedSpeakerId, primaryImages);
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
        var transcript = BuildTranscript(path, messages, childrenLookup, descendantCounts, selectedSpeakerId, characters, primaryImages, processMap, snapshotCandidateMessageIds, snapshots, appearanceEntries);

        var currentLocationName = story.Scene.CurrentLocationId.HasValue
            ? story.Locations.Entries.FirstOrDefault(x => x.Id == story.Scene.CurrentLocationId.Value)?.Name
            : null;
        var selectedModelId = agentCatalog.NormalizeSelectedModelId(thread.SelectedAiModelId);
        var selectedAgentName = agentCatalog.GetEnabledAgents().FirstOrDefault(x => x.ModelId == selectedModelId)?.Name;

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
        thread.UpdatedUtc = DateTime.UtcNow;

        if (!message.ParentMessageId.HasValue
            && string.Equals(thread.Title, BuildThreadTitle(previousContent), StringComparison.Ordinal))
            thread.Title = BuildThreadTitle(trimmedContent);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    public async Task UpdatePrivateIntentAsync(UpdateStoryScenePrivateIntent request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Updating private intent failed because the selected chat could not be found.");
        var message = thread.Messages.FirstOrDefault(x => x.Id == request.MessageId)
            ?? throw new InvalidOperationException("Updating private intent failed because the selected message could not be found.");
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(thread.Messages, thread.ActiveLeafMessageId);

        if (!IsMessageOnSelectedPath(thread.Messages, selectedLeafMessageId, message.Id))
            throw new InvalidOperationException("Updating private intent failed because only messages in the current chat log can be edited.");

        var privateIntent = NormalizeOptionalText(request.PrivateIntent);
        message.PrivateIntent = privateIntent;
        thread.UpdatedUtc = DateTime.UtcNow;

        if (message.SourceProcessRunId.HasValue)
        {
            var run = await dbContext.ProcessRuns
                .Include(x => x.Steps)
                .FirstOrDefaultAsync(x => x.Id == message.SourceProcessRunId.Value && x.ThreadId == thread.Id, cancellationToken);
            if (run is not null && run.Status != ProcessRunStatus.Running)
            {
                var processContext = DeserializeContext(run.ContextJson);
                if (processContext is not null)
                {
                    var updatedPlanner = processContext.Planner is null
                        ? null
                        : processContext.Planner with { PrivateIntent = privateIntent ?? string.Empty };
                    var updatedProseRequest = processContext.ProseRequest is null
                        ? null
                        : processContext.ProseRequest with
                        {
                            Planner = updatedPlanner ?? processContext.ProseRequest.Planner with { PrivateIntent = privateIntent ?? string.Empty }
                        };
                    var updatedContext = processContext with
                    {
                        Planner = updatedPlanner,
                        ProseRequest = updatedProseRequest,
                        StepArtifacts = UpdatePlanningArtifactsPrivateIntent(processContext.StepArtifacts, updatedPlanner)
                    };

                    run.ContextJson = SerializeContext(updatedContext);
                    if (updatedPlanner is not null)
                    {
                        run.Summary = BuildPlannerSummary(updatedPlanner);
                        UpdateStepSummaryIfPresent(run, ProcessStepKeys.Planning, BuildPlannerSummary(updatedPlanner), StorySceneSharedPromptBuilder.BuildPlannerDetail(updatedPlanner));
                    }
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
    }

    public async Task<ChangeStorySceneMessageSpeakerResult> ChangeMessageSpeakerAsync(ChangeStorySceneMessageSpeaker request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Changing the message speaker failed because the selected chat could not be found.");
        var story = await GetOrCreateStoryAsync(dbContext, request.ThreadId, cancellationToken);

        ValidateSpeaker(story, request.SpeakerCharacterId);

        var sourceMessage = thread.Messages.FirstOrDefault(x => x.Id == request.MessageId)
            ?? throw new InvalidOperationException("Changing the message speaker failed because the selected message could not be found.");
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(thread.Messages, thread.ActiveLeafMessageId);

        if (!IsMessageOnSelectedPath(thread.Messages, selectedLeafMessageId, sourceMessage.Id))
            throw new InvalidOperationException("Changing the message speaker failed because only messages in the current chat log can be reassigned.");

        if (HasMessageSpeaker(sourceMessage, request.SpeakerCharacterId))
            return new ChangeStorySceneMessageSpeakerResult(sourceMessage.Id, false);

        ProcessRun? sourceRun = null;
        if (sourceMessage.SourceProcessRunId.HasValue)
        {
            sourceRun = await dbContext.ProcessRuns
                .Include(x => x.Steps)
                .FirstOrDefaultAsync(x => x.Id == sourceMessage.SourceProcessRunId.Value && x.ThreadId == thread.Id, cancellationToken)
                ?? throw new InvalidOperationException("Changing the message speaker failed because the saved generation process could not be found.");

            if (sourceRun.Status == ProcessRunStatus.Running)
                throw new InvalidOperationException("Changing the message speaker failed because the selected message is still being generated.");
        }

        var characters = story.Characters.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var updatedActor = BuildActorContext(request.SpeakerCharacterId, characters);
        var canSaveInPlace = await CanSaveMessageInPlaceAsync(dbContext, thread, sourceMessage.Id, cancellationToken);
        var now = DateTime.UtcNow;

        if (canSaveInPlace)
        {
            AssignMessageSpeaker(sourceMessage, request.SpeakerCharacterId);
            sourceMessage.PrivateIntent = null;
            if (sourceRun is not null)
                RewriteProcessRunForSpeakerChange(sourceRun, sourceMessage.Id, request.SpeakerCharacterId, updatedActor);

            thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
            thread.UpdatedUtc = now;

            await dbContext.SaveChangesAsync(cancellationToken);
            PublishWorkspaceRefresh(thread.Id);
            return new ChangeStorySceneMessageSpeakerResult(sourceMessage.Id, false);
        }

        var replacementMessage = new DbChatMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            Role = sourceMessage.Role,
            MessageKind = ResolveMessageKind(request.SpeakerCharacterId),
            Content = sourceMessage.Content,
            PrivateIntent = null,
            CreatedUtc = now,
            SpeakerCharacterId = request.SpeakerCharacterId,
            GenerationMode = sourceMessage.GenerationMode,
            SourceProcessRunId = null,
            ParentMessageId = sourceMessage.ParentMessageId,
            EditedFromMessageId = sourceMessage.Id
        };

        if (sourceRun is not null)
        {
            var clonedRun = CloneProcessRunForSpeakerChange(sourceRun, thread, replacementMessage.Id, request.SpeakerCharacterId, updatedActor);
            replacementMessage.SourceProcessRunId = clonedRun.Id;
            dbContext.ProcessRuns.Add(clonedRun);
        }

        dbContext.ChatMessages.Add(replacementMessage);
        thread.ActiveLeafMessageId = replacementMessage.Id;
        thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
        thread.UpdatedUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(thread.Id);
        return new ChangeStorySceneMessageSpeakerResult(replacementMessage.Id, true);
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
            PrivateIntent = sourceMessage.PrivateIntent,
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
        StoryMessageTokenUsage? proseTokenUsage = null;
        string sourceSpeakerName;
        string failedPartialProseText = string.Empty;

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var thread = await dbContext.ChatThreads
                .Include(x => x.Messages)
                .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
                ?? throw new InvalidOperationException("Regenerating the scene prose failed because the selected chat could not be found.");
            agent = agentCatalog.GetAgentOrDefault(thread.SelectedAiModelId)
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

            proseRequest = new StoryMessageProseRequest(sourceMessage.GenerationMode, sourceContext.GuidancePrompt, sourceContext.RequestedTurnShape, generationContext, planner);
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
                AiModelId = agent.ModelId,
                AiProviderId = agent.ProviderId,
                AiProviderKind = agent.ProviderKind,
                Summary = $"Regenerating prose from the saved plan as {sourceSpeakerName}.",
                Stage = "Writing",
                ContextJson = SerializeContext(new StoryMessageProcessContext(
                    proseRequest.Mode,
                    proseRequest.GuidancePrompt,
                    proseRequest.RequestedTurnShape,
                    proseRequest.Context,
                    appearance,
                    null,
                    proseRequest.Planner,
                    proseRequest,
                    null,
                    BuildPlanningCompletedStepArtifacts(proseRequest.Mode, proseRequest.GuidancePrompt, proseRequest.RequestedTurnShape, proseRequest.Context, appearance, null, proseRequest.Planner))),
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
                        Detail = TruncateProcessDetail(StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance)),
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
                        Detail = TruncateProcessDetail(StorySceneSharedPromptBuilder.BuildPlannerDetail(proseRequest.Planner)),
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
            var generationSettings = await storyGenerationSettingsService.GetSettingsAsync(agent.ModelId, operation.CancellationToken);
            var proseText = await StreamProseStageAsync(
                agent,
                request.ThreadId,
                messageId,
                proseRequest,
                generationSettings,
                streamHandler,
                partialProseText => failedPartialProseText = partialProseText,
                operation.CancellationToken);
            proseTokenUsage = proseText.TokenUsage;
            await CompleteRunAsync(request.ThreadId, runId, messageId, proseRequest, appearance, null, proseText.FinalMessage, proseTokenUsage, cancellationToken);
            if (streamHandler is not null)
                await streamHandler(new StorySceneMessageStreamUpdate(request.ThreadId, messageId, proseText.FinalMessage, true), cancellationToken);

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
                null,
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
                null,
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
        thread.ActiveLeafMessageId = ResolveActiveLeafAfterDeletion(thread.ActiveLeafMessageId, deletedIdSet, messages, survivingMessages);
        thread.SelectedSpeakerCharacterId = ResolveSelectedSpeakerAfterDeletion(thread.SelectedSpeakerCharacterId, survivingMessages, thread.ActiveLeafMessageId);
        thread.UpdatedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishWorkspaceRefresh(request.ThreadId);
    }

    public async Task UpdateAppearanceEntryAsync(UpdateStorySceneAppearanceEntry request, CancellationToken cancellationToken)
    {
        var updatedAppearance = await storyChatAppearanceService.UpdateEntryAsync(request, cancellationToken);
        await RefreshProcessAppearanceAsync(request.ThreadId, updatedAppearance, cancellationToken);
        PublishWorkspaceRefresh(request.ThreadId);
    }

    public async Task<StorySceneAppearanceEntryView> SaveAppearanceReplayStepAsync(SaveStoryChatAppearanceReplayStep request, CancellationToken cancellationToken)
    {
        var updatedAppearance = await storyChatAppearanceService.UpsertEntryForMessageAsync(request, cancellationToken);
        await RefreshProcessAppearanceAsync(request.ThreadId, updatedAppearance, cancellationToken);
        PublishWorkspaceRefresh(request.ThreadId);
        return updatedAppearance;
    }

    public async Task UpdatePlanAsync(UpdateStoryScenePlan request, CancellationToken cancellationToken)
    {
        var updatedPlanner = NormalizeEditablePlanner(request.Planner);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == request.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("Updating the saved plan failed because the selected chat could not be found.");
        var sourceMessage = thread.Messages.FirstOrDefault(x => x.Id == request.SourceMessageId)
            ?? throw new InvalidOperationException("Updating the saved plan failed because the selected message could not be found.");
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(thread.Messages, thread.ActiveLeafMessageId);

        if (!IsMessageOnSelectedPath(thread.Messages, selectedLeafMessageId, sourceMessage.Id))
            throw new InvalidOperationException("Updating the saved plan failed because only messages in the current chat log can be edited.");

        if (sourceMessage.GenerationMode == StoryScenePostMode.Manual)
            throw new InvalidOperationException("Updating the saved plan failed because direct messages do not have reusable AI planning.");

        if (!sourceMessage.SourceProcessRunId.HasValue)
            throw new InvalidOperationException("Updating the saved plan failed because the selected message does not have a saved generation process.");

        var sourceRun = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == sourceMessage.SourceProcessRunId.Value && x.ThreadId == thread.Id, cancellationToken)
            ?? throw new InvalidOperationException("Updating the saved plan failed because the saved generation process could not be found.");

        if (sourceRun.Status == ProcessRunStatus.Running)
            throw new InvalidOperationException("Updating the saved plan failed because the source generation process is still running.");

        var sourceContext = DeserializeContext(sourceRun.ContextJson)
            ?? throw new InvalidOperationException("Updating the saved plan failed because the saved generation process did not include reusable planning details.");
        var generationContext = sourceContext.GenerationContext
            ?? throw new InvalidOperationException("Updating the saved plan failed because the saved generation context could not be reused.");
        var appearance = sourceContext.Appearance
            ?? throw new InvalidOperationException("Updating the saved plan failed because the saved appearance context could not be reused.");
        _ = sourceContext.Planner
            ?? throw new InvalidOperationException("Updating the saved plan failed because the saved planner result could not be reused.");

        var updatedProseRequest = sourceContext.ProseRequest is null
            ? null
            : sourceContext.ProseRequest with { Planner = updatedPlanner };
        var updatedStepArtifacts = BuildStepArtifactsForSavedPlannerEdit(
            sourceContext.Mode,
            sourceContext.GuidancePrompt,
            sourceContext.RequestedTurnShape,
            generationContext,
            appearance,
            sourceContext.ResponderSelection,
            updatedPlanner,
            updatedProseRequest,
            sourceContext.FinalMessage);
        var updatedContext = sourceContext with
        {
            Planner = updatedPlanner,
            ProseRequest = updatedProseRequest,
            StepArtifacts = updatedStepArtifacts
        };
        var now = DateTime.UtcNow;

        sourceRun.Summary = BuildPlannerSummary(updatedPlanner);
        sourceRun.ContextJson = SerializeContext(updatedContext);
        thread.UpdatedUtc = now;

        UpdateStepSummaryIfPresent(sourceRun, ProcessStepKeys.Planning, BuildPlannerSummary(updatedPlanner), StorySceneSharedPromptBuilder.BuildPlannerDetail(updatedPlanner));

        await dbContext.SaveChangesAsync(cancellationToken);
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

        if (RequiresGuidance(request.Mode) && request.Mode != StoryScenePostMode.Manual && string.IsNullOrWhiteSpace(trimmedGuidancePrompt))
            throw new InvalidOperationException("Planning the guided scene message failed because the guidance prompt was empty.");

        StorySceneGenerationContext generationContext;
        StorySceneResponderSelectionResult? responderSelection = null;
        StorySceneAppearanceResolution appearanceResolution;
        StoryMessageTokenUsage? appearanceTokenUsage;
        StorySceneSharedContext sharedContext;
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
            agent = agentCatalog.GetAgentOrDefault(thread.SelectedAiModelId)
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
                MessageKind = IsRespondMode(request.Mode) ? ChatMessageKind.System : ResolveMessageKind(request.SpeakerCharacterId),
                Content = string.Empty,
                CreatedUtc = now,
                SpeakerCharacterId = IsRespondMode(request.Mode) ? null : request.SpeakerCharacterId,
                GenerationMode = request.Mode,
                ParentMessageId = postTarget.ParentMessageId,
                EditedFromMessageId = postTarget.EditedFromMessageId
            };

            var processContext = new StoryMessageProcessContext(
                request.Mode,
                trimmedGuidancePrompt,
                request.RequestedTurnShape,
                null,
                null,
                null,
                null,
                null,
                null,
                BuildInitialStepArtifacts(request.Mode));
            var processRun = new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Thread = thread,
                UserMessageId = targetMessage.Id,
                AssistantMessageId = targetMessage.Id,
                TargetMessageId = targetMessage.Id,
                ActorCharacterId = IsRespondMode(request.Mode) ? null : request.SpeakerCharacterId,
                AiModelId = agent.ModelId,
                AiProviderId = agent.ProviderId,
                AiProviderKind = agent.ProviderKind,
                Summary = BuildInitialRunSummary(request.Mode, actor),
                Stage = "Appearance",
                ContextJson = SerializeContext(processContext),
                Status = ProcessRunStatus.Running,
                StartedUtc = now,
                Steps = BuildInitialProcessSteps(request.Mode, now)
            };

            targetMessage.SourceProcessRunId = processRun.Id;
            dbContext.ChatMessages.Add(targetMessage);
            dbContext.ProcessRuns.Add(processRun);

            thread.SelectedSpeakerCharacterId = request.SpeakerCharacterId;
            thread.UpdatedUtc = now;
            thread.ActiveLeafMessageId = targetMessage.Id;
            if (thread.Title == "New Chat")
                thread.Title = BuildThreadTitle(BuildThreadSeedText(request.Mode, trimmedGuidancePrompt, actor));
            else if (ShouldRetitleRootRetry(thread.Title, postTarget))
                thread.Title = BuildThreadTitle(BuildThreadSeedText(request.Mode, trimmedGuidancePrompt, actor));

            await dbContext.SaveChangesAsync(cancellationToken);

            messageId = targetMessage.Id;
            runId = processRun.Id;
        }

        PublishWorkspaceRefresh(request.ThreadId);

        using var operation = StartRunOperation(runId, cancellationToken);

        try
        {
            var generationSettings = await storyGenerationSettingsService.GetSettingsAsync(agent.ModelId, operation.CancellationToken);
            var generationBuild = await BuildSharedGenerationContextAsync(request.ThreadId, contextLeafMessageId, operation.CancellationToken);
            sharedContext = generationBuild.Context;
            appearanceResolution = generationBuild.Appearance;
            appearanceTokenUsage = generationBuild.AppearanceTokenUsage;
            await UpdateAppearanceCompletionAsync(request.ThreadId, runId, request.Mode, trimmedGuidancePrompt, request.RequestedTurnShape, sharedContext, appearanceResolution, appearanceTokenUsage, generationBuild.AppearanceStageSkipped, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            if (IsRespondMode(request.Mode))
            {
                responderSelection = await RunResponderSelectionStageAsync(
                    agent,
                    request.ThreadId,
                    request.Mode,
                    trimmedGuidancePrompt,
                    request.SpeakerCharacterId,
                    sharedContext,
                    appearanceResolution,
                    generationSettings,
                    operation.CancellationToken);
                generationContext = BuildGenerationContext(sharedContext, responderSelection.CharacterId);
                await UpdateResponderCompletionAsync(
                    request.ThreadId,
                    runId,
                    messageId,
                    request.Mode,
                    trimmedGuidancePrompt,
                    request.RequestedTurnShape,
                    generationContext,
                    appearanceResolution,
                    responderSelection,
                    cancellationToken);
                PublishWorkspaceRefresh(request.ThreadId);
            }
            else
            {
                generationContext = BuildGenerationContext(sharedContext, request.SpeakerCharacterId);
            }

            var plannerStage = await RunPlannerStageAsync(agent, request, generationContext, generationSettings, operation.CancellationToken);
            var planner = plannerStage.Planner;
            await UpdatePlannerCompletionAsync(request.ThreadId, runId, planner, plannerStage.TokenUsage, generationContext, appearanceResolution, responderSelection, trimmedGuidancePrompt, request.RequestedTurnShape, request.Mode, cancellationToken);
            PublishWorkspaceRefresh(request.ThreadId);

            proseRequest = new StoryMessageProseRequest(request.Mode, trimmedGuidancePrompt, request.RequestedTurnShape, generationContext, planner);
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
            await CompleteRunAsync(request.ThreadId, runId, messageId, proseRequest, appearanceResolution, responderSelection, proseText.FinalMessage, proseText.TokenUsage, cancellationToken);
            if (streamHandler is not null)
                await streamHandler(new StorySceneMessageStreamUpdate(request.ThreadId, messageId, proseText.FinalMessage, true), cancellationToken);

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
                responderSelection,
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
                responderSelection,
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

    private async Task<PlannerStageExecutionResult> RunPlannerStageAsync(
        ConfiguredAgent agent,
        PostStorySceneMessage request,
        StorySceneGenerationContext context,
        StoryGenerationSettingsView generationSettings,
        CancellationToken cancellationToken)
    {
        var prompt = await promptLibraryService.RenderPlanningPromptAsync(
            request.ThreadId,
            request,
            context,
            cancellationToken);

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt.SystemPrompt),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt.UserPrompt)
        ];

        var response = await agent.ChatClient.GetResponseAsync<PlannerStageResponse>(
            messages,
            options: BuildPlannerOptions(generationSettings),
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var planner = response.Result;

        return new PlannerStageExecutionResult(
            new StoryMessagePlannerResult(
                request.RequestedTurnShape ?? NormalizeTurnShape(planner.TurnShape),
                RequireValue(planner.Beat, "planner beat"),
                RequireValue(planner.Intent, "planner intent"),
                RequireValue(planner.ImmediateGoal, "planner immediate goal"),
                RequireValue(planner.WhyNow, "planner why now"),
                RequireValue(planner.ChangeIntroduced, "planner change introduced"),
                RequireValue(planner.PrivateIntent, "planner private intent"),
                NormalizeItems(planner.NarrativeGuardrails),
                NormalizeItems(planner.ContentGuardrails)),
            StoryMessageTokenUsageMapper.Map(response.Usage));
    }

    private async Task<StorySceneResponderSelectionResult> RunResponderSelectionStageAsync(
        ConfiguredAgent agent,
        Guid threadId,
        StoryScenePostMode mode,
        string? guidancePrompt,
        Guid? activeSpeakerCharacterId,
        StorySceneSharedContext context,
        StorySceneAppearanceResolution appearance,
        StoryGenerationSettingsView generationSettings,
        CancellationToken cancellationToken)
    {
        var activeSpeaker = BuildActorContext(activeSpeakerCharacterId, context.CharacterDocuments);
        var candidates = GetResponderCandidates(context.Characters, activeSpeakerCharacterId);
        if (candidates.Count == 1)
        {
            return new StorySceneResponderSelectionResult(
                activeSpeaker.CharacterId,
                activeSpeaker.Name,
                candidates[0].CharacterId,
                candidates[0].Name,
                $"Only {candidates[0].Name} was eligible to respond next.");
        }

        var prompt = await promptLibraryService.RenderSelectionPromptAsync(
            threadId,
            activeSpeaker,
            candidates,
            context.StoryContext,
            context.CurrentLocation,
            context.TranscriptSinceSnapshot,
            appearance,
            UsesGuidance(mode) ? guidancePrompt : null,
            cancellationToken);

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt.SystemPrompt),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt.UserPrompt)
        ];

        var response = await agent.ChatClient.GetResponseAsync<ResponderStageResponse>(
            messages,
            options: BuildStageOptions(generationSettings.Planning),
            useJsonSchemaResponseFormat: agent.UseJsonSchemaResponseFormat,
            cancellationToken: cancellationToken);
        var selectedCandidate = ResolveResponderCandidate(candidates, response.Result.CharacterName);

        return new StorySceneResponderSelectionResult(
            activeSpeaker.CharacterId,
            activeSpeaker.Name,
            selectedCandidate.CharacterId,
            selectedCandidate.Name,
            RequireValue(response.Result.WhyThisCharacter, "responder selection why-this-character"));
    }

    private async Task<ProseStageExecutionResult> StreamProseStageAsync(
        ConfiguredAgent agent,
        Guid threadId,
        Guid messageId,
        StoryMessageProseRequest request,
        StoryGenerationSettingsView generationSettings,
        StorySceneMessageStreamHandler? streamHandler,
        Action<string> partialProseTextChanged,
        CancellationToken cancellationToken)
    {
        var prompt = await promptLibraryService.RenderProsePromptAsync(threadId, request, cancellationToken);

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt.SystemPrompt),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt.UserPrompt)
        ];

        var rawProseBuilder = new StringBuilder();
        var responseUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in agent.ChatClient.GetStreamingResponseAsync(
            messages,
            options: BuildStageOptions(generationSettings.Writing),
            cancellationToken: cancellationToken))
        {
            responseUpdates.Add(update.Clone());
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
        {
            var response = responseUpdates.Count == 0
                ? null
                : await EnumerateResponseUpdatesAsync(responseUpdates, cancellationToken).ToChatResponseAsync(cancellationToken);
            return new ProseStageExecutionResult(normalizedProse, StoryMessageTokenUsageMapper.Map(response?.Usage));
        }

        throw new InvalidOperationException($"Writing the scene message as {request.Context.Actor.Name} failed because the prose stage returned an empty message.");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EnumerateResponseUpdatesAsync(
        IReadOnlyList<ChatResponseUpdate> responseUpdates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var update in responseUpdates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.CompletedTask;
        }
    }

    private async Task UpdateAppearanceCompletionAsync(
        Guid threadId,
        Guid runId,
        StoryScenePostMode mode,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
        StorySceneSharedContext context,
        StorySceneAppearanceResolution appearance,
        StoryMessageTokenUsage? tokenUsage,
        bool skipAppearanceStep,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        run.Stage = IsRespondMode(mode) ? "Responder" : "Planning";
        run.Summary = appearance.LatestEntry?.Summary ?? "Resolved current appearance for the active scene.";
        if (!IsRespondMode(mode))
            run.PlanningStartedUtc = now;
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            mode,
            guidancePrompt,
            requestedTurnShape,
            null,
            appearance,
            null,
            null,
            null,
            null,
            BuildAppearanceCompletedStepArtifacts(mode, requestedTurnShape, context, appearance)));

        if (skipAppearanceStep)
            RemoveStepIfPresent(dbContext, run, ProcessStepKeys.Appearance);
        else
            CompleteStepIfPresent(
                run,
                ProcessStepKeys.Appearance,
                now,
                appearance.LatestEntry?.Summary ?? "Current appearance was resolved for the active branch.",
                StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance),
                tokenUsage);

        if (IsRespondMode(mode))
        {
            StartStepIfPresent(
                run,
                ProcessStepKeys.Responder,
                now,
                "Choosing which in-scene character should answer next without reusing the active speaker.");
        }
        else
        {
            StartStepIfPresent(
                run,
                ProcessStepKeys.Planning,
                now,
                "The planner is reviewing the actor, scene, appearance state, snapshot summary, and recent transcript.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateResponderCompletionAsync(
        Guid threadId,
        Guid runId,
        Guid messageId,
        StoryScenePostMode mode,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult responderSelection,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.ProcessRuns
            .Include(x => x.Steps)
            .FirstAsync(x => x.Id == runId && x.ThreadId == threadId, cancellationToken);
        var message = await dbContext.ChatMessages
            .FirstAsync(x => x.Id == messageId && x.ThreadId == threadId, cancellationToken);
        var now = DateTime.UtcNow;

        run.Stage = "Planning";
        run.ActorCharacterId = responderSelection.CharacterId;
        run.Summary = BuildResponderSummary(responderSelection);
        run.PlanningStartedUtc = now;
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            mode,
            guidancePrompt,
            requestedTurnShape,
            context,
            appearance,
            responderSelection,
            null,
            null,
            null,
            BuildResponderCompletedStepArtifacts(mode, guidancePrompt, requestedTurnShape, context, appearance, responderSelection)));

        message.SpeakerCharacterId = responderSelection.CharacterId;
        message.MessageKind = ResolveMessageKind(responderSelection.CharacterId);

        UpdateStepSummaryIfPresent(
            run,
            ProcessStepKeys.Appearance,
            appearance.LatestEntry?.Summary ?? "The latest branch-local appearance was reused without changes.",
            StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance));
        CompleteStepIfPresent(
            run,
            ProcessStepKeys.Responder,
            now,
            BuildResponderSummary(responderSelection),
            BuildResponderDetail(responderSelection));
        StartStepIfPresent(
            run,
            ProcessStepKeys.Planning,
            now,
            "The planner is reviewing the chosen responder, scene state, appearance state, snapshot summary, and recent transcript.");

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdatePlannerCompletionAsync(
        Guid threadId,
        Guid runId,
        StoryMessagePlannerResult planner,
        StoryMessageTokenUsage? tokenUsage,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult? responderSelection,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
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
            requestedTurnShape,
            context,
            appearance,
            responderSelection,
            planner,
            null,
            null,
            BuildPlanningCompletedStepArtifacts(mode, guidancePrompt, requestedTurnShape, context, appearance, responderSelection, planner)));

        UpdateStepSummaryIfPresent(
            run,
            ProcessStepKeys.Appearance,
            appearance.LatestEntry?.Summary ?? "The latest branch-local appearance was reused without changes.",
            StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance));
        if (responderSelection is not null)
            UpdateStepSummaryIfPresent(run, ProcessStepKeys.Responder, BuildResponderSummary(responderSelection), BuildResponderDetail(responderSelection));
        CompleteStepIfPresent(run, ProcessStepKeys.Planning, now, BuildPlannerSummary(planner), StorySceneSharedPromptBuilder.BuildPlannerDetail(planner), tokenUsage);
        StartStepIfPresent(
            run,
            ProcessStepKeys.Writing,
            now,
            "The prose stage is turning the approved plan into the final scene message.");

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CompleteRunAsync(
        Guid threadId,
        Guid runId,
        Guid messageId,
        StoryMessageProseRequest proseRequest,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult? responderSelection,
        string finalMessage,
        StoryMessageTokenUsage? tokenUsage,
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
        message.PrivateIntent = NormalizeOptionalText(proseRequest.Planner.PrivateIntent);
        thread.UpdatedUtc = now;
        run.Stage = null;
        run.Status = ProcessRunStatus.Completed;
        run.ProseCompletedUtc = now;
        run.CompletedUtc = now;
        run.Summary = $"Completed a {DescribeMode(proseRequest.Mode).ToLowerInvariant()} message as {proseRequest.Context.Actor.Name}.";
        run.ContextJson = SerializeContext(new StoryMessageProcessContext(
            proseRequest.Mode,
            proseRequest.GuidancePrompt,
            proseRequest.RequestedTurnShape,
            proseRequest.Context,
            appearance,
            responderSelection,
            proseRequest.Planner,
            proseRequest,
            finalMessage,
            BuildCompletedStepArtifacts(proseRequest, appearance, responderSelection, finalMessage)));

        CompleteStepIfPresent(run, ProcessStepKeys.Writing, now, "The final message was written and saved to the transcript.", BuildProseDetail(proseRequest, finalMessage), tokenUsage);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelRunAsync(
        Guid threadId,
        Guid runId,
        StoryMessageProseRequest? proseRequest,
        StorySceneAppearanceResolution? appearance,
        StorySceneResponderSelectionResult? responderSelection,
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
                proseRequest.RequestedTurnShape,
                proseRequest.Context,
                appearance,
                responderSelection,
                proseRequest.Planner,
                proseRequest,
                string.IsNullOrWhiteSpace(partialMessage) ? null : partialMessage,
                BuildCompletedStepArtifacts(
                    proseRequest,
                    appearance,
                    responderSelection,
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
                    IsProcessStep(step, ProcessStepKeys.Writing) && string.IsNullOrWhiteSpace(partialMessage)
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
        StorySceneResponderSelectionResult? responderSelection,
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
                    proseRequest.RequestedTurnShape,
                    proseRequest.Context,
                    appearance,
                    responderSelection,
                    proseRequest.Planner,
                    proseRequest,
                    partialMessage,
                    BuildCompletedStepArtifacts(proseRequest, appearance, responderSelection, partialMessage, "Partial Message")));
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

    private async Task<GenerationBuildResult> BuildSharedGenerationContextAsync(
        Guid threadId,
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
        StoryCharacterModelSheetSupport.EnsureReady(characters, _ => true, "Building the scene generation context");
        var locations = story.Locations.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = story.Items.Entries
            .Where(x => !x.IsArchived)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentLocation = story.Scene.CurrentLocationId.HasValue
            ? locations.FirstOrDefault(x => x.Id == story.Scene.CurrentLocationId.Value)
            : null;
        var transcriptSinceSnapshot = latestSnapshot is null
            ? effectivePath
            : effectivePath.Where(x => x.CreatedUtc > latestSnapshot.CoveredThroughUtc).ToList();
        var appearanceStage = await storyChatAppearanceService.ResolveLatestAppearanceAsync(
            threadId,
            effectivePath,
            story,
            writeChanges: true,
            cancellationToken);
        var appearance = appearanceStage.Appearance;
        var currentAppearanceLookup = appearance.EffectiveCharacters.ToDictionary(x => x.CharacterId, x => x.CurrentAppearance);

        var context = new StorySceneSharedContext(
            characters,
            currentLocation is null
                ? null
                : new StorySceneLocationContext(currentLocation.Id, currentLocation.Name, currentLocation.Summary, currentLocation.Details),
            characters.Select(character =>
                {
                    var modelSheet = StoryCharacterModelSheetSupport.GetModelSheet(character);
                    return new StorySceneCharacterContext(
                        character.Id,
                        character.Name,
                        modelSheet.Summary,
                        modelSheet.Appearance,
                        currentAppearanceLookup.TryGetValue(character.Id, out var currentAppearance) ? currentAppearance : string.Empty,
                        modelSheet.Voice,
                        modelSheet.Hides,
                        modelSheet.Tendency,
                        modelSheet.Constraint,
                        modelSheet.Relationships,
                        modelSheet.LikesBeliefs,
                        modelSheet.PrivateMotivations,
                        story.Scene.PresentCharacterIds.Contains(character.Id));
                })
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
                    message.Content,
                    message.SpeakerCharacterId,
                    message.PrivateIntent))
                .ToList(),
            effectivePath.Select(message => new StorySceneTranscriptMessage(
                    message.Id,
                    message.CreatedUtc,
                    ResolveSpeakerName(message, characters),
                    message.MessageKind == ChatMessageKind.Narration,
                    message.Content,
                    message.SpeakerCharacterId,
                    message.PrivateIntent))
                .ToList());

        return new GenerationBuildResult(
            context,
            appearance,
            appearanceStage.TokenUsage,
            ShouldSkipAppearanceProcessStep(appearance, appearanceStage.TokenUsage));
    }

    private static bool ShouldSkipAppearanceProcessStep(
        StorySceneAppearanceResolution appearance,
        StoryMessageTokenUsage? tokenUsage) =>
        tokenUsage is null
        && appearance.LatestEntry is not null
        && appearance.TranscriptSinceLatestEntry.Count == 0;

    private static StorySceneGenerationContext BuildGenerationContext(StorySceneSharedContext context, Guid? speakerCharacterId)
    {
        var actor = BuildActorContext(speakerCharacterId, context.CharacterDocuments);
        return new(
            actor,
            context.CurrentLocation,
            context.Characters,
            context.SceneObjects,
            context.StoryContext,
            context.HistorySummary,
            context.LatestSnapshot,
            FilterTranscriptPrivateIntentForActor(context.TranscriptSinceSnapshot, actor),
            FilterTranscriptPrivateIntentForActor(context.PrivateIntentTranscript ?? context.TranscriptSinceSnapshot, actor));
    }

    private static IReadOnlyList<StorySceneTranscriptMessage> FilterTranscriptPrivateIntentForActor(
        IReadOnlyList<StorySceneTranscriptMessage> transcript,
        StorySceneActorContext actor) =>
        transcript
            .Select(message => ShouldIncludePrivateIntentForActor(message, actor)
                ? message
                : message with { PrivateIntent = null })
            .ToList();

    private static bool ShouldIncludePrivateIntentForActor(StorySceneTranscriptMessage message, StorySceneActorContext actor) =>
        actor.IsNarrator
            ? message.IsNarrator && !message.SpeakerCharacterId.HasValue
            : message.SpeakerCharacterId == actor.CharacterId;

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
                string.Empty,
                string.Empty,
                string.Empty,
                "Speak in concise descriptive prose. Introduce or clarify facts without inventing contradictions.",
            BuildNarratorHiddenKnowledge(characters));
    }

        var character = characters.FirstOrDefault(x => x.Id == speakerCharacterId.Value)
            ?? throw new InvalidOperationException("Building the scene context failed because the selected speaker could not be found.");
        var modelSheet = StoryCharacterModelSheetSupport.GetModelSheet(character);

        return new StorySceneActorContext(
            character.Id,
            character.Name,
            false,
            modelSheet.Summary,
            modelSheet.Appearance,
            modelSheet.Voice,
            modelSheet.Hides,
            modelSheet.Tendency,
            modelSheet.Constraint,
            modelSheet.Relationships,
            modelSheet.LikesBeliefs,
            modelSheet.PrivateMotivations,
            string.Empty,
            string.Empty);
    }

    private static async Task<Dictionary<Guid, PrimaryImageData>> LoadPrimaryImagesAsync(
        DbAppContext dbContext,
        ChatStory story,
        CancellationToken cancellationToken)
    {
        var imageIds = story.Characters.Entries
            .Select(x => x.PrimaryImageId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        if (imageIds.Count == 0)
            return [];

        return await dbContext.StoryImageAssets
            .AsNoTracking()
            .Where(x => imageIds.Contains(x.Id))
            .ToDictionaryAsync(
                x => x.Id,
                x => new PrimaryImageData(StoryImageUrlBuilder.Build(x.Id), BuildCrop(x)),
                cancellationToken);
    }

    private static string? GetPrimaryImageUrl(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.ImageUrl : null;

    private static StoryImageAvatarCropView GetPrimaryImageCrop(IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages, Guid? imageId) =>
        imageId.HasValue && primaryImages.TryGetValue(imageId.Value, out var data) ? data.Crop : StoryImageAvatarCropView.Default;

    private static StoryImageAvatarCropView BuildCrop(StoryImageAsset image) =>
        new(
            Math.Clamp(image.AvatarFocusXPercent ?? StoryImageAvatarCropView.Default.FocusXPercent, 0, 100),
            Math.Clamp(image.AvatarFocusYPercent ?? StoryImageAvatarCropView.Default.FocusYPercent, 0, 100),
            Math.Clamp(image.AvatarZoomPercent ?? StoryImageAvatarCropView.Default.ZoomPercent, 100, 300));

    private static string BuildNarratorHiddenKnowledge(IReadOnlyList<StoryCharacterDocument> characters)
    {
        var hiddenDetails = characters
            .Select(x => new
            {
                x.Name,
                ModelSheet = StoryCharacterModelSheetSupport.GetModelSheet(x)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ModelSheet.Hides) || !string.IsNullOrWhiteSpace(x.ModelSheet.PrivateMotivations))
            .Select(x => $"{x.Name}: Hides: {StorySceneSharedPromptBuilder.PromptInlineText(x.ModelSheet.Hides, "None")}; Private motivations: {StorySceneSharedPromptBuilder.PromptInlineText(x.ModelSheet.PrivateMotivations, "None")}")
            .ToList();

        return hiddenDetails.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, hiddenDetails);
    }

    private static IReadOnlyList<StorySceneSpeakerView> BuildSpeakers(
        ChatStory story,
        IReadOnlyList<StoryCharacterDocument> characters,
        Guid? selectedSpeakerId,
        IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages)
    {
        var speakers = new List<StorySceneSpeakerView>
        {
            new(
                null,
                "Narrator",
                "Injects scene facts and framing details.",
                true,
                true,
                !selectedSpeakerId.HasValue,
                null,
                null,
                StoryImageAvatarCropView.Default)
        };

        speakers.AddRange(characters.Select(character => new StorySceneSpeakerView(
            character.Id,
            character.Name,
            StoryCharacterModelSheetSupport.GetUserSheet(character).Summary,
            false,
            story.Scene.PresentCharacterIds.Contains(character.Id),
            selectedSpeakerId == character.Id,
            character.PrimaryImageId,
            GetPrimaryImageUrl(primaryImages, character.PrimaryImageId),
            GetPrimaryImageCrop(primaryImages, character.PrimaryImageId))));

        return speakers;
    }

    private static StorySceneMessageView MapMessage(
        DbChatMessage message,
        int directChildCount,
        int descendantCount,
        Guid? selectedSpeakerId,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages,
        IReadOnlyDictionary<Guid, StorySceneMessageProcessView> processMap,
        IReadOnlySet<Guid> snapshotCandidateMessageIds,
        StorySceneBranchNavigatorView? branchNavigator)
    {
        var process = message.SourceProcessRunId.HasValue && processMap.TryGetValue(message.Id, out var mappedProcess) ? mappedProcess : null;
        var speaker = message.SpeakerCharacterId.HasValue
            ? characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)
            : null;
        var canonicalSpeakerName = ResolveMessageSpeakerName(message, characters, process);
        var isSelectedSpeaker = selectedSpeakerId == message.SpeakerCharacterId
            || (!selectedSpeakerId.HasValue && message.MessageKind == ChatMessageKind.Narration);
        var canSaveInPlace = snapshotCandidateMessageIds.Contains(message.Id);

        return new StorySceneMessageView(
            message.Id,
            message.MessageKind,
            message.GenerationMode,
            message.Content,
            message.PrivateIntent,
            message.CreatedUtc,
            message.SpeakerCharacterId,
            canonicalSpeakerName,
            isSelectedSpeaker ? "You" : canonicalSpeakerName,
            message.MessageKind == ChatMessageKind.Narration,
            speaker?.PrimaryImageId,
            GetPrimaryImageUrl(primaryImages, speaker?.PrimaryImageId),
            GetPrimaryImageCrop(primaryImages, speaker?.PrimaryImageId),
            isSelectedSpeaker,
            canSaveInPlace,
            canSaveInPlace,
            branchNavigator,
            new StorySceneDeleteCapabilitiesView(
                DirectChildCount: directChildCount,
                DescendantCount: descendantCount,
                CanDeleteSingleMessage: true,
                CanDeleteBranch: descendantCount > 0),
            process);
    }

    private static IReadOnlyList<StorySceneTranscriptNodeView> BuildTranscript(
        IReadOnlyList<DbChatMessage> selectedPath,
        IReadOnlyList<DbChatMessage> allMessages,
        ILookup<Guid?, DbChatMessage> childrenLookup,
        IReadOnlyDictionary<Guid, int> descendantCounts,
        Guid? selectedSpeakerId,
        IReadOnlyList<StoryCharacterDocument> characters,
        IReadOnlyDictionary<Guid, PrimaryImageData> primaryImages,
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
                    primaryImages,
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

            var appearanceStep = FindProcessStep(run, ProcessStepKeys.Appearance);
            if (appearanceStep is not null)
            {
                appearanceStep.Summary = updatedAppearance.Summary;
                appearanceStep.Detail = TruncateProcessDetail(StorySceneSharedPromptBuilder.BuildAppearanceDetail(refreshedAppearance));
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
                step.InputTokenCount,
                step.OutputTokenCount,
                step.TotalTokenCount,
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
            .Select(step => GetProcessStepKey(step.Title) == ProcessStepKeys.Appearance
                ? step with
                {
                    Summary = updatedAppearanceEntry.Summary,
                    Detail = TruncateProcessDetail(StorySceneSharedPromptBuilder.BuildAppearanceDetail(refreshedAppearance))
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

    private static void CompleteProcessStep(ProcessStep step, DateTime completedUtc, string summary, string detail, StoryMessageTokenUsage? tokenUsage = null)
    {
        step.Status = ProcessStepStatus.Completed;
        step.StartedUtc ??= completedUtc;
        step.CompletedUtc = completedUtc;
        step.Summary = summary;
        step.Detail = TruncateProcessDetail(detail);
        step.InputTokenCount = tokenUsage?.InputTokenCount;
        step.OutputTokenCount = tokenUsage?.OutputTokenCount;
        step.TotalTokenCount = tokenUsage?.TotalTokenCount;
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

    private static bool IsProcessStep(ProcessStep step, string stepKey) => GetProcessStepKey(step.Title) == stepKey;

    private static string GetProcessStepKey(string title)
    {
        var normalized = title.Trim().ToLowerInvariant();
        return normalized switch
        {
            "appearance" => ProcessStepKeys.Appearance,
            "responder" => ProcessStepKeys.Responder,
            "planning" => ProcessStepKeys.Planning,
            "writing" => ProcessStepKeys.Writing,
            _ => normalized
        };
    }

    private static ProcessStep? FindProcessStep(ProcessRun run, string stepKey) =>
        run.Steps.FirstOrDefault(step => IsProcessStep(step, stepKey));

    private static void StartStepIfPresent(ProcessRun run, string stepKey, DateTime startedUtc, string detail)
    {
        var step = FindProcessStep(run, stepKey);
        if (step is not null)
            StartProcessStep(step, startedUtc, detail);
    }

    private static void CompleteStepIfPresent(ProcessRun run, string stepKey, DateTime completedUtc, string summary, string detail, StoryMessageTokenUsage? tokenUsage = null)
    {
        var step = FindProcessStep(run, stepKey);
        if (step is not null)
            CompleteProcessStep(step, completedUtc, summary, detail, tokenUsage);
    }

    private static void RemoveStepIfPresent(DbAppContext dbContext, ProcessRun run, string stepKey)
    {
        var step = FindProcessStep(run, stepKey);
        if (step is null)
            return;

        run.Steps.Remove(step);
        dbContext.ProcessSteps.Remove(step);
    }

    private static void UpdateStepSummaryIfPresent(ProcessRun run, string stepKey, string summary, string detail)
    {
        var step = FindProcessStep(run, stepKey);
        if (step is null)
            return;

        step.Summary = summary;
        step.Detail = TruncateProcessDetail(detail);
    }

    private static IReadOnlyList<StorySceneCharacterContext> GetResponderCandidates(
        IReadOnlyList<StorySceneCharacterContext> characters,
        Guid? activeSpeakerCharacterId)
    {
        var candidates = characters
            .Where(x => x.IsPresentInScene)
            .Where(x => !activeSpeakerCharacterId.HasValue || x.CharacterId != activeSpeakerCharacterId.Value)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count > 0)
            return candidates;

        throw new InvalidOperationException("Generating the response failed because another present character is required.");
    }

    private static StorySceneCharacterContext ResolveResponderCandidate(
        IReadOnlyList<StorySceneCharacterContext> candidates,
        string? selectedCharacterName)
    {
        var requestedName = RequireValue(selectedCharacterName, "responder selection character");
        var exactMatch = candidates.FirstOrDefault(x => x.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
            return exactMatch;

        var partialMatches = candidates
            .Where(x => x.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partialMatches.Count == 1)
            return partialMatches[0];

        throw new InvalidOperationException("Selecting the next responder failed because the model returned an ineligible character.");
    }

    private static StorySceneSharedContext CreateSharedContext(StorySceneGenerationContext context) => new(
        [],
        context.CurrentLocation,
        context.Characters,
        context.SceneObjects,
        context.StoryContext,
        context.HistorySummary,
        context.LatestSnapshot,
        context.TranscriptSinceSnapshot,
        context.PrivateIntentTranscript);

    private static StorySceneActorContext BuildResponderActiveSpeakerContext(
        StorySceneGenerationContext context,
        StorySceneResponderSelectionResult responderSelection)
    {
        if (!responderSelection.ActiveSpeakerCharacterId.HasValue)
        {
            return new StorySceneActorContext(
                null,
                responderSelection.ActiveSpeakerName,
                true,
                "An always-present narrator who injects reliable scene facts and framing details.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "Speak in concise descriptive prose. Introduce or clarify facts without inventing contradictions.",
                string.Empty);
        }

        var activeSpeaker = context.Characters.FirstOrDefault(x => x.CharacterId == responderSelection.ActiveSpeakerCharacterId.Value);
        return activeSpeaker is null
            ? new StorySceneActorContext(
                responderSelection.ActiveSpeakerCharacterId,
                responderSelection.ActiveSpeakerName,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty)
            : new StorySceneActorContext(
                activeSpeaker.CharacterId,
                activeSpeaker.Name,
                false,
                activeSpeaker.Summary,
                activeSpeaker.Appearance,
                activeSpeaker.Voice,
                activeSpeaker.Hides,
                activeSpeaker.Tendency,
                activeSpeaker.Constraint,
                activeSpeaker.Relationships,
                activeSpeaker.LikesBeliefs,
                activeSpeaker.PrivateMotivations,
                string.Empty,
                string.Empty);
    }

    private static List<ProcessStep> BuildInitialProcessSteps(StoryScenePostMode mode, DateTime now)
    {
        var steps = new List<ProcessStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SortOrder = 0,
                Title = "Appearance",
                Summary = "Resolving the current appearance of characters in the active scene.",
                Detail = "The appearance stage is reviewing the latest branch-local appearance block and recent transcript.",
                IconCssClass = "fa-regular fa-shirt",
                Status = ProcessStepStatus.Running,
                StartedUtc = now
            }
        };

        var nextSortOrder = 1;
        if (IsRespondMode(mode))
        {
            steps.Add(new ProcessStep
            {
                Id = Guid.NewGuid(),
                SortOrder = nextSortOrder++,
                Title = "Responder",
                Summary = "Choosing which in-scene character should answer next.",
                Detail = "The responder stage will run after the latest current appearance is resolved.",
                IconCssClass = "fa-regular fa-user-check",
                Status = ProcessStepStatus.Pending
            });
        }

        steps.Add(new ProcessStep
        {
            Id = Guid.NewGuid(),
            SortOrder = nextSortOrder++,
            Title = "Planning",
            Summary = "Determining the beat, intent, change, and guardrails for the next message.",
            Detail = IsRespondMode(mode)
                ? "The planner will run after the next responder is chosen."
                : "The planner will run after the latest current appearance is resolved.",
            IconCssClass = "fa-regular fa-map",
            Status = ProcessStepStatus.Pending
        });
        steps.Add(new ProcessStep
        {
            Id = Guid.NewGuid(),
            SortOrder = nextSortOrder,
            Title = "Writing",
            Summary = "Drafting the scene message in the actor's voice.",
            Detail = "The prose stage will turn the planner result into the final message.",
            IconCssClass = "fa-regular fa-pen-line",
            Status = ProcessStepStatus.Pending
        });
        return steps;
    }

    private static class ProcessStepKeys
    {
        public const string Appearance = "appearance";
        public const string Responder = "responder";
        public const string Planning = "planning";
        public const string Writing = "writing";
    }

    private static string BuildResponderSummary(StorySceneResponderSelectionResult responderSelection) =>
        $"Selected {responderSelection.CharacterName} to answer next.";

    private static string BuildResponderDetail(StorySceneResponderSelectionResult responderSelection) =>
        $"**Responder:** {responderSelection.CharacterName}\n**Why now:** {responderSelection.WhyThisCharacter}";

    private static string BuildProseDetail(StoryMessageProseRequest request, string finalMessage)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.GuidancePrompt))
            builder.AppendLine($"**Guidance prompt:** {request.GuidancePrompt}");

        if (request.RequestedTurnShape.HasValue)
            builder.AppendLine($"**Requested turn shape:** {StorySceneSharedPromptBuilder.FormatTurnShape(request.RequestedTurnShape.Value)}");

        builder.AppendLine($"**Actor:** {request.Context.Actor.Name}");
        builder.AppendLine($"**Plan:** {BuildPlannerSummary(request.Planner)}");
        builder.AppendLine("**Final message:**");
        builder.AppendLine(finalMessage);
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
        return combined.Count == 0 ? "" : string.Join(" | ", combined);
    }

    private async Task<bool> CanSaveMessageInPlaceAsync(
        DbAppContext dbContext,
        ChatThread thread,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var selectedLeafMessageId = ResolveSelectedLeafMessageId(thread.Messages, thread.ActiveLeafMessageId);
        if (!selectedLeafMessageId.HasValue)
            return false;

        var processStatusMap = await dbContext.ProcessRuns
            .AsNoTracking()
            .Where(x => x.ThreadId == thread.Id && x.TargetMessageId.HasValue)
            .ToDictionaryAsync(x => x.TargetMessageId!.Value, x => x.Status, cancellationToken);
        var selectedPath = BuildSelectedPath(thread.Messages, selectedLeafMessageId.Value);
        var effectivePath = ExcludeStoppedPlaceholderMessages(selectedPath, processStatusMap);
        var effectiveLeafMessageId = effectivePath.LastOrDefault()?.Id;
        var latestSnapshot = effectiveLeafMessageId.HasValue
            ? await storyChatSnapshotService.GetLatestSnapshotAsync(thread.Id, effectiveLeafMessageId.Value, cancellationToken)
            : null;
        var snapshotCandidateMessageIds = StoryChatSnapshotService.GetSnapshotCandidateMessageIds(effectivePath, latestSnapshot).ToHashSet();
        return snapshotCandidateMessageIds.Contains(messageId);
    }

    private static bool HasMessageSpeaker(DbChatMessage message, Guid? speakerCharacterId) =>
        speakerCharacterId.HasValue
            ? message.MessageKind == ChatMessageKind.CharacterSpeech && message.SpeakerCharacterId == speakerCharacterId.Value
            : message.MessageKind == ChatMessageKind.Narration && !message.SpeakerCharacterId.HasValue;

    private static void AssignMessageSpeaker(DbChatMessage message, Guid? speakerCharacterId)
    {
        message.SpeakerCharacterId = speakerCharacterId;
        message.MessageKind = ResolveMessageKind(speakerCharacterId);
    }

    private static ProcessRun CloneProcessRunForSpeakerChange(
        ProcessRun sourceRun,
        ChatThread thread,
        Guid targetMessageId,
        Guid? speakerCharacterId,
        StorySceneActorContext updatedActor)
    {
        var clonedRun = new ProcessRun
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            UserMessageId = targetMessageId,
            AssistantMessageId = targetMessageId,
            TargetMessageId = targetMessageId,
            ActorCharacterId = speakerCharacterId,
            AiModelId = sourceRun.AiModelId,
            AiProviderId = sourceRun.AiProviderId,
            AiProviderKind = sourceRun.AiProviderKind,
            Summary = sourceRun.Summary,
            Stage = sourceRun.Stage,
            ContextJson = sourceRun.ContextJson,
            Status = sourceRun.Status,
            StartedUtc = sourceRun.StartedUtc,
            PlanningStartedUtc = sourceRun.PlanningStartedUtc,
            PlanningCompletedUtc = sourceRun.PlanningCompletedUtc,
            ProseStartedUtc = sourceRun.ProseStartedUtc,
            ProseCompletedUtc = sourceRun.ProseCompletedUtc,
            CompletedUtc = sourceRun.CompletedUtc,
            Steps = []
        };

        clonedRun.Steps = sourceRun.Steps
            .OrderBy(x => x.SortOrder)
            .Select(step => new ProcessStep
            {
                Id = Guid.NewGuid(),
                ProcessRunId = clonedRun.Id,
                SortOrder = step.SortOrder,
                Title = step.Title,
                Summary = step.Summary,
                Detail = step.Detail,
                IconCssClass = step.IconCssClass,
                Status = step.Status,
                StartedUtc = step.StartedUtc,
                CompletedUtc = step.CompletedUtc,
                InputTokenCount = step.InputTokenCount,
                OutputTokenCount = step.OutputTokenCount,
                TotalTokenCount = step.TotalTokenCount,
                Run = clonedRun
            })
            .ToList();

        RewriteProcessRunForSpeakerChange(clonedRun, targetMessageId, speakerCharacterId, updatedActor);
        return clonedRun;
    }

    private static void RewriteProcessRunForSpeakerChange(
        ProcessRun run,
        Guid targetMessageId,
        Guid? speakerCharacterId,
        StorySceneActorContext updatedActor)
    {
        run.UserMessageId = targetMessageId;
        run.AssistantMessageId = targetMessageId;
        run.TargetMessageId = targetMessageId;
        run.ActorCharacterId = speakerCharacterId;

        var processContext = DeserializeContext(run.ContextJson);
        if (processContext is null)
            return;

        var updatedGenerationContext = processContext.GenerationContext is null
            ? null
            : processContext.GenerationContext with { Actor = updatedActor };
        var updatedResponderSelection = processContext.ResponderSelection is null || updatedActor.IsNarrator
            ? null
            : processContext.ResponderSelection with
            {
                CharacterId = speakerCharacterId
                    ?? throw new InvalidOperationException("Changing the message speaker failed because the responder character could not be resolved."),
                CharacterName = updatedActor.Name,
                WhyThisCharacter = "The sender was reassigned after generation."
            };
        var updatedPlanner = processContext.Planner is null
            ? null
            : processContext.Planner with { PrivateIntent = string.Empty };
        var updatedProseRequest = processContext.ProseRequest is null
            ? null
            : processContext.ProseRequest with
            {
                Context = processContext.ProseRequest.Context with { Actor = updatedActor },
                Planner = updatedPlanner ?? processContext.ProseRequest.Planner with { PrivateIntent = string.Empty }
            };

        var updatedStepArtifacts = processContext.StepArtifacts;
        if (updatedGenerationContext is not null && processContext.Appearance is not null)
        {
            if (updatedProseRequest is not null && !string.IsNullOrWhiteSpace(processContext.FinalMessage))
            {
                updatedStepArtifacts = BuildCompletedStepArtifacts(
                    updatedProseRequest,
                    processContext.Appearance,
                    updatedResponderSelection,
                    processContext.FinalMessage);
            }
            else if (updatedPlanner is not null)
            {
                updatedStepArtifacts = BuildPlanningCompletedStepArtifacts(
                    processContext.Mode,
                    processContext.GuidancePrompt,
                    processContext.RequestedTurnShape,
                    updatedGenerationContext,
                    processContext.Appearance,
                    updatedResponderSelection,
                    updatedPlanner);
            }
            else if (updatedResponderSelection is not null)
            {
                updatedStepArtifacts = BuildResponderCompletedStepArtifacts(
                    processContext.Mode,
                    processContext.GuidancePrompt,
                    processContext.RequestedTurnShape,
                    updatedGenerationContext,
                    processContext.Appearance,
                    updatedResponderSelection);
            }
            else
            {
                updatedStepArtifacts = BuildAppearanceCompletedStepArtifacts(
                    processContext.Mode,
                    processContext.RequestedTurnShape,
                    CreateSharedContext(updatedGenerationContext),
                    processContext.Appearance);
            }
        }

        var updatedContext = processContext with
        {
            GenerationContext = updatedGenerationContext,
            ResponderSelection = updatedResponderSelection,
            Planner = updatedPlanner,
            ProseRequest = updatedProseRequest,
            StepArtifacts = updatedStepArtifacts
        };

        run.ContextJson = SerializeContext(updatedContext);

        if (run.Status == ProcessRunStatus.Completed && updatedProseRequest is not null && !string.IsNullOrWhiteSpace(updatedContext.FinalMessage))
            run.Summary = $"Completed a {DescribeMode(updatedContext.Mode).ToLowerInvariant()} message as {updatedActor.Name}.";
        else if (run.Status is not ProcessRunStatus.Canceled and not ProcessRunStatus.Failed)
            run.Summary = updatedContext.Planner is not null
                ? BuildPlannerSummary(updatedContext.Planner)
                : updatedResponderSelection is not null
                    ? BuildResponderSummary(updatedResponderSelection)
                    : updatedGenerationContext is not null
                        ? BuildInitialRunSummary(updatedContext.Mode, updatedActor)
                        : run.Summary;

        if (updatedResponderSelection is not null)
            UpdateStepSummaryIfPresent(run, ProcessStepKeys.Responder, BuildResponderSummary(updatedResponderSelection), BuildResponderDetail(updatedResponderSelection));

        if (updatedProseRequest is not null && !string.IsNullOrWhiteSpace(updatedContext.FinalMessage))
            UpdateStepSummaryIfPresent(run, ProcessStepKeys.Writing, "The final message was written and saved to the transcript.", BuildProseDetail(updatedProseRequest, updatedContext.FinalMessage));
    }

    private static string ResolveSpeakerName(DbChatMessage message, IReadOnlyList<StoryCharacterDocument> characters)
    {
        if (message.MessageKind == ChatMessageKind.Narration)
            return "Narrator";

        if (message.SpeakerCharacterId.HasValue)
            return characters.FirstOrDefault(x => x.Id == message.SpeakerCharacterId.Value)?.Name ?? "Unknown Character";

        return "System";
    }

    private static string ResolveMessageSpeakerName(
        DbChatMessage message,
        IReadOnlyList<StoryCharacterDocument> characters,
        StorySceneMessageProcessView? process)
    {
        if (!message.SpeakerCharacterId.HasValue
            && IsRespondMode(message.GenerationMode)
            && message.MessageKind == ChatMessageKind.System)
        {
            if (!string.IsNullOrWhiteSpace(process?.Context?.ResponderSelection?.CharacterName))
                return process.Context.ResponderSelection.CharacterName;

            if (process?.Status == ProcessRunStatus.Running)
                return "Selecting responder";
        }

        return ResolveSpeakerName(message, characters);
    }

    private static Guid? ResolveSelectedSpeakerId(Guid? selectedSpeakerCharacterId, IReadOnlyList<StoryCharacterDocument> characters) =>
        selectedSpeakerCharacterId.HasValue && characters.Any(x => x.Id == selectedSpeakerCharacterId.Value)
            ? selectedSpeakerCharacterId
            : null;

    private static bool UsesGuidance(StoryScenePostMode mode) =>
        mode is StoryScenePostMode.GuidedAi or StoryScenePostMode.RespondGuidedAi;

    private static bool RequiresGuidance(StoryScenePostMode mode) =>
        mode is StoryScenePostMode.GuidedAi or StoryScenePostMode.RespondGuidedAi;

    private static bool IsRespondMode(StoryScenePostMode mode) =>
        mode is StoryScenePostMode.RespondGuidedAi or StoryScenePostMode.RespondAutomaticAi;

    private static string DescribeMode(StoryScenePostMode mode) => mode switch
    {
        StoryScenePostMode.Manual => "Manual",
        StoryScenePostMode.GuidedAi => "Guided AI",
        StoryScenePostMode.AutomaticAi => "Automatic AI",
        StoryScenePostMode.RespondGuidedAi => "Respond Guided AI",
        StoryScenePostMode.RespondAutomaticAi => "Respond Automatic AI",
        _ => mode.ToString()
    };

    private static ChatMessageKind ResolveMessageKind(Guid? speakerCharacterId) =>
        speakerCharacterId.HasValue ? ChatMessageKind.CharacterSpeech : ChatMessageKind.Narration;

    private static string BuildInitialRunSummary(StoryScenePostMode mode, StorySceneActorContext actor) =>
        IsRespondMode(mode)
            ? $"Preparing a {DescribeMode(mode).ToLowerInvariant()} message from another present character."
            : $"Preparing a {DescribeMode(mode).ToLowerInvariant()} message as {actor.Name}.";

    private static string BuildPlannerSummary(StoryMessagePlannerResult planner) =>
        $"{StorySceneSharedPromptBuilder.FormatTurnShape(planner.TurnShape)} turn, {planner.Beat}: {planner.ImmediateGoal}";

    private static string BuildThreadSeedText(StoryScenePostMode mode, string? guidancePrompt, StorySceneActorContext actor) =>
        !string.IsNullOrWhiteSpace(guidancePrompt)
            ? guidancePrompt
            : IsRespondMode(mode)
                ? "Scene response"
                : mode == StoryScenePostMode.AutomaticAi
                ? "Automatic scene message"
                : $"Scene message as {actor.Name}";

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
        IReadOnlySet<Guid> deletedIds,
        IReadOnlyList<DbChatMessage> allMessages,
        IReadOnlyList<DbChatMessage> survivingMessages)
    {
        if (survivingMessages.Count == 0)
            return null;

        if (currentActiveLeafMessageId.HasValue && !deletedIds.Contains(currentActiveLeafMessageId.Value))
            return currentActiveLeafMessageId.Value;

        var allMessagesById = allMessages.ToDictionary(x => x.Id);
        var survivingMap = survivingMessages.ToDictionary(x => x.Id);
        Guid? currentMessageId = currentActiveLeafMessageId;
        while (currentMessageId.HasValue && allMessagesById.TryGetValue(currentMessageId.Value, out var currentMessage))
        {
            if (survivingMap.ContainsKey(currentMessage.Id))
                return currentMessage.Id;

            currentMessageId = currentMessage.ParentMessageId;
        }

        return FindLatestLeaf(survivingMessages);
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

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildInitialStepArtifacts(StoryScenePostMode mode)
    {
        var stepArtifacts = new List<StoryMessageProcessStepArtifact>
        {
            new(
                ProcessStepKeys.Appearance,
                "Appearance",
                [new StoryMessageProcessTextBlock("Starting Point", $"Preparing to resolve current appearance before planning a {DescribeMode(mode)} message.")],
                [])
        };

        if (IsRespondMode(mode))
        {
            stepArtifacts.Add(new(
                ProcessStepKeys.Responder,
                "Responder",
                [],
                []));
        }

        stepArtifacts.Add(new(
            ProcessStepKeys.Planning,
            "Planning",
            [],
            []));
        stepArtifacts.Add(new(
            ProcessStepKeys.Writing,
            "Writing",
            [],
            []));
        return stepArtifacts;
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildPlanningCompletedStepArtifacts(
        StoryScenePostMode mode,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult? responderSelection,
        StoryMessagePlannerResult planner)
    {
        var proseRequest = new StoryMessageProseRequest(mode, guidancePrompt, requestedTurnShape, context, planner);
        var stepArtifacts = new List<StoryMessageProcessStepArtifact>
        {
            new(
                ProcessStepKeys.Appearance,
                "Appearance",
                BuildAppearanceInputBlocks(context.StoryContext, context.Characters, appearance),
                [new StoryMessageProcessTextBlock("Resolved Appearance", StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance))])
        };

        if (responderSelection is not null)
        {
            var activeSpeaker = BuildResponderActiveSpeakerContext(context, responderSelection);
            var sharedContext = CreateSharedContext(context);
            var candidates = GetResponderCandidates(sharedContext.Characters, activeSpeaker.CharacterId);
            stepArtifacts.Add(new(
                ProcessStepKeys.Responder,
                "Responder",
                BuildResponderInputBlocks(activeSpeaker, candidates, sharedContext, appearance, guidancePrompt),
                BuildResponderOutputBlocks(responderSelection)));
        }

        stepArtifacts.Add(new(
            ProcessStepKeys.Planning,
            "Planning",
            BuildPromptBlocks(
                StoryScenePlanningPromptBuilder.BuildSystemPrompt(),
                StoryScenePlanningPromptBuilder.BuildUserPrompt(
                    new PostStorySceneMessage(Guid.Empty, context.Actor.CharacterId, mode, null, guidancePrompt, RequestedTurnShape: requestedTurnShape),
                    context)),
            BuildPlanningOutputBlocks(planner)));
        stepArtifacts.Add(new(
            ProcessStepKeys.Writing,
            "Writing",
            BuildWritingInputBlocks(proseRequest),
            []));
        return stepArtifacts;
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildAppearanceCompletedStepArtifacts(
        StoryScenePostMode mode,
        StoryTurnShape? requestedTurnShape,
        StorySceneSharedContext context,
        StorySceneAppearanceResolution appearance)
    {
        var stepArtifacts = new List<StoryMessageProcessStepArtifact>
        {
            new(
                ProcessStepKeys.Appearance,
                "Appearance",
                BuildAppearanceInputBlocks(context.StoryContext, context.Characters, appearance),
                [new StoryMessageProcessTextBlock("Resolved Appearance", StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance))])
        };

        if (IsRespondMode(mode))
        {
            stepArtifacts.Add(new(
                ProcessStepKeys.Responder,
                "Responder",
                [],
                []));
        }

        stepArtifacts.Add(new(
            ProcessStepKeys.Planning,
            "Planning",
            [],
            []));
        stepArtifacts.Add(new(
            ProcessStepKeys.Writing,
            "Writing",
            [],
            []));
        return stepArtifacts;
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildResponderCompletedStepArtifacts(
        StoryScenePostMode mode,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult responderSelection)
    {
        var activeSpeaker = BuildResponderActiveSpeakerContext(context, responderSelection);
        var sharedContext = CreateSharedContext(context);
        var candidates = GetResponderCandidates(sharedContext.Characters, activeSpeaker.CharacterId);

        return
        [
            new(
                ProcessStepKeys.Appearance,
                "Appearance",
                BuildAppearanceInputBlocks(context.StoryContext, context.Characters, appearance),
                [new StoryMessageProcessTextBlock("Resolved Appearance", StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance))]),
            new(
                ProcessStepKeys.Responder,
                "Responder",
                BuildResponderInputBlocks(activeSpeaker, candidates, sharedContext, appearance, guidancePrompt),
                BuildResponderOutputBlocks(responderSelection)),
            new(
                ProcessStepKeys.Planning,
                "Planning",
                [],
                []),
            new(
                ProcessStepKeys.Writing,
                "Writing",
                [],
                [])
        ];
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildCompletedStepArtifacts(
        StoryMessageProseRequest proseRequest,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult? responderSelection,
        string finalMessage,
        string messageTitle = "Final Message")
    {
        var stepArtifacts = new List<StoryMessageProcessStepArtifact>
        {
            new(
                ProcessStepKeys.Appearance,
                "Appearance",
                BuildAppearanceInputBlocks(proseRequest.Context.StoryContext, proseRequest.Context.Characters, appearance),
                [new StoryMessageProcessTextBlock("Resolved Appearance", StorySceneSharedPromptBuilder.BuildAppearanceDetail(appearance))])
        };

        if (responderSelection is not null)
        {
            var activeSpeaker = BuildResponderActiveSpeakerContext(proseRequest.Context, responderSelection);
            var sharedContext = CreateSharedContext(proseRequest.Context);
            var candidates = GetResponderCandidates(sharedContext.Characters, activeSpeaker.CharacterId);
            stepArtifacts.Add(new(
                ProcessStepKeys.Responder,
                "Responder",
                BuildResponderInputBlocks(activeSpeaker, candidates, sharedContext, appearance, proseRequest.GuidancePrompt),
                BuildResponderOutputBlocks(responderSelection)));
        }

        stepArtifacts.Add(new(
            ProcessStepKeys.Planning,
            "Planning",
            BuildPromptBlocks(
                StoryScenePlanningPromptBuilder.BuildSystemPrompt(),
                StoryScenePlanningPromptBuilder.BuildUserPrompt(
                    new PostStorySceneMessage(Guid.Empty, proseRequest.Context.Actor.CharacterId, proseRequest.Mode, null, proseRequest.GuidancePrompt, RequestedTurnShape: proseRequest.RequestedTurnShape),
                    proseRequest.Context)),
            BuildPlanningOutputBlocks(proseRequest.Planner)));
        stepArtifacts.Add(new(
            ProcessStepKeys.Writing,
            "Writing",
            BuildWritingInputBlocks(proseRequest),
            [new StoryMessageProcessTextBlock(messageTitle, finalMessage)]));
        return stepArtifacts;
    }

    private static IReadOnlyList<StoryMessageProcessStepArtifact> BuildStepArtifactsForSavedPlannerEdit(
        StoryScenePostMode mode,
        string? guidancePrompt,
        StoryTurnShape? requestedTurnShape,
        StorySceneGenerationContext context,
        StorySceneAppearanceResolution appearance,
        StorySceneResponderSelectionResult? responderSelection,
        StoryMessagePlannerResult planner,
        StoryMessageProseRequest? proseRequest,
        string? finalMessage)
    {
        var effectiveProseRequest = proseRequest ?? new StoryMessageProseRequest(mode, guidancePrompt, requestedTurnShape, context, planner);

        if (!string.IsNullOrWhiteSpace(finalMessage))
            return BuildCompletedStepArtifacts(effectiveProseRequest, appearance, responderSelection, finalMessage);

        return BuildPlanningCompletedStepArtifacts(mode, guidancePrompt, effectiveProseRequest.RequestedTurnShape, context, appearance, responderSelection, planner);
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildPlanningOutputBlocks(StoryMessagePlannerResult planner) =>
        [new StoryMessageProcessTextBlock("Planning Outcome", StorySceneSharedPromptBuilder.BuildPlannerDetail(planner))];

    private static IReadOnlyList<StoryMessageProcessStepArtifact> UpdatePlanningArtifactsPrivateIntent(
        IReadOnlyList<StoryMessageProcessStepArtifact> artifacts,
        StoryMessagePlannerResult? planner) =>
        planner is null
            ? artifacts
            : artifacts
                .Select(artifact => artifact.StepKey == ProcessStepKeys.Planning
                    ? artifact with { Outputs = BuildPlanningOutputBlocks(planner) }
                    : artifact)
                .ToList();

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildAppearanceInputBlocks(
        StoryNarrativeSettingsView storyContext,
        IReadOnlyList<StorySceneCharacterContext> characters,
        StorySceneAppearanceResolution appearance)
    {
        var promptCharacters = characters
            .Where(x => x.IsPresentInScene)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StorySceneAppearancePromptCharacter(x.Name, x.CurrentAppearance))
            .ToList();

        return
        [
            ..BuildPromptBlocks(
                StorySceneAppearancePromptBuilder.BuildSystemPrompt(),
                StorySceneAppearancePromptBuilder.BuildUserPrompt(
                    promptCharacters,
                    appearance.TranscriptSinceLatestEntry,
                    storyContext.ExplicitContent,
                    storyContext.ViolentContent)),
            new StoryMessageProcessTextBlock("Appearance Context", StorySceneSharedPromptBuilder.BuildAppearanceContextSummary(storyContext, characters, appearance))
        ];
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildResponderInputBlocks(
        StorySceneActorContext activeSpeaker,
        IReadOnlyList<StorySceneCharacterContext> candidates,
        StorySceneSharedContext context,
        StorySceneAppearanceResolution appearance,
        string? guidancePrompt)
    {
        if (candidates.Count == 1)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"**Active speaker:** {activeSpeaker.Name}");
            builder.AppendLine($"**Eligible responder:** {candidates[0].Name}");
            if (!string.IsNullOrWhiteSpace(guidancePrompt))
                builder.AppendLine($"**Guidance:** {guidancePrompt.Trim()}");
            builder.AppendLine("Only one other present character was eligible, so no responder model call was needed.");
            return [new StoryMessageProcessTextBlock("Responder Context", builder.ToString().TrimEnd())];
        }

        return BuildPromptBlocks(
            StorySceneResponderSelectionPromptBuilder.BuildSystemPrompt(),
            StorySceneResponderSelectionPromptBuilder.BuildUserPrompt(
                activeSpeaker,
                candidates,
                context.StoryContext,
                context.CurrentLocation,
                context.TranscriptSinceSnapshot,
                appearance,
                guidancePrompt));
    }

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildResponderOutputBlocks(StorySceneResponderSelectionResult responderSelection) =>
        [new StoryMessageProcessTextBlock("Responder Selection", BuildResponderDetail(responderSelection))];

    private static IReadOnlyList<StoryMessageProcessTextBlock> BuildWritingInputBlocks(StoryMessageProseRequest proseRequest) =>
    [
        new StoryMessageProcessTextBlock("Planning Summary", BuildPlannerSummary(proseRequest.Planner)),
        new StoryMessageProcessTextBlock("Planning Full Details", StorySceneSharedPromptBuilder.BuildPlannerDetail(proseRequest.Planner)),
        ..BuildPromptBlocks(StorySceneProsePromptBuilder.BuildSystemPrompt(proseRequest), StorySceneProsePromptBuilder.BuildUserPrompt(proseRequest))
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

        var stepKey = GetProcessStepKey(step.Title);
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

    private static StoryNarrativeSettingsView MapNarrativeSettings(ChatStoryContextDocument document) => new(
        document.Genre,
        document.Setting,
        document.Tone,
        document.StoryDirection,
        document.ExplicitContent,
        document.ViolentContent);

    private static string TruncateProcessDetail(string detail) =>
        detail.Length <= 4000 ? detail : $"{detail[..3997].TrimEnd()}...";

    private static IReadOnlyList<string> NormalizeItems(IReadOnlyList<string>? values) =>
        values?
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    private static string? NormalizeOptionalText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string RequireValue(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        throw new InvalidOperationException($"Planning the scene message failed because the model returned an empty {fieldName}.");
    }

    private static StoryMessagePlannerResult NormalizeEditablePlanner(StoryMessagePlannerResult planner) => new(
        planner.TurnShape,
        RequireEditablePlannerValue(planner.Beat, "beat"),
        RequireEditablePlannerValue(planner.Intent, "intent"),
        RequireEditablePlannerValue(planner.ImmediateGoal, "immediate goal"),
        RequireEditablePlannerValue(planner.WhyNow, "why now"),
        RequireEditablePlannerValue(planner.ChangeIntroduced, "change introduced"),
        NormalizeOptionalText(planner.PrivateIntent) ?? string.Empty,
        NormalizeItems(planner.NarrativeGuardrails),
        NormalizeItems(planner.ContentGuardrails));

    private static string RequireEditablePlannerValue(string? value, string fieldName)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        throw new InvalidOperationException($"Updating the saved plan failed because the planner {fieldName} was empty.");
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

        return BuildStageOptions(
            generationSettings.Planning,
            [AIFunctionFactory.Create(GetContextDetails)]);
    }

    private static ChatOptions BuildStageOptions(
        StoryModelStageSettingsView settings,
        IList<AITool>? tools = null)
    {
        var options = new ChatOptions
        {
            Temperature = (float)settings.Temperature
        };

        if (settings.TopP.HasValue)
            options.TopP = (float)settings.TopP.Value;

        if (settings.MaxOutputTokens.HasValue)
            options.MaxOutputTokens = settings.MaxOutputTokens.Value;

        if (settings.Seed.HasValue)
            options.Seed = settings.Seed.Value;

        if (settings.FrequencyPenalty.HasValue)
            options.FrequencyPenalty = (float)settings.FrequencyPenalty.Value;

        if (settings.PresencePenalty.HasValue)
            options.PresencePenalty = (float)settings.PresencePenalty.Value;

        if (settings.StopSequences.Count > 0)
            options.StopSequences = settings.StopSequences.ToList();

        if (tools is not null)
            options.Tools = tools;

        return options;
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
        StorySceneSharedContext Context,
        StorySceneAppearanceResolution Appearance,
        StoryMessageTokenUsage? AppearanceTokenUsage,
        bool AppearanceStageSkipped);

    private sealed record PlannerStageExecutionResult(
        StoryMessagePlannerResult Planner,
        StoryMessageTokenUsage? TokenUsage);

    private sealed record ProseStageExecutionResult(
        string FinalMessage,
        StoryMessageTokenUsage? TokenUsage);

    private sealed record StorySceneSharedContext(
        IReadOnlyList<StoryCharacterDocument> CharacterDocuments,
        StorySceneLocationContext? CurrentLocation,
        IReadOnlyList<StorySceneCharacterContext> Characters,
        IReadOnlyList<StorySceneObjectContext> SceneObjects,
        StoryNarrativeSettingsView StoryContext,
        string HistorySummary,
        StoryChatSnapshotSummaryView? LatestSnapshot,
        IReadOnlyList<StorySceneTranscriptMessage> TranscriptSinceSnapshot,
        IReadOnlyList<StorySceneTranscriptMessage>? PrivateIntentTranscript);

    private sealed class PlannerStageResponse
    {
        public string TurnShape { get; set; } = string.Empty;

        public string Beat { get; set; } = string.Empty;

        public string Intent { get; set; } = string.Empty;

        public string ImmediateGoal { get; set; } = string.Empty;

        public string WhyNow { get; set; } = string.Empty;

        public string ChangeIntroduced { get; set; } = string.Empty;

        public string PrivateIntent { get; set; } = string.Empty;

        public IReadOnlyList<string>? NarrativeGuardrails { get; set; }

        public IReadOnlyList<string>? ContentGuardrails { get; set; }
    }

    private sealed class ResponderStageResponse
    {
        public string CharacterName { get; set; } = string.Empty;

        public string WhyThisCharacter { get; set; } = string.Empty;
    }

    private sealed record PrimaryImageData(string ImageUrl, StoryImageAvatarCropView Crop);

    private static StoryTurnShape NormalizeTurnShape(string? value)
    {
        var normalized = value?.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized?.ToLowerInvariant() switch
        {
            "compact" => StoryTurnShape.Compact,
            "brief" => StoryTurnShape.Brief,
            "extended" => StoryTurnShape.Extended,
            "monologue" => StoryTurnShape.Monologue,
            "silent" => StoryTurnShape.Silent,
            "silentmonologue" => StoryTurnShape.SilentMonologue,
            _ => throw new InvalidOperationException("Planning the scene message failed because the planner returned an invalid turn shape.")
        };
    }

}
