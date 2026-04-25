using Microsoft.EntityFrameworkCore;

namespace AgentRp.Data;

public sealed class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
{
    public DbSet<ChatThread> ChatThreads => Set<ChatThread>();

    public DbSet<ChatStory> ChatStories => Set<ChatStory>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<ProcessRun> ProcessRuns => Set<ProcessRun>();

    public DbSet<ProcessStep> ProcessSteps => Set<ProcessStep>();

    public DbSet<StoryChatSnapshot> StoryChatSnapshots => Set<StoryChatSnapshot>();

    public DbSet<StoryChatAppearanceEntry> StoryChatAppearanceEntries => Set<StoryChatAppearanceEntry>();

    public DbSet<StoryImageAsset> StoryImageAssets => Set<StoryImageAsset>();

    public DbSet<StoryImageLink> StoryImageLinks => Set<StoryImageLink>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<AiProvider> AiProviders => Set<AiProvider>();

    public DbSet<AiModel> AiModels => Set<AiModel>();

    public DbSet<AiProviderMetric> AiProviderMetrics => Set<AiProviderMetric>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatThread>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Title).HasColumnType("nvarchar(max)");
            builder.Property(x => x.SelectedAgentName).HasMaxLength(200);
            builder.HasIndex(x => x.SelectedAiModelId);
            builder.HasIndex(x => x.UpdatedUtc);
            builder.HasIndex(x => new { x.IsStarred, x.UpdatedUtc });
            builder.HasOne(x => x.SelectedAiModel)
                .WithMany()
                .HasForeignKey(x => x.SelectedAiModelId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.Story)
                .WithOne(x => x.Thread)
                .HasForeignKey<ChatStory>(x => x.ChatThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.Messages)
                .WithOne(x => x.Thread)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.ProcessRuns)
                .WithOne(x => x.Thread)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.Snapshots)
                .WithOne(x => x.Thread)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.AppearanceEntries)
                .WithOne(x => x.Thread)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.ImageLinks)
                .WithOne(x => x.Thread)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatStory>(builder =>
        {
            builder.HasKey(x => x.ChatThreadId);
            builder.Property(x => x.SceneJson)
                .HasColumnType("nvarchar(max)");
            builder.Property(x => x.CharactersJson)
                .HasColumnType("nvarchar(max)");
            builder.Property(x => x.LocationsJson)
                .HasColumnType("nvarchar(max)");
            builder.Property(x => x.ItemsJson)
                .HasColumnType("nvarchar(max)");
            builder.Property(x => x.HistoryJson)
                .HasColumnType("nvarchar(max)");
            builder.Property(x => x.StoryContextJson)
                .HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<ChatMessage>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Role).HasConversion<string>();
            builder.Property(x => x.MessageKind).HasConversion<string>();
            builder.Property(x => x.GenerationMode).HasConversion<string>();
            builder.Property(x => x.Content).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ThreadId, x.CreatedUtc });
            builder.HasIndex(x => new { x.ThreadId, x.ParentMessageId });
            builder.HasIndex(x => x.EditedFromMessageId);
            builder.HasIndex(x => new { x.ThreadId, x.SourceProcessRunId });
        });

        modelBuilder.Entity<ProcessRun>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Status).HasConversion<string>();
            builder.Property(x => x.Summary).HasColumnType("nvarchar(max)");
            builder.Property(x => x.Stage).HasColumnType("nvarchar(max)");
            builder.Property(x => x.ContextJson).HasColumnType("nvarchar(max)");
            builder.Property(x => x.AiProviderKind).HasConversion<string>().HasMaxLength(80);
            builder.HasIndex(x => new { x.ThreadId, x.UserMessageId });
            builder.HasIndex(x => new { x.ThreadId, x.TargetMessageId });
            builder.HasIndex(x => new { x.ThreadId, x.AiProviderId });
            builder.HasMany(x => x.Steps)
                .WithOne(x => x.Run)
                .HasForeignKey(x => x.ProcessRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessStep>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Status).HasConversion<string>();
            builder.Property(x => x.Title).HasColumnType("nvarchar(max)");
            builder.Property(x => x.Summary).HasColumnType("nvarchar(max)");
            builder.Property(x => x.Detail).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ProcessRunId, x.SortOrder });
        });

        modelBuilder.Entity<AppSetting>(builder =>
        {
            builder.HasKey(x => x.Key);
            builder.Property(x => x.Key).HasMaxLength(200);
            builder.Property(x => x.JsonValue).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<StoryImageAsset>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.ContentType).HasMaxLength(100);
            builder.Property(x => x.FileName).HasMaxLength(500);
            builder.Property(x => x.Title).HasMaxLength(500);
            builder.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(80);
            builder.Property(x => x.Bytes).HasColumnType("varbinary(max)");
            builder.Property(x => x.UserPrompt).HasColumnType("nvarchar(max)");
            builder.Property(x => x.FinalPrompt).HasColumnType("nvarchar(max)");
            builder.Property(x => x.GenerationRationale).HasColumnType("nvarchar(max)");
            builder.Property(x => x.AvatarFocusXPercent);
            builder.Property(x => x.AvatarFocusYPercent);
            builder.Property(x => x.AvatarZoomPercent);
            builder.Property(x => x.TransientSessionId);
            builder.Property(x => x.TransientExpiresUtc);
            builder.Property(x => x.AiProviderKind).HasConversion<string>().HasMaxLength(80);
            builder.Property(x => x.AiProviderName).HasMaxLength(200);
            builder.Property(x => x.AiModelName).HasMaxLength(500);
            builder.Property(x => x.ProviderModelId).HasMaxLength(500);
            builder.Property(x => x.OptimizationLastError).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => x.CreatedUtc);
            builder.HasIndex(x => new { x.ThreadId, x.CreatedUtc });
            builder.HasIndex(x => new { x.IsTransient, x.TransientExpiresUtc });
            builder.HasIndex(x => new { x.ThreadId, x.TransientSessionId });
            builder.HasIndex(x => new { x.OptimizedUtc, x.OptimizationQueuedUtc, x.OptimizationAttemptCount });
            builder.HasMany(x => x.Links)
                .WithOne(x => x.Image)
                .HasForeignKey(x => x.ImageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoryImageLink>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.EntityKind).HasConversion<string>().HasMaxLength(80);
            builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(80);
            builder.HasIndex(x => new { x.ThreadId, x.EntityKind, x.EntityId });
            builder.HasIndex(x => new { x.ThreadId, x.ImageId });
            builder.HasIndex(x => x.MessageId);
            builder.HasIndex(x => x.ProcessRunId);
        });

        modelBuilder.Entity<AiProvider>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).HasMaxLength(200);
            builder.Property(x => x.ProviderKind).HasConversion<string>().HasMaxLength(80);
            builder.Property(x => x.BaseEndpoint).HasMaxLength(1000);
            builder.Property(x => x.ApiKey).HasColumnType("nvarchar(max)");
            builder.Property(x => x.ManagementApiKey).HasColumnType("nvarchar(max)");
            builder.Property(x => x.AccountId).HasMaxLength(300);
            builder.Property(x => x.ProjectId).HasMaxLength(300);
            builder.Property(x => x.TeamId).HasMaxLength(300);
            builder.Property(x => x.LastDiscoveryError).HasColumnType("nvarchar(max)");
            builder.Property(x => x.LastMetricsError).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => x.Name).IsUnique();
            builder.HasIndex(x => new { x.IsEnabled, x.SortOrder });
            builder.HasMany(x => x.Models)
                .WithOne(x => x.Provider)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(x => x.Metrics)
                .WithOne(x => x.Provider)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiModel>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.ProviderModelId).HasMaxLength(500);
            builder.Property(x => x.DisplayName).HasMaxLength(500);
            builder.Property(x => x.Endpoint).HasMaxLength(1000);
            builder.Property(x => x.Repository).HasMaxLength(500);
            builder.Property(x => x.PlanningSettingsJson).HasColumnType("nvarchar(max)");
            builder.Property(x => x.WritingSettingsJson).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ProviderId, x.ProviderModelId }).IsUnique();
            builder.HasIndex(x => new { x.IsEnabled, x.SortOrder });
        });

        modelBuilder.Entity<AiProviderMetric>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.MetricKind).HasMaxLength(120);
            builder.Property(x => x.Label).HasMaxLength(300);
            builder.Property(x => x.Value).HasMaxLength(500);
            builder.Property(x => x.Detail).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ProviderId, x.MetricKind });
        });

        modelBuilder.Entity<StoryChatSnapshot>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Summary).HasColumnType("nvarchar(max)");
            builder.Property(x => x.SnapshotJson).HasColumnType("nvarchar(max)");
            builder.Property(x => x.IncludedMessageIdsJson).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ThreadId, x.CreatedUtc });
            builder.HasIndex(x => new { x.ThreadId, x.SelectedLeafMessageId });
            builder.HasIndex(x => new { x.ThreadId, x.CoveredThroughMessageId });
        });

        modelBuilder.Entity<StoryChatAppearanceEntry>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Summary).HasColumnType("nvarchar(max)");
            builder.Property(x => x.AppearanceJson).HasColumnType("nvarchar(max)");
            builder.HasIndex(x => new { x.ThreadId, x.CreatedUtc });
            builder.HasIndex(x => new { x.ThreadId, x.SelectedLeafMessageId });
            builder.HasIndex(x => new { x.ThreadId, x.CoveredThroughMessageId });
        });
    }
}

