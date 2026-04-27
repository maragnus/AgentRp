using Microsoft.AspNetCore.Components;
using AgentRp.Data;
using AgentRp.Services;

namespace AgentRp.Components.Shared;

public enum ChatMessageDesign
{
    Classic
}

public sealed record ChatLogContext(
    StorySceneChatState Chat,
    IReadOnlyList<ChatTranscriptItemContext> Items,
    ChatLogFooterContext Footer);

public sealed record ChatTranscriptItemContext(
    object Key,
    ChatProcessTraceContext? Process,
    ChatMessageContext? Message,
    ChatSnapshotContext? Snapshot,
    ChatAppearanceEntryContext? Appearance);

public sealed record ChatMessageContext(
    StorySceneMessageView Message,
    StorySceneMessageProcessView? Process,
    string DisplayContent,
    string CardClass,
    bool IsEditing,
    string EditingDraft,
    bool IsSubmitting,
    bool IsSceneInteractionLocked,
    ChatMessageFooterContext Footer);

public sealed record ChatMessageFooterContext(
    StorySceneMessageView Message,
    IReadOnlyList<StorySceneSpeakerView> AvailableSpeakers,
    bool IsEditing,
    bool IsSubmitting,
    bool IsSceneInteractionLocked,
    Guid? SwitchingBranchLeafId,
    Guid? ReassigningMessageId,
    string? ReassigningAuthorKey,
    Guid? StartingRetryMessageId,
    Guid? RegeneratingProseMessageId,
    Guid? ChangingTurnShapeMessageId,
    StoryTurnShape? ChangingTurnShapeValue,
    bool IsSnapshotDraftLoading,
    bool IsCommittingSnapshot,
    bool CanRetryMessage,
    bool CanRegenerateProse,
    bool CanEditSavedPlan,
    StoryTurnShape? SavedPlanTurnShape,
    bool HasEditingMessage,
    string EditingDraft,
    bool IsSavingInlineEdit,
    bool IsBranchingInlineEdit);

public sealed record ChatLogFooterContext(
    StorySceneChatState Chat,
    string DraftText,
    string? ErrorMessage,
    bool IsRetrying,
    string? RetrySpeakerName,
    bool IsSubmitting,
    bool IsSceneInteractionLocked,
    bool IsAppearanceReplayLoading,
    bool IsUpdatingDisplayPreferences,
    bool ShowProcessPanels,
    bool ShowRespondAsPicker,
    StoryScenePostMode? SubmittingMode,
    string? SubmittingAuthorKey,
    StoryTurnShape? SubmittingTurnShape,
    StorySceneMessageProcessView? ActiveRunningProcess);

public sealed record ChatProcessTraceContext(
    StorySceneMessageProcessView Process,
    bool ShowProcessPanels,
    bool IsTraceExpanded,
    Guid? ExpandedStepId,
    Guid? CopyingProcessStepId,
    Guid? CopiedProcessStepId);

public sealed record ChatSnapshotContext(
    StorySceneSnapshotView Snapshot,
    bool IsExpanded);

public sealed record ChatAppearanceEntryContext(
    StorySceneAppearanceEntryView Appearance,
    bool IsVisible,
    bool IsExpanded,
    bool IsEditing,
    Guid? SavingAppearanceEntryId);

public sealed record StorySceneChatActions
{
    public Func<StoryScenePostMode, StoryTurnShape?, Task> Post { get; init; } = (_, _) => Task.CompletedTask;
    public Func<StorySceneSpeakerView, Task> RespondAs { get; init; } = _ => Task.CompletedTask;
    public Func<Task> RespondAutoselect { get; init; } = () => Task.CompletedTask;
    public Func<string, Task> SetDraftText { get; init; } = _ => Task.CompletedTask;
    public Func<Task> CancelRetry { get; init; } = () => Task.CompletedTask;
    public Func<Task> OpenAppearanceReplay { get; init; } = () => Task.CompletedTask;
    public Func<Task> ToggleProcessPanels { get; init; } = () => Task.CompletedTask;
    public Func<Guid, Task> StopSceneRun { get; init; } = _ => Task.CompletedTask;
    public Func<Guid, bool> IsStoppingRun { get; init; } = _ => false;
    public Action<ElementReference>? ChatLogScrollContainerChanged { get; init; }
    public Action<ElementReference>? ChatLogFooterElementChanged { get; init; }
    public EventCallback<Guid> SelectBranch { get; init; }
    public Action<Guid> ToggleSelectedMessage { get; init; } = _ => { };
    public Func<string, Task> SetEditingDraft { get; init; } = _ => Task.CompletedTask;
    public Func<Task> CancelInlineEdit { get; init; } = () => Task.CompletedTask;
    public Func<Task> SaveInlineEdit { get; init; } = () => Task.CompletedTask;
    public Func<Task> CreateEditedBranch { get; init; } = () => Task.CompletedTask;
    public Func<StorySceneMessageView, StorySceneSpeakerView, Task> ChangeMessageSpeaker { get; init; } = (_, _) => Task.CompletedTask;
    public Func<StorySceneMessageView, Task> BeginRetry { get; init; } = _ => Task.CompletedTask;
    public Action<StorySceneMessageView> OpenPrivateIntent { get; init; } = _ => { };
    public Action<StorySceneMessageView> OpenMessageAppearance { get; init; } = _ => { };
    public Func<StorySceneMessageView, Task> RegenerateProse { get; init; } = _ => Task.CompletedTask;
    public Func<StorySceneMessageView, StoryTurnShape, Task> ChangeTurnShapeAndRegenerate { get; init; } = (_, _) => Task.CompletedTask;
    public Action<StorySceneMessageView> OpenPlan { get; init; } = _ => { };
    public Func<StorySceneMessageView, Task> OpenSnapshotDraft { get; init; } = _ => Task.CompletedTask;
    public Action<StorySceneMessageView> BeginInlineEdit { get; init; } = _ => { };
    public Func<string, Task> CopyMessage { get; init; } = _ => Task.CompletedTask;
    public Action<StorySceneMessageView> OpenDelete { get; init; } = _ => { };
    public Action<Guid> ToggleProcessTrace { get; init; } = _ => { };
    public Action<Guid, Guid> ToggleProcessStep { get; init; } = (_, _) => { };
    public Func<StorySceneProcessStepView, IReadOnlyList<StoryMessageProcessTextBlock>, Task> CopyProcessStep { get; init; } = (_, _) => Task.CompletedTask;
    public Action<Guid> ToggleSnapshot { get; init; } = _ => { };
    public Action<Guid> ToggleAppearanceEntry { get; init; } = _ => { };
    public Action<StorySceneAppearanceEntryView> BeginAppearanceEdit { get; init; } = _ => { };
    public Func<Task> CancelAppearanceEdit { get; init; } = () => Task.CompletedTask;
    public Func<Guid, string> GetAppearanceDraft { get; init; } = _ => string.Empty;
    public Action<Guid, string?> SetAppearanceDraft { get; init; } = (_, _) => { };
    public Func<StorySceneAppearanceEntryView, Task> SaveAppearanceEdit { get; init; } = _ => Task.CompletedTask;
}
