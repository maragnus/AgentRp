using AgentRp.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentRp.Services;

public sealed class ChatWorkspaceService(
    IDbContextFactory<AgentRp.Data.AppContext> dbContextFactory,
    IActivityNotifier activityNotifier,
    IAgentTurnComposer agentTurnComposer,
    IAgentCatalog agentCatalog) : IChatWorkspaceService
{
    public async Task<Guid> CreateThreadAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var thread = new ChatThread
        {
            Id = Guid.NewGuid(),
            Title = "New Chat",
            SelectedAgentName = agentCatalog.GetDefaultAgentName() ?? string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var story = new ChatStory
        {
            ChatThreadId = thread.Id,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        dbContext.ChatThreads.Add(thread);
        dbContext.ChatStories.Add(story);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
        return thread.Id;
    }

    public async Task<Guid> ResolveWorkspaceThreadAsync(Guid? requestedThreadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (requestedThreadId.HasValue)
        {
            var exists = await dbContext.ChatThreads.AnyAsync(x => x.Id == requestedThreadId.Value, cancellationToken);
            if (exists)
                return requestedThreadId.Value;
        }

        var latestThreadId = await dbContext.ChatThreads
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return latestThreadId ?? await CreateThreadAsync(cancellationToken);
    }

    public async Task<ChatWorkspaceState?> GetWorkspaceAsync(Guid threadId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .AsNoTracking()
            .Where(x => x.Id == threadId)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.ActiveLeafMessageId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (thread is null)
            return null;

        var messages = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.CreatedUtc)
            .Select(x => new MessageProjection(
                x.Id,
                x.Role,
                x.Content,
                x.CreatedUtc,
                x.ParentMessageId,
                x.EditedFromMessageId))
            .ToListAsync(cancellationToken);

        var runs = await dbContext.ProcessRuns
            .AsNoTracking()
            .Where(x => x.ThreadId == threadId)
            .OrderBy(x => x.StartedUtc)
            .Select(x => new RunProjection(
                x.Id,
                x.UserMessageId,
                x.AssistantMessageId,
                x.Summary,
                x.Status,
                x.StartedUtc,
                x.CompletedUtc,
                x.Steps
                    .OrderBy(step => step.SortOrder)
                    .Select(step => new StepProjection(
                        step.Id,
                        step.Title,
                        step.Summary,
                        step.Detail,
                        step.IconCssClass,
                        step.Status))
                    .ToList()))
            .ToListAsync(cancellationToken);

        var messageMap = messages.ToDictionary(x => x.Id);
        var path = BuildActivePath(messages, thread.ActiveLeafMessageId);
        var userTurns = path.Where(x => x.Role == ChatRole.User).ToList();
        var runMap = runs.ToDictionary(x => x.UserMessageId);
        var turns = new List<ChatConversationTurnView>(userTurns.Count);

        for (var index = 0; index < userTurns.Count; index++)
        {
            var userMessage = userTurns[index];
            var assistantMessage = path.FirstOrDefault(x => x.ParentMessageId == userMessage.Id && x.Role == ChatRole.Assistant);
            var siblingBranches = messages
                .Where(x => x.Role == ChatRole.User && x.ParentMessageId == userMessage.ParentMessageId)
                .OrderBy(x => x.CreatedUtc)
                .Select(x => new ChatBranchOption(
                    x.Id,
                    BuildPromptPreview(x.Content),
                    x.Id == userMessage.Id))
                .ToList();

            runMap.TryGetValue(userMessage.Id, out var run);
            turns.Add(new ChatConversationTurnView(
                index + 1,
                new ChatTurnUserMessageView(userMessage.Id, userMessage.Content, userMessage.CreatedUtc, userMessage.ParentMessageId),
                siblingBranches,
                run is null ? null : MapRun(run),
                assistantMessage is null ? null : new ChatTurnAssistantMessageView(assistantMessage.Id, assistantMessage.Content, assistantMessage.CreatedUtc)));
        }

        var activeLeafUserMessageId = userTurns.LastOrDefault()?.Id;
        return new ChatWorkspaceState(thread.Id, thread.Title, activeLeafUserMessageId, turns);

        static ProcessRunView MapRun(RunProjection run)
        {
            var steps = run.Steps
                .Select((step, index) => new ProcessStepView(
                    step.Id,
                    step.Title,
                    step.Summary,
                    step.Detail,
                    step.IconCssClass,
                    step.Status,
                    index == run.Steps.Count - 1 && step.Status == ProcessStepStatus.Completed))
                .ToList();

            return new ProcessRunView(
                run.Id,
                run.Summary,
                run.Status,
                run.Status switch
                {
                    ProcessRunStatus.Canceled => "Stopped",
                    ProcessRunStatus.Completed => "Completed",
                    ProcessRunStatus.Failed => "Failed",
                    _ => "Running"
                },
                run.StartedUtc,
                run.CompletedUtc,
                steps);
        }
    }

    public async Task<IReadOnlyList<ChatThreadSummary>> GetRecentThreadsAsync(int take, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ChatThreads
            .AsNoTracking()
            .OrderByDescending(x => x.IsStarred)
            .ThenByDescending(x => x.UpdatedUtc)
            .Take(take)
            .Select(x => new ChatThreadSummary(x.Id, x.Title, x.IsStarred, x.UpdatedUtc, x.Messages.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task RenameThreadAsync(Guid threadId, string title, CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeThreadTitle(title);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await GetThreadForUpdateAsync(dbContext, threadId, cancellationToken);
        if (string.Equals(thread.Title, normalizedTitle, StringComparison.Ordinal))
            return;

        thread.Title = normalizedTitle;
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
        PublishWorkspaceRefresh(thread.Id);
    }

    public async Task SetThreadStarredAsync(Guid threadId, bool isStarred, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await GetThreadForUpdateAsync(dbContext, threadId, cancellationToken);
        if (thread.IsStarred == isStarred)
            return;

        thread.IsStarred = isStarred;
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
    }

    public async Task SendMessageAsync(ChatPromptSubmission submission, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(submission.Prompt))
            return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == submission.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat could not be found.");

        Guid? parentMessageId;
        if (submission.EditedFromUserMessageId.HasValue)
        {
            var sourceUserMessage = thread.Messages.FirstOrDefault(x => x.Id == submission.EditedFromUserMessageId.Value && x.Role == ChatRole.User)
                ?? throw new InvalidOperationException("The edited user message could not be found.");
            parentMessageId = sourceUserMessage.ParentMessageId;
        }
        else
        {
            parentMessageId = thread.ActiveLeafMessageId;
        }

        var now = DateTime.UtcNow;
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            Role = ChatRole.User,
            MessageKind = ChatMessageKind.System,
            Content = submission.Prompt.Trim(),
            CreatedUtc = now,
            GenerationMode = StoryScenePostMode.Manual,
            ParentMessageId = parentMessageId,
            EditedFromMessageId = submission.EditedFromUserMessageId
        };

        var composedTurn = await agentTurnComposer.ComposeAsync(userMessage.Content, submission.EditedFromUserMessageId.HasValue, cancellationToken);
        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            Role = ChatRole.Assistant,
            MessageKind = ChatMessageKind.System,
            Content = composedTurn.AssistantMarkdown,
            CreatedUtc = now.AddSeconds(1),
            GenerationMode = StoryScenePostMode.AutomaticAi,
            ParentMessageId = userMessage.Id
        };

        var processRun = new ProcessRun
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Thread = thread,
            UserMessageId = userMessage.Id,
            AssistantMessageId = assistantMessage.Id,
            TargetMessageId = assistantMessage.Id,
            Summary = composedTurn.RunSummary,
            Status = ProcessRunStatus.Completed,
            StartedUtc = now,
            CompletedUtc = now.AddSeconds(1),
            Steps = composedTurn.Steps
                .Select((step, index) => new ProcessStep
                {
                    Id = Guid.NewGuid(),
                    SortOrder = index,
                    Title = step.Title,
                    Summary = step.Summary,
                    Detail = step.Detail,
                    IconCssClass = step.IconCssClass,
                    Status = step.Status
                })
                .ToList()
        };

        dbContext.ChatMessages.Add(userMessage);
        dbContext.ChatMessages.Add(assistantMessage);
        dbContext.ProcessRuns.Add(processRun);

        thread.ActiveLeafMessageId = assistantMessage.Id;
        thread.UpdatedUtc = assistantMessage.CreatedUtc;
        if (thread.Title == "New Chat")
            thread.Title = BuildThreadTitle(userMessage.Content);

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
    }

    public async Task UpdateMessageAsync(ChatMessageUpdate update, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.Content))
            return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == update.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat could not be found.");

        var message = thread.Messages.FirstOrDefault(x => x.Id == update.MessageId)
            ?? throw new InvalidOperationException("The selected message could not be found.");

        var trimmedContent = update.Content.Trim();
        var previousContent = message.Content;
        message.Content = trimmedContent;
        thread.UpdatedUtc = DateTime.UtcNow;

        if (message.Role == ChatRole.User
            && string.Equals(thread.Title, BuildThreadTitle(previousContent), StringComparison.Ordinal))
        {
            thread.Title = BuildThreadTitle(trimmedContent);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
    }

    public async Task SetActiveLeafAsync(Guid threadId, Guid userMessageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var thread = await dbContext.ChatThreads
            .Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
            ?? throw new InvalidOperationException("The selected chat could not be found.");

        var messageMap = thread.Messages.ToDictionary(x => x.Id);
        if (!messageMap.TryGetValue(userMessageId, out var selectedUserMessage) || selectedUserMessage.Role != ChatRole.User)
            throw new InvalidOperationException("The selected branch could not be found.");

        var activeLeafMessageId = FindBranchLeaf(thread.Messages, userMessageId);
        thread.ActiveLeafMessageId = activeLeafMessageId;
        thread.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishSidebarRefresh(thread.Id);
    }

    private void PublishSidebarRefresh(Guid threadId) =>
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.SidebarChats, "updated", null, threadId, DateTime.UtcNow));

    private void PublishWorkspaceRefresh(Guid threadId) =>
        activityNotifier.Publish(new ActivityNotification(ActivityStreams.StoryChatWorkspace, "updated", null, threadId, DateTime.UtcNow));

    private static async Task<ChatThread> GetThreadForUpdateAsync(
        AgentRp.Data.AppContext dbContext,
        Guid threadId,
        CancellationToken cancellationToken) =>
        await dbContext.ChatThreads.FirstOrDefaultAsync(x => x.Id == threadId, cancellationToken)
        ?? throw new InvalidOperationException("The selected chat could not be found.");

    private static IReadOnlyList<MessageProjection> BuildActivePath(IReadOnlyList<MessageProjection> messages, Guid? activeLeafMessageId)
    {
        if (messages.Count == 0)
            return [];

        var messageMap = messages.ToDictionary(x => x.Id);
        var leafId = activeLeafMessageId;

        if (!leafId.HasValue || !messageMap.ContainsKey(leafId.Value))
            leafId = FindLatestLeaf(messages);

        var path = new List<MessageProjection>();
        var currentId = leafId;

        while (currentId.HasValue && messageMap.TryGetValue(currentId.Value, out var current))
        {
            path.Add(current);
            currentId = current.ParentMessageId;
        }

        path.Reverse();
        return path;
    }

    private static Guid FindLatestLeaf(IReadOnlyList<MessageProjection> messages)
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

    private static Guid FindBranchLeaf(IReadOnlyList<ChatMessage> messages, Guid userMessageId)
    {
        var currentId = userMessageId;

        while (true)
        {
            var children = messages
                .Where(x => x.ParentMessageId == currentId)
                .OrderByDescending(x => x.CreatedUtc)
                .ToList();

            if (children.Count == 0)
                return currentId;

            currentId = children[0].Id;
        }
    }

    private static string BuildPromptPreview(string content)
    {
        var condensed = string.Join(
            " ",
            content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (condensed.Length <= 32)
            return condensed;

        return $"{condensed[..29].TrimEnd()}...";
    }

    private static string BuildThreadTitle(string prompt)
    {
        var title = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?? "New Chat";

        if (title.Length <= 60)
            return title;

        return $"{title[..57].TrimEnd()}...";
    }

    private static string NormalizeThreadTitle(string title)
    {
        var normalizedTitle = title.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            throw new InvalidOperationException("Renaming the chat failed because the new title was blank.");

        return normalizedTitle;
    }

    private sealed record MessageProjection(
        Guid Id,
        ChatRole Role,
        string Content,
        DateTime CreatedUtc,
        Guid? ParentMessageId,
        Guid? EditedFromMessageId);

    private sealed record RunProjection(
        Guid Id,
        Guid UserMessageId,
        Guid? AssistantMessageId,
        string Summary,
        ProcessRunStatus Status,
        DateTime StartedUtc,
        DateTime? CompletedUtc,
        IReadOnlyList<StepProjection> Steps);

    private sealed record StepProjection(
        Guid Id,
        string Title,
        string Summary,
        string Detail,
        string IconCssClass,
        ProcessStepStatus Status);
}