public sealed class ChatThread
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    public bool IsStarred { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public Guid? ActiveLeafMessageId { get; set; }

    public Guid? SelectedSpeakerCharacterId { get; set; }

    public string SelectedAgentName { get; set; } = string.Empty;

    public Guid? SelectedAiModelId { get; set; }

    public AiModel? SelectedAiModel { get; set; }

    public ChatStory? Story { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];

    public List<ProcessRun> ProcessRuns { get; set; } = [];

    public List<StoryChatSnapshot> Snapshots { get; set; } = [];

    public List<StoryChatAppearanceEntry> AppearanceEntries { get; set; } = [];

    public List<StoryImageLink> ImageLinks { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public ChatRole Role { get; set; }

    public ChatMessageKind MessageKind { get; set; }

    public required string Content { get; set; }

    public DateTime CreatedUtc { get; set; }

    public Guid? SpeakerCharacterId { get; set; }

    public StoryScenePostMode GenerationMode { get; set; }

    public Guid? SourceProcessRunId { get; set; }

    public Guid? ParentMessageId { get; set; }

    public Guid? EditedFromMessageId { get; set; }

    public required ChatThread Thread { get; set; }
}

public sealed class ProcessRun
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public Guid UserMessageId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public Guid? TargetMessageId { get; set; }

    public Guid? ActorCharacterId { get; set; }

    public Guid? AiModelId { get; set; }

    public Guid? AiProviderId { get; set; }

    public AiProviderKind? AiProviderKind { get; set; }

    public required string Summary { get; set; }

    public string? Stage { get; set; }

    public string? ContextJson { get; set; }

    public ProcessRunStatus Status { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? PlanningStartedUtc { get; set; }

    public DateTime? PlanningCompletedUtc { get; set; }

    public DateTime? ProseStartedUtc { get; set; }

    public DateTime? ProseCompletedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public required ChatThread Thread { get; set; }

    public List<ProcessStep> Steps { get; set; } = [];
}

public sealed class ProcessStep
{
    public Guid Id { get; set; }

    public Guid ProcessRunId { get; set; }

    public int SortOrder { get; set; }

    public required string Title { get; set; }

    public required string Summary { get; set; }

    public required string Detail { get; set; }

    public required string IconCssClass { get; set; }

    public ProcessStepStatus Status { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long? InputTokenCount { get; set; }

    public long? OutputTokenCount { get; set; }

    public long? TotalTokenCount { get; set; }

    public ProcessRun Run { get; set; } = null!;
}

public sealed class StoryChatSnapshot
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public Guid SelectedLeafMessageId { get; set; }

    public Guid CoveredThroughMessageId { get; set; }

    public DateTime CoveredThroughUtc { get; set; }

    public required string Summary { get; set; }

    public required string SnapshotJson { get; set; }

    public required string IncludedMessageIdsJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public ChatThread Thread { get; set; } = null!;
}

public sealed class StoryChatAppearanceEntry
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public Guid SelectedLeafMessageId { get; set; }

    public Guid CoveredThroughMessageId { get; set; }

    public DateTime CoveredThroughUtc { get; set; }

    public required string Summary { get; set; }

    public required string AppearanceJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ChatThread Thread { get; set; } = null!;
}

