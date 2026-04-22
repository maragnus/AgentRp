using System.Text.Json.Serialization;

namespace AgentRp.Services;

public enum StoryEntityFieldKey
{
    Name,
    Title,
    Summary,
    GeneralAppearance,
    CorePersonality,
    Relationships,
    PreferencesBeliefs,
    Details,
    WhenText
}

public enum StoryFieldGuidanceFormat
{
    Words,
    Sentences,
    Paragraphs,
    BulletList
}

public enum StoryFieldGuidanceDetailLevel
{
    Simple,
    Medium,
    Detailed
}

public sealed record StoryFieldGuidanceLength(
    int Minimum,
    int Maximum);

public sealed record StoryFieldGuidance(
    [property: JsonPropertyName("Type")]
    StoryFieldGuidanceFormat Format,
    StoryFieldGuidanceDetailLevel LevelOfDetail,
    StoryFieldGuidanceLength SuggestedLength,
    string Example);

public sealed record StoryEntityFieldGuidanceView(
    StoryEntityKind EntityKind,
    StoryEntityFieldKey FieldKey,
    string Label,
    StoryFieldGuidance Guidance);

public sealed record UpdateStoryEntityFieldGuidance(
    StoryEntityKind EntityKind,
    StoryEntityFieldKey FieldKey,
    StoryFieldGuidance Guidance);
