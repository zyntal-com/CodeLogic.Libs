using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>A sync plan: every step, the versions they depend on, and a digest to approve it by.</summary>
public sealed record StorageSyncPlan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The plan format this library writes; a plan of another format is refused at apply.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Gets the plan format; plans without one (older libraries) are refused at apply.</summary>
    public int SchemaVersion { get; init; }
    /// <summary>
    /// Gets the source connection's id; the plan applies only to connections with the same ids. The id is the only
    /// connection identity a plan records: a connection registered again under the same id is taken for the same
    /// store, and one registered under a new id refuses the plan (plan again). Keep connection ids stable.
    /// </summary>
    public string? SourceConnectionId { get; init; }
    /// <summary>Gets the destination connection's id.</summary>
    public string? DestinationConnectionId { get; init; }
    /// <summary>Gets the source directory.</summary>
    public required string SourceRoot { get; init; }
    /// <summary>Gets the destination directory.</summary>
    public required string DestinationRoot { get; init; }
    /// <summary>Gets the sync's baseline name, when it has one.</summary>
    public string? SyncId { get; init; }
    /// <summary>Gets the baseline generation the plan was made against.</summary>
    public long BaselineGeneration { get; init; }
    /// <summary>Gets the direction.</summary>
    public StorageSyncDirection Direction { get; init; }
    /// <summary>Gets the conflict policy.</summary>
    public StorageSyncConflictPolicy ConflictPolicy { get; init; }
    /// <summary>Gets whether names differing only by case were matched as one item.</summary>
    public bool CaseInsensitive { get; init; }
    /// <summary>Gets the digest of the options the plan was made with; the apply must use the same options.</summary>
    public string? OptionsDigest { get; init; }
    /// <summary>Gets the steps, in the order they run.</summary>
    public IReadOnlyList<StorageSyncAction> Actions { get; init; } = [];
    /// <summary>Gets the files that already matched.</summary>
    public int Unchanged { get; init; }
    /// <summary>
    /// Gets, for a two-way sync with a baseline, the paths both sides agreed on when the plan was made, with the
    /// versions seen then, and the previous entries of paths the plan leaves alone (left out by a filter, hidden,
    /// a skipped link, under a file/folder clash, or a folder kept by excluded items). The baseline saved after the
    /// run records these and what the run's copies wrote — never what happens to be there afterwards, so an edit
    /// made during the run is still seen next time, and a deletion made while a path was left out is not undone.
    /// Entries below a folder the filters leave out are dropped once neither side has that folder any more.
    /// </summary>
    public IReadOnlyDictionary<string, StorageSyncBaselineEntry> Agreed { get; init; } = new Dictionary<string, StorageSyncBaselineEntry>();
    /// <summary>Gets notes for a person, such as why deletions are withheld.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Gets when the plan was made.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Gets the SHA-256 over everything above; approve a plan by this value.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Gets the conflicts left for a person.</summary>
    [JsonIgnore]
    public IReadOnlyList<StorageSyncAction> Conflicts => [.. Actions.Where(action => action.Kind == StorageSyncActionKind.Conflict)];

    /// <summary>
    /// Gets whether the plan can be applied without <see cref="StorageSyncOptions.ApplyWithConflicts"/>: it has no
    /// conflicts, or only those <see cref="StorageSyncConflictPolicy.NewerWins"/> could not decide (left alone).
    /// </summary>
    [JsonIgnore]
    public bool IsApprovable => Conflicts.Count == 0 || ConflictPolicy == StorageSyncConflictPolicy.NewerWins;

    /// <summary>Serializes the plan so it can be shown, stored, and approved later.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a plan written by <see cref="ToJson"/>.</summary>
    public static StorageSyncPlan FromJson(string json) =>
        JsonSerializer.Deserialize<StorageSyncPlan>(json, Json) ?? throw new JsonException("The plan is empty.");

    /// <summary>Computes the digest of the plan's content (everything but the digest itself).</summary>
    public string ComputeDigest() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this with { Digest = string.Empty }, Json))));
}