public sealed class StoryImageAsset
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public required byte[] Bytes { get; set; }

    public required string ContentType { get; set; }

    public string? FileName { get; set; }

    public string? Title { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public StoryImageSourceKind SourceKind { get; set; }

    public string? UserPrompt { get; set; }

    public string? FinalPrompt { get; set; }

    public string? GenerationRationale { get; set; }

    public int? AvatarFocusXPercent { get; set; }

    public int? AvatarFocusYPercent { get; set; }

    public int? AvatarZoomPercent { get; set; }

    public bool IsTransient { get; set; }

    public Guid? TransientSessionId { get; set; }

    public DateTime? TransientExpiresUtc { get; set; }

    public Guid? AiProviderId { get; set; }

    public AiProviderKind? AiProviderKind { get; set; }

    public string? AiProviderName { get; set; }

    public Guid? AiModelId { get; set; }

    public string? AiModelName { get; set; }

    public string? ProviderModelId { get; set; }

    public DateTime? OptimizedUtc { get; set; }

    public DateTime? OptimizationQueuedUtc { get; set; }

    public int OptimizationAttemptCount { get; set; }

    public DateTime? OptimizationLastAttemptUtc { get; set; }

    public string? OptimizationLastError { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<StoryImageLink> Links { get; set; } = [];
}

public sealed class StoryImageLink
{
    public Guid Id { get; set; }

    public Guid ImageId { get; set; }

    public Guid ThreadId { get; set; }

    public StoryImageEntityKind EntityKind { get; set; }

    public Guid EntityId { get; set; }

    public Guid? MessageId { get; set; }

    public Guid? ProcessRunId { get; set; }

    public StoryImageLinkPurpose Purpose { get; set; }

    public DateTime CreatedUtc { get; set; }

    public StoryImageAsset Image { get; set; } = null!;

    public ChatThread Thread { get; set; } = null!;
}

public sealed class AppSetting
{
    public required string Key { get; set; }

    public required string JsonValue { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

public sealed class AiProvider
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public AiProviderKind ProviderKind { get; set; }

    public required string BaseEndpoint { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    public string ManagementApiKey { get; set; } = string.Empty;

    public string? AccountId { get; set; }

    public string? ProjectId { get; set; }

    public string? TeamId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastDiscoveredUtc { get; set; }

    public string? LastDiscoveryError { get; set; }

    public DateTime? LastMetricsRefreshUtc { get; set; }

    public string? LastMetricsError { get; set; }

    public List<AiModel> Models { get; set; } = [];

    public List<AiProviderMetric> Metrics { get; set; } = [];
}

public sealed class AiModel
{
    public Guid Id { get; set; }

    public Guid ProviderId { get; set; }

    public required string ProviderModelId { get; set; }

    public required string DisplayName { get; set; }

    public string? Endpoint { get; set; }

    public string? Repository { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsTextModelEnabled { get; set; } = true;

    public bool IsImageModelEnabled { get; set; }

    public bool UseJsonSchemaResponseFormat { get; set; } = true;

    public int SortOrder { get; set; }

    public required string PlanningSettingsJson { get; set; }

    public required string WritingSettingsJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public AiProvider Provider { get; set; } = null!;
}

public sealed class AiProviderMetric
{
    public Guid Id { get; set; }

    public Guid ProviderId { get; set; }

    public required string MetricKind { get; set; }

    public required string Label { get; set; }

    public required string Value { get; set; }

    public string? Detail { get; set; }

    public DateTime RefreshedUtc { get; set; }

    public AiProvider Provider { get; set; } = null!;
}

public enum AiProviderKind
{
    OpenAI,
    Grok,
    Claude,
    HuggingFaceInferenceEndpoint,
    OpenAiCompatible
}

public enum StoryImageSourceKind
{
    Uploaded,
    Generated
}

public enum StoryImageEntityKind
{
    Character,
    Location,
    Item
}

public enum StoryImageLinkPurpose
{
    Gallery,
    Reference
}

public enum ChatRole
{
    User,
    Assistant
}

public enum ChatMessageKind
{
    CharacterSpeech,
    Narration,
    System
}

public enum StoryScenePostMode
{
    Manual,
    GuidedAi,
    AutomaticAi,
    RespondGuidedAi,
    RespondAutomaticAi
}

public enum ProcessRunStatus
{
    Running,
    Canceled,
    Completed,
    Failed
}

public enum ProcessStepStatus
{
    Pending,
    Running,
    Canceled,
    Completed,
    Failed
}
