using System.Reflection;
using System.Text.Json;
using AgentRp.Data;
using AgentRp.Services;

namespace AgentRp.Tests;

public sealed class StorySceneChatServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void GetResponderCandidates_ExcludesActiveSpeaker_AndOutOfSceneCharacters()
    {
        var activeSpeakerId = Guid.NewGuid();
        var candidates = InvokePrivateStatic<IReadOnlyList<StorySceneCharacterContext>>(
            "GetResponderCandidates",
            new List<StorySceneCharacterContext>
            {
                new(activeSpeakerId, "Ava", "Lead", "Coat", "Rain damp hair", "Focused", "Ben", "Truth", true),
                new(Guid.NewGuid(), "Ben", "Support", "Jacket", "Rolled sleeves", "Calm", "Ava", "Trust", true),
                new(Guid.NewGuid(), "Cleo", "Offstage", "Scarf", "Hidden", "Watchful", "Ava", "Distance", false)
            },
            activeSpeakerId);

        Assert.Collection(
            candidates,
            candidate => Assert.Equal("Ben", candidate.Name));
    }

    [Fact]
    public void GetResponderCandidates_WithNarratorActive_ReturnsOnlyPresentCharacters()
    {
        var candidates = InvokePrivateStatic<IReadOnlyList<StorySceneCharacterContext>>(
            "GetResponderCandidates",
            new List<StorySceneCharacterContext>
            {
                new(Guid.NewGuid(), "Ava", "Lead", "Coat", "Rain damp hair", "Focused", "Ben", "Truth", true),
                new(Guid.NewGuid(), "Ben", "Support", "Jacket", "Rolled sleeves", "Calm", "Ava", "Trust", true),
                new(Guid.NewGuid(), "Cleo", "Offstage", "Scarf", "Hidden", "Watchful", "Ava", "Distance", false)
            },
            null);

        Assert.Equal(["Ava", "Ben"], candidates.Select(candidate => candidate.Name).ToArray());
    }

    [Fact]
    public void GetResponderCandidates_WhenNoAlternateExists_ThrowsClearError()
    {
        var activeSpeakerId = Guid.NewGuid();
        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokePrivateStatic<IReadOnlyList<StorySceneCharacterContext>>(
                "GetResponderCandidates",
                new List<StorySceneCharacterContext>
                {
                    new(activeSpeakerId, "Ava", "Lead", "Coat", "Rain damp hair", "Focused", "Ben", "Truth", true),
                    new(Guid.NewGuid(), "Cleo", "Offstage", "Scarf", "Hidden", "Watchful", "Ava", "Distance", false)
                },
                activeSpeakerId));

        Assert.Equal(
            "Generating the response failed because another present character is required.",
            exception.InnerException?.Message);
    }

    [Fact]
    public void MapProcess_GuidedRun_ResolvesArtifactsByTitle()
    {
        var process = InvokePrivateStatic<StorySceneMessageProcessView>(
            "MapProcess",
            new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = Guid.NewGuid(),
                UserMessageId = Guid.NewGuid(),
                Summary = "Guided run",
                Status = ProcessRunStatus.Completed,
                StartedUtc = DateTime.UtcNow,
                Thread = CreateThread(),
                ContextJson = JsonSerializer.Serialize(
                    new StoryMessageProcessContext(
                        StoryScenePostMode.GuidedAi,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        [
                            new(ProcessStepKey("appearance"), "Appearance", [], []),
                            new(ProcessStepKey("planning"), "Planning", [], []),
                            new(ProcessStepKey("writing"), "Writing", [], [])
                        ]),
                    JsonOptions),
                Steps =
                [
                    CreateStep(30, "Writing"),
                    CreateStep(10, "Appearance"),
                    CreateStep(20, "Planning")
                ]
            },
            new Dictionary<Guid, StorySceneAppearanceEntryView>());

        Assert.Equal(3, process.Steps.Count);
        Assert.Equal(
            new string?[] { "appearance", "planning", "writing" },
            process.Steps.Select(step => step.Artifact?.StepKey).ToArray());
    }

    [Fact]
    public void MapProcess_AutomaticRun_ResolvesArtifactsWithoutResponderStep()
    {
        var process = InvokePrivateStatic<StorySceneMessageProcessView>(
            "MapProcess",
            new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = Guid.NewGuid(),
                UserMessageId = Guid.NewGuid(),
                Summary = "Automatic run",
                Status = ProcessRunStatus.Running,
                StartedUtc = DateTime.UtcNow,
                Thread = CreateThread(),
                ContextJson = JsonSerializer.Serialize(
                    new StoryMessageProcessContext(
                        StoryScenePostMode.AutomaticAi,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        [
                            new(ProcessStepKey("appearance"), "Appearance", [], []),
                            new(ProcessStepKey("planning"), "Planning", [], []),
                            new(ProcessStepKey("writing"), "Writing", [], [])
                        ]),
                    JsonOptions),
                Steps =
                [
                    CreateStep(30, "Writing"),
                    CreateStep(10, "Appearance"),
                    CreateStep(20, "Planning")
                ]
            },
            new Dictionary<Guid, StorySceneAppearanceEntryView>());

        Assert.Equal(3, process.Steps.Count);
        Assert.DoesNotContain(process.Steps, step => step.Title == "Responder");
        Assert.Equal(
            new string?[] { "appearance", "planning", "writing" },
            process.Steps.Select(step => step.Artifact?.StepKey).ToArray());
    }

    [Fact]
    public void MapProcess_RespondAutomaticRun_ResolvesResponderArtifactWithoutSortOrderAssumptions()
    {
        var process = InvokePrivateStatic<StorySceneMessageProcessView>(
            "MapProcess",
            new ProcessRun
            {
                Id = Guid.NewGuid(),
                ThreadId = Guid.NewGuid(),
                UserMessageId = Guid.NewGuid(),
                Summary = "Respond automatic run",
                Status = ProcessRunStatus.Running,
                StartedUtc = DateTime.UtcNow,
                Thread = CreateThread(),
                ContextJson = JsonSerializer.Serialize(
                    new StoryMessageProcessContext(
                        StoryScenePostMode.RespondAutomaticAi,
                        null,
                        null,
                        null,
                        new StorySceneResponderSelectionResult(Guid.NewGuid(), "Ava", Guid.NewGuid(), "Ben", "He has the strongest immediate stake."),
                        null,
                        null,
                        null,
                        [
                            new(ProcessStepKey("appearance"), "Appearance", [], []),
                            new(ProcessStepKey("responder"), "Responder", [], []),
                            new(ProcessStepKey("planning"), "Planning", [], []),
                            new(ProcessStepKey("writing"), "Writing", [], [])
                        ]),
                    JsonOptions),
                Steps =
                [
                    CreateStep(40, "Planning"),
                    CreateStep(10, "Responder"),
                    CreateStep(30, "Writing"),
                    CreateStep(20, "Appearance")
                ]
            },
            new Dictionary<Guid, StorySceneAppearanceEntryView>());

        Assert.Equal(4, process.Steps.Count);
        Assert.Contains(process.Steps, step => step.Title == "Responder" && step.Artifact?.StepKey == "responder");
        Assert.Equal(
            new string?[] { "responder", "appearance", "writing", "planning" },
            process.Steps.Select(step => step.Artifact?.StepKey).ToArray());
    }

    [Fact]
    public void NormalizeEditablePlanner_AllowsEmptyGuardrails()
    {
        var planner = InvokePrivateStatic<StoryMessagePlannerResult>(
            "NormalizeEditablePlanner",
            new StoryMessagePlannerResult(
                StoryTurnShape.Brief,
                "Beat",
                "Intent",
                "Immediate goal",
                "Why now",
                "Change introduced",
                []));

        Assert.Empty(planner.Guardrails);
    }

    [Fact]
    public void NormalizeEditablePlanner_NormalizesNullGuardrailsToEmpty()
    {
        var planner = InvokePrivateStatic<StoryMessagePlannerResult>(
            "NormalizeEditablePlanner",
            new StoryMessagePlannerResult(
                StoryTurnShape.Brief,
                "Beat",
                "Intent",
                "Immediate goal",
                "Why now",
                "Change introduced",
                null!));

        Assert.Empty(planner.Guardrails);
    }

    private static ChatThread CreateThread() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Thread",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static ProcessStep CreateStep(int sortOrder, string title) => new()
    {
        Id = Guid.NewGuid(),
        SortOrder = sortOrder,
        Title = title,
        Summary = title,
        Detail = $"{title} detail",
        IconCssClass = "fa-regular fa-circle",
        Status = ProcessStepStatus.Pending
    };

    private static string ProcessStepKey(string key) => key;

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(StorySceneChatService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} was not found.");
        var result = method.Invoke(null, args);
        return result is T typedResult
            ? typedResult
            : throw new InvalidOperationException($"Method {methodName} returned an unexpected value.");
    }
}
