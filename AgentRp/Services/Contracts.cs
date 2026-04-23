using AgentRp.Data;

namespace AgentRp.Services;

public sealed record ActivityNotification(
    string Stream,
    string Kind,
    string? Text,
    Guid? EntityId,
    DateTime OccurredUtc);

public static class ActivityStreams
{
    public const string SidebarChats = "sidebar-chats";
    public const string SidebarStory = "sidebar-story";
    public const string StoryChatWorkspace = "story-chat-workspace";
}

public sealed record ChatThreadSummary(
    Guid Id,
    string Title,
    bool IsStarred,
    DateTime UpdatedUtc,
    int MessageCount);

public sealed record ChatBranchOption(
    Guid TargetUserMessageId,
    string PromptPreview,
    bool IsActive);

public sealed record ChatTurnUserMessageView(
    Guid MessageId,
    string Content,
    DateTime CreatedUtc,
    Guid? ParentMessageId);

public sealed record ChatTurnAssistantMessageView(
    Guid MessageId,
    string Content,
    DateTime CreatedUtc);

public sealed record ProcessStepView(
    Guid StepId,
    string Title,
    string Summary,
    string Detail,
    string IconCssClass,
    ProcessStepStatus Status,
    bool IsActive);

public sealed record ProcessRunView(
    Guid RunId,
    string Summary,
    ProcessRunStatus Status,
    string DisplayStatusText,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<ProcessStepView> Steps);

public sealed record ChatConversationTurnView(
    int Sequence,
    ChatTurnUserMessageView UserMessage,
    IReadOnlyList<ChatBranchOption> BranchOptions,
    ProcessRunView? Process,
    ChatTurnAssistantMessageView? AssistantMessage);

public sealed record ChatWorkspaceState(
    Guid ThreadId,
    string Title,
    Guid? ActiveLeafUserMessageId,
    IReadOnlyList<ChatConversationTurnView> Turns);

public sealed record ChatPromptSubmission(
    Guid ThreadId,
    string Prompt,
    Guid? EditedFromUserMessageId);

public sealed record ChatMessageUpdate(
    Guid ThreadId,
    Guid MessageId,
    string Content);

public sealed record ChatStorySidebarView(
    Guid ThreadId,
    string? CurrentLocationName,
    string SelectedSpeakerName,
    IReadOnlyList<StorySidebarSpeakerView> Speakers,
    IReadOnlyList<StoryCharacterListItemView> Characters,
    IReadOnlyList<StoryItemListItemView> SceneItems,
    int FactCount,
    int TimelineEntryCount,
    string? SelectedAgentName,
    IReadOnlyList<AgentProviderOptionView> AvailableAgents,
    bool IsAiAvailable);

public sealed record StorySidebarSpeakerView(
    Guid? CharacterId,
    string Name,
    string Summary,
    bool IsNarrator,
    bool IsPresentInScene,
    bool IsSelected);

public sealed record StoryCharacterListItemView(
    Guid CharacterId,
    string Name,
    string Summary,
    bool IsPresentInScene,
    bool IsSelectedSpeaker);

public sealed record StoryItemListItemView(
    Guid ItemId,
    string Name,
    string Summary);

public sealed record StoryLocationListItemView(
    Guid LocationId,
    string Name,
    string Summary,
    bool IsCurrent);

public sealed record StoryCharacterEditorView(
    Guid CharacterId,
    string Name,
    string Summary,
    string GeneralAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    string PrivateMotivations,
    bool IsPresentInScene);

public sealed record StoryLocationEditorView(
    Guid LocationId,
    string Name,
    string Summary,
    string Details,
    bool IsCurrent);

public sealed record StoryItemEditorView(
    Guid ItemId,
    string Name,
    string Summary,
    string Details,
    Guid? OwnerCharacterId,
    Guid? LocationId,
    bool IsPresentInScene);

