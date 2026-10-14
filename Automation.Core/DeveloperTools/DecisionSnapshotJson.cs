using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Automation.Core.DeveloperTools;

public sealed record DecisionSnapshotSuggestedRegionsFile(
    int SchemaVersion,
    string Status,
    string ReasonId,
    IReadOnlyList<DecisionSnapshotRegionMetadata> Regions);

public static class DecisionSnapshotJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeMetadata(DecisionSnapshotMetadata metadata)
        => JsonSerializer.Serialize(metadata, Options);

    public static string SerializeSuggestedRegions(DecisionSnapshotMetadata metadata)
        => JsonSerializer.Serialize(
            new DecisionSnapshotSuggestedRegionsFile(
                1,
                metadata.Status,
                metadata.ReasonId,
                metadata.Regions),
            Options);
}