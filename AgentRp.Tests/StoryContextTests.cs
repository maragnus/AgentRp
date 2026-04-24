using AgentRp.Data;
using AgentRp.Services;

namespace AgentRp.Tests;

public sealed class StoryContextTests
{
    [Fact]
    public void StoryContextNormalizer_TrimsText_AndDefaultsContentControls()
    {
        var normalized = StoryDocumentNormalizer.Normalize(new ChatStoryContextDocument(
            "  Romance  ",
            "  Rainy city  ",
            "  Tense  ",
            "  Push them toward a breakup  ",
            (StoryContentIntensity)99,
            StoryContentIntensity.Forbidden));

        Assert.Equal("Romance", normalized.Genre);
        Assert.Equal("Rainy city", normalized.Setting);
        Assert.Equal("Tense", normalized.Tone);
        Assert.Equal("Push them toward a breakup", normalized.StoryDirection);
        Assert.Equal(StoryContentIntensity.Allowed, normalized.ExplicitContent);
        Assert.Equal(StoryContentIntensity.Forbidden, normalized.ViolentContent);
    }

    [Fact]
    public void StoryContextJson_RoundTripsNarrativeSettings()
    {
        var document = new ChatStoryContextDocument(
            "Noir",
            "Chicago winter",
            "Brooding",
            "Tighten the investigation around the missing ledger.",
            StoryContentIntensity.Allowed,
            StoryContentIntensity.Encouraged);

        var json = ChatStoryJson.Serialize(document);
        var restored = StoryDocumentNormalizer.Normalize(ChatStoryJson.Deserialize(json, ChatStoryContextDocument.Empty));

        Assert.Equal(document, restored);
    }

    [Fact]
    public void NormalizeSelection_WithMessages_DoesNotForceStoryContext()
    {
        var service = CreateTransferService();

        var selection = service.NormalizeSelection(
            new ChatTransferSelection(true, false, false, false, false, false, false, false),
            ChatTransferSelection.All);

        Assert.True(selection.Messages);
        Assert.False(selection.StoryContext);
        Assert.False(selection.SceneState);
    }

    [Fact]
    public void NormalizeSelection_WithoutMessages_RemovesChatLogChildren()
    {
        var service = CreateTransferService();

        var selection = service.NormalizeSelection(
            ChatTransferSelection.All with { Messages = false },
            ChatTransferSelection.All);

        Assert.False(selection.Messages);
        Assert.False(selection.Snapshots);
        Assert.False(selection.CurrentAppearanceBlocks);
        Assert.True(selection.Characters);
        Assert.True(selection.Locations);
        Assert.True(selection.Items);
        Assert.True(selection.StoryContext);
        Assert.True(selection.SceneState);
    }

    [Fact]
    public void GetLockedSections_WithMessages_AllowsSectionToggles()
    {
        var service = CreateTransferService();

        var lockedSections = service.GetLockedSections(ChatTransferSelection.All, ChatTransferSelection.All);

        Assert.False(lockedSections.Messages);
        Assert.False(lockedSections.Snapshots);
        Assert.False(lockedSections.CurrentAppearanceBlocks);
        Assert.False(lockedSections.Characters);
        Assert.False(lockedSections.Locations);
        Assert.False(lockedSections.Items);
        Assert.False(lockedSections.StoryContext);
        Assert.False(lockedSections.SceneState);
    }

    [Fact]
    public void GetLockedSections_WithoutMessages_LocksChatLogChildren()
    {
        var service = CreateTransferService();

        var lockedSections = service.GetLockedSections(ChatTransferSelection.All with { Messages = false }, ChatTransferSelection.All);

        Assert.False(lockedSections.Messages);
        Assert.True(lockedSections.Snapshots);
        Assert.True(lockedSections.CurrentAppearanceBlocks);
        Assert.False(lockedSections.Characters);
        Assert.False(lockedSections.Locations);
        Assert.False(lockedSections.Items);
        Assert.False(lockedSections.StoryContext);
        Assert.False(lockedSections.SceneState);
    }

    [Fact]
    public void InspectPackage_ReportsStoryContextAvailability_OnlyWhenNarrativeAndHistoryExist()
    {
        var service = CreateTransferService();
        var completePackage = new ChatTransferPackage(
            2,
            DateTime.UtcNow,
            new ChatTransferSourceInfo("Story"),
            new ChatTransferPayload(
                null,
                null,
                null,
                null,
                null,
                null,
                new ChatStoryContextDocument("Mystery", string.Empty, string.Empty, string.Empty, StoryContentIntensity.Allowed, StoryContentIntensity.Allowed),
                ChatStoryHistoryDocument.Empty,
                null,
                null,
                null));
        var incompletePackage = completePackage with
        {
            Payload = completePackage.Payload with
            {
                History = null
            }
        };

        var completeInspection = service.InspectPackage(service.SerializePackage(completePackage));
        var incompleteInspection = service.InspectPackage(service.SerializePackage(incompletePackage));

        Assert.True(completeInspection.Source.AvailableSections.StoryContext);
        Assert.False(incompleteInspection.Source.AvailableSections.StoryContext);
    }

    private static ChatTransferService CreateTransferService() => new(null!, null!, null!);
}