public sealed record StoryHistoryFactView(
    Guid FactId,
    int SortOrder,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryTimelineEntryView(
    Guid TimelineEntryId,
    int SortOrder,
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryHistoryView(
    IReadOnlyList<StoryHistoryFactView> Facts,
    IReadOnlyList<StoryTimelineEntryView> TimelineEntries);

public sealed record UpsertCharacter(
    Guid ThreadId,
    Guid? CharacterId,
    string Name,
    string Summary,
    string GeneralAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    string PrivateMotivations,
    bool IsPresentInScene,
    bool IsArchived);

public sealed record StorySceneCharacterAppearanceView(
    Guid CharacterId,
    string CharacterName,
    string CurrentAppearance);

public sealed record StorySceneAppearanceEntryView(
    Guid AppearanceEntryId,
    Guid CoveredThroughMessageId,
    DateTime CreatedUtc,
    string Summary,
    bool CanEdit,
    IReadOnlyList<StorySceneCharacterAppearanceView> Characters);

public sealed record UpdateStorySceneAppearanceEntry(
    Guid ThreadId,
    Guid AppearanceEntryId,
    IReadOnlyList<StorySceneCharacterAppearanceView> Characters);

public sealed record DeleteCharacter(
    Guid ThreadId,
    Guid CharacterId);

public sealed record UpsertLocation(
    Guid ThreadId,
    Guid? LocationId,
    string Name,
    string Summary,
    string Details,
    bool IsArchived);

public sealed record DeleteLocation(
    Guid ThreadId,
    Guid LocationId);

public sealed record SetCurrentLocation(
    Guid ThreadId,
    Guid? LocationId);

public sealed record UpsertItem(
    Guid ThreadId,
    Guid? ItemId,
    string Name,
    string Summary,
    string Details,
    Guid? OwnerCharacterId,
    Guid? LocationId,
    bool IsPresentInScene,
    bool IsArchived);

public sealed record DeleteItem(
    Guid ThreadId,
    Guid ItemId);

public sealed record UpsertHistoryFact(
    Guid ThreadId,
    Guid? FactId,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record DeleteHistoryFact(
    Guid ThreadId,
    Guid FactId);

public sealed record UpsertTimelineEntry(
    Guid ThreadId,
    Guid? TimelineEntryId,
    int? SortOrder,
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record DeleteTimelineEntry(
    Guid ThreadId,
    Guid TimelineEntryId);

public sealed record UpdateScenePresence(
    Guid ThreadId,
    IReadOnlyList<Guid> PresentCharacterIds,
    IReadOnlyList<Guid> PresentItemIds);

public sealed record ToastMessage(
    Guid Id,
    string Title,
    string Message,
    ToastIntent Intent,
    DateTime CreatedUtc);

public enum ToastIntent
{
    Success,
    Error,
    Info
}

public interface IActivityNotifier
{
    IDisposable Subscribe(string stream, Action<ActivityNotification> handler);

    void Publish(ActivityNotification notification);
}

public interface IUserFeedbackService
{
    event Action? Changed;

    IReadOnlyList<ToastMessage> Messages { get; }

    void ShowBackgroundSuccess(string message, string title);

    void ShowBackgroundError(string message, string title);

    void ShowBackgroundInfo(string message, string title);

    void Dismiss(Guid id);
}

public interface IMarkdownRenderer
{
    string Render(string markdown);
}

public interface IAgentTurnComposer
{
    Task<ComposedAgentTurn> ComposeAsync(string prompt, bool isBranch, CancellationToken cancellationToken);
}

public sealed record ComposedAgentTurn(
    string AssistantMarkdown,
    string RunSummary,
    IReadOnlyList<ComposedProcessStep> Steps);

public sealed record ComposedProcessStep(
    string Title,
    string Summary,
    string Detail,
    string IconCssClass,
    ProcessStepStatus Status);

public interface IChatWorkspaceService
{
    Task<Guid> CreateThreadAsync(CancellationToken cancellationToken);

    Task<Guid> ResolveWorkspaceThreadAsync(Guid? requestedThreadId, CancellationToken cancellationToken);

    Task<ChatWorkspaceState?> GetWorkspaceAsync(Guid threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatThreadSummary>> GetRecentThreadsAsync(int take, CancellationToken cancellationToken);

    Task RenameThreadAsync(Guid threadId, string title, CancellationToken cancellationToken);

    Task SetThreadStarredAsync(Guid threadId, bool isStarred, CancellationToken cancellationToken);

    Task SendMessageAsync(ChatPromptSubmission submission, CancellationToken cancellationToken);

    Task UpdateMessageAsync(ChatMessageUpdate update, CancellationToken cancellationToken);

    Task SetActiveLeafAsync(Guid threadId, Guid userMessageId, CancellationToken cancellationToken);
}

public sealed record StorySceneSpeakerView(
    Guid? CharacterId,
    string Name,
    string Summary,
    bool IsNarrator,
    bool IsPresentInScene,
    bool IsSelected);

public sealed record StoryChatSnapshotSummaryView(
    Guid SnapshotId,
    string Summary,
    DateTime CreatedUtc,
    Guid SelectedLeafMessageId,
    Guid CoveredThroughMessageId,
    DateTime CoveredThroughUtc,
    int IncludedMessageCount,
    int FactCount,
    int TimelineEntryCount);

public sealed record StorySceneSnapshotFactView(
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<string> CharacterNames,
    IReadOnlyList<string> LocationNames,
    IReadOnlyList<string> ItemNames);

public sealed record StorySceneSnapshotTimelineEntryView(
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<string> CharacterNames,
    IReadOnlyList<string> LocationNames,
    IReadOnlyList<string> ItemNames);

public sealed record StorySceneSnapshotView(
    Guid SnapshotId,
    Guid CoveredThroughMessageId,
    DateTime CreatedUtc,
    string Summary,
    int IncludedMessageCount,
    int FactCount,
    int TimelineEntryCount,
    IReadOnlyList<StorySceneSnapshotFactView> Facts,
    IReadOnlyList<StorySceneSnapshotTimelineEntryView> TimelineEntries);

public sealed record StoryChatSnapshotReferenceView(
    Guid Id,
    string Name);

public sealed record StoryChatSnapshotMessageSelectionView(
    Guid MessageId,
    string SpeakerName,
    DateTime CreatedUtc,
    string Content,
    bool IsIncluded);

public sealed record StoryChatSnapshotFactDraftView(
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryChatSnapshotTimelineDraftView(
    string? WhenText,
    string Title,
    string Summary,
    string Details,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<Guid> ItemIds);

public sealed record StoryChatSnapshotDraftView(
    Guid ThreadId,
    Guid ThroughMessageId,
    StoryChatSnapshotSummaryView? LatestSnapshot,
    IReadOnlyList<StoryChatSnapshotMessageSelectionView> Messages,
    string NarrativeSummary,
    IReadOnlyList<StoryChatSnapshotFactDraftView> Facts,
    IReadOnlyList<StoryChatSnapshotTimelineDraftView> TimelineEntries,
    IReadOnlyList<StoryChatSnapshotReferenceView> Characters,
    IReadOnlyList<StoryChatSnapshotReferenceView> Locations,
    IReadOnlyList<StoryChatSnapshotReferenceView> Items);

public sealed record CreateStoryChatSnapshotDraft(
    Guid ThreadId,
    Guid ThroughMessageId);

public sealed record CommitStoryChatSnapshotDraft(
    Guid ThreadId,
    Guid ThroughMessageId,
    string NarrativeSummary,
    IReadOnlyList<StoryChatSnapshotFactDraftView> Facts,
    IReadOnlyList<StoryChatSnapshotTimelineDraftView> TimelineEntries);

public sealed record StorySceneProcessStepView(
    Guid StepId,
    string Title,
    string Summary,
    string Detail,
    string IconCssClass,
    ProcessStepStatus Status,
    bool IsActive,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    StoryMessageProcessStepArtifact? Artifact);

public sealed record StorySceneMessageProcessView(
    Guid RunId,
    string Summary,
    ProcessRunStatus Status,
    string DisplayStatusText,
    string? ActiveStage,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<StorySceneProcessStepView> Steps,
    StoryMessageProcessContext? Context);

public sealed record StorySceneBranchOptionView(
    Guid BranchMessageId,
    Guid TargetLeafMessageId,
    string Preview,
    string SpeakerName,
    DateTime CreatedUtc,
    bool IsSelected);

public sealed record StorySceneBranchNavigatorView(
    Guid? ParentMessageId,
    IReadOnlyList<StorySceneBranchOptionView> Options,
    int SelectedOptionNumber);

public enum StorySceneDeleteMode
{
    SingleMessage,
    Branch
}

public sealed record DeleteStorySceneMessage(
    Guid ThreadId,
    Guid MessageId,
    StorySceneDeleteMode Mode);

public sealed record StorySceneDeleteCapabilitiesView(
    int DirectChildCount,
    int DescendantCount,
    bool CanDeleteSingleMessage,
    bool CanDeleteBranch);

public sealed record StorySceneMessageView(
    Guid MessageId,
    ChatMessageKind MessageKind,
    StoryScenePostMode GenerationMode,
    string Content,
    DateTime CreatedUtc,
    Guid? SpeakerCharacterId,
    string CanonicalSpeakerName,
    string DisplaySpeakerName,
    bool IsNarrator,
    bool IsSelectedSpeaker,
    bool CanSaveInPlace,
    bool IsSnapshotCandidate,
    StorySceneBranchNavigatorView? BranchNavigator,
    StorySceneDeleteCapabilitiesView DeleteCapabilities,
    StorySceneMessageProcessView? Process);

public sealed record StorySceneTranscriptNodeView(
    int Sequence,
    StorySceneMessageView? Message,
    StorySceneSnapshotView? Snapshot,
    StorySceneAppearanceEntryView? Appearance);

public sealed record StorySceneChatState(
    Guid ThreadId,
    string Title,
    string? CurrentLocationName,
    Guid? SelectedLeafMessageId,
    StorySceneSpeakerView SelectedSpeaker,
    IReadOnlyList<StorySceneSpeakerView> AvailableSpeakers,
    IReadOnlyList<StorySceneTranscriptNodeView> Transcript,
    string? SelectedAgentName,
    bool IsAiAvailable);

public sealed record PostStorySceneMessage(
    Guid ThreadId,
    Guid? SpeakerCharacterId,
    StoryScenePostMode Mode,
    string? ManualText,
    string? GuidancePrompt,
    Guid? RetrySourceMessageId = null);

public sealed record BranchStorySceneMessage(
    Guid ThreadId,
    Guid SourceMessageId,
    string Content);

public sealed record RegenerateStorySceneProse(
    Guid ThreadId,
    Guid SourceMessageId);

public sealed record StorySceneTranscriptMessage(
    Guid MessageId,
    DateTime CreatedUtc,
    string SpeakerName,
    bool IsNarrator,
    string Content);

public sealed record StorySceneActorContext(
    Guid? CharacterId,
    string Name,
    bool IsNarrator,
    string Summary,
    string GeneralAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    string PrivateMotivations,
    string NarratorGuidance,
    string HiddenKnowledge);

public sealed record StorySceneLocationContext(
    Guid? LocationId,
    string Name,
    string Summary,
    string Details);

public sealed record StorySceneObjectContext(
    Guid ItemId,
    string Name,
    string Summary,
    string Details);

public sealed record StorySceneCharacterContext(
    Guid CharacterId,
    string Name,
    string Summary,
    string GeneralAppearance,
    string CurrentAppearance,
    string CorePersonality,
    string Relationships,
    string PreferencesBeliefs,
    bool IsPresentInScene);

public sealed record StorySceneAppearanceResolution(
    StorySceneAppearanceEntryView? LatestEntry,
    IReadOnlyList<StorySceneCharacterAppearanceView> EffectiveCharacters,
    IReadOnlyList<StorySceneTranscriptMessage> TranscriptSinceLatestEntry);

public sealed record StorySceneGenerationContext(
    StorySceneActorContext Actor,
    StorySceneLocationContext? CurrentLocation,
    IReadOnlyList<StorySceneCharacterContext> Characters,
    IReadOnlyList<StorySceneObjectContext> SceneObjects,
    string HistorySummary,
    StoryChatSnapshotSummaryView? LatestSnapshot,
    IReadOnlyList<StorySceneTranscriptMessage> TranscriptSinceSnapshot);

public sealed record StoryMessagePlannerResult(
    string Intent,
    string ImmediateGoal,
    string EmotionalStance,
    IReadOnlyList<string> TargetAddressees,
    IReadOnlyList<string> RequiredFactualBeats,
    IReadOnlyList<string> Guardrails,
    string PlanningSummary);

public sealed record StoryMessageProseRequest(
    StoryScenePostMode Mode,
    string? GuidancePrompt,
    StorySceneGenerationContext Context,
    StoryMessagePlannerResult Planner);

public sealed record StoryMessageProcessContext(
    StoryScenePostMode Mode,
    string? GuidancePrompt,
    StorySceneGenerationContext? GenerationContext,
    StorySceneAppearanceResolution? Appearance,
    StoryMessagePlannerResult? Planner,
    StoryMessageProseRequest? ProseRequest,
    string? FinalMessage,
    IReadOnlyList<StoryMessageProcessStepArtifact> StepArtifacts);

public sealed record StoryMessageProcessStepArtifact(
    string StepKey,
    string StepTitle,
    IReadOnlyList<StoryMessageProcessTextBlock> Inputs,
    IReadOnlyList<StoryMessageProcessTextBlock> Outputs);

public sealed record StoryMessageProcessTextBlock(
    string Title,
    string Content);

public sealed record StorySceneMessageStreamUpdate(
    Guid ThreadId,
    Guid MessageId,
    string Content,
    bool IsFinal);

public delegate Task StorySceneMessageStreamHandler(
    StorySceneMessageStreamUpdate update,
    CancellationToken cancellationToken);

public interface IStorySceneChatService
{
    Task<StorySceneChatState?> GetChatStateAsync(Guid threadId, CancellationToken cancellationToken);

    Task SelectSpeakerAsync(Guid threadId, Guid? speakerCharacterId, CancellationToken cancellationToken);

    Task PostMessageAsync(
        PostStorySceneMessage request,
        StorySceneMessageStreamHandler? streamHandler,
        CancellationToken cancellationToken);

    Task UpdateMessageAsync(ChatMessageUpdate request, CancellationToken cancellationToken);

    Task CreateBranchAsync(BranchStorySceneMessage request, CancellationToken cancellationToken);

    Task RegenerateProseAsync(
        RegenerateStorySceneProse request,
        StorySceneMessageStreamHandler? streamHandler,
        CancellationToken cancellationToken);

    Task SelectBranchAsync(Guid threadId, Guid leafMessageId, CancellationToken cancellationToken);

    Task DeleteMessageAsync(DeleteStorySceneMessage request, CancellationToken cancellationToken);

    Task UpdateAppearanceEntryAsync(UpdateStorySceneAppearanceEntry request, CancellationToken cancellationToken);
}

public interface IStoryChatSnapshotService
{
    Task<StoryChatSnapshotSummaryView?> GetLatestSnapshotAsync(Guid threadId, Guid selectedLeafMessageId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StorySceneSnapshotView>> GetSnapshotsForPathAsync(Guid threadId, Guid selectedLeafMessageId, CancellationToken cancellationToken);

    Task<StoryChatSnapshotDraftView> CreateDraftAsync(CreateStoryChatSnapshotDraft request, CancellationToken cancellationToken);

    Task<StoryChatSnapshotSummaryView> CommitDraftAsync(CommitStoryChatSnapshotDraft request, CancellationToken cancellationToken);
}

public interface IStoryChatAppearanceService
{
    Task<StorySceneAppearanceResolution> ResolveLatestAppearanceAsync(
        Guid threadId,
        IReadOnlyList<AgentRp.Data.ChatMessage> selectedPath,
        ChatStory story,
        bool writeChanges,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StorySceneAppearanceEntryView>> GetEntriesForPathAsync(
        Guid threadId,
        IReadOnlyList<AgentRp.Data.ChatMessage> selectedPath,
        ChatStory story,
        CancellationToken cancellationToken);

    Task<StorySceneAppearanceEntryView> UpdateLatestEntryAsync(
        UpdateStorySceneAppearanceEntry request,
        CancellationToken cancellationToken);
}

public interface IChatStoryService
{
    Task<ChatStorySidebarView?> GetSidebarAsync(Guid threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryCharacterEditorView>> GetCharactersAsync(Guid threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryLocationListItemView>> GetLocationsAsync(Guid threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryLocationEditorView>> GetLocationEditorsAsync(Guid threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryItemEditorView>> GetItemsAsync(Guid threadId, CancellationToken cancellationToken);

    Task<StoryHistoryView?> GetHistoryAsync(Guid threadId, CancellationToken cancellationToken);

    Task<StoryCharacterEditorView> UpsertCharacterAsync(UpsertCharacter command, CancellationToken cancellationToken);

    Task DeleteCharacterAsync(DeleteCharacter command, CancellationToken cancellationToken);

    Task<StoryLocationEditorView> UpsertLocationAsync(UpsertLocation command, CancellationToken cancellationToken);

    Task DeleteLocationAsync(DeleteLocation command, CancellationToken cancellationToken);

    Task SetCurrentLocationAsync(SetCurrentLocation command, CancellationToken cancellationToken);

    Task<StoryItemEditorView> UpsertItemAsync(UpsertItem command, CancellationToken cancellationToken);

    Task DeleteItemAsync(DeleteItem command, CancellationToken cancellationToken);

    Task<StoryHistoryFactView> UpsertHistoryFactAsync(UpsertHistoryFact command, CancellationToken cancellationToken);

    Task DeleteHistoryFactAsync(DeleteHistoryFact command, CancellationToken cancellationToken);

    Task<StoryTimelineEntryView> UpsertTimelineEntryAsync(UpsertTimelineEntry command, CancellationToken cancellationToken);

    Task DeleteTimelineEntryAsync(DeleteTimelineEntry command, CancellationToken cancellationToken);

    Task UpdateScenePresenceAsync(UpdateScenePresence command, CancellationToken cancellationToken);
}

public interface IStoryFieldGuidanceService
{
    Task<IReadOnlyList<StoryEntityFieldGuidanceView>> GetGuidanceAsync(StoryEntityKind entityKind, CancellationToken cancellationToken);

    Task<StoryEntityFieldGuidanceView> GetGuidanceAsync(StoryEntityKind entityKind, StoryEntityFieldKey fieldKey, CancellationToken cancellationToken);

    Task<StoryEntityFieldGuidanceView> UpdateGuidanceAsync(UpdateStoryEntityFieldGuidance update, CancellationToken cancellationToken);
}
