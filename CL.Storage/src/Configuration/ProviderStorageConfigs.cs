using CodeLogic.Core.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CL.Storage.Configuration;

/// <summary>Common shape used by provider connection configuration.</summary>
/// <remarks>
/// A C# 15 <c>closed</c> class: the provider connection types (FTP, SFTP, WebDAV, S3, Azure Blob, Google Cloud,
/// Swift) are its only direct subtypes, so a <c>switch</c> over a connection is checked for exhaustiveness.
/// </remarks>
public closed class StorageConnectionConfigBase
{
    /// <summary>Gets or sets whether this named connection is enabled.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gets or sets upload and download speed limits for this connection.</summary>
    public StorageTransferLimitsConfig TransferLimits { get; set; } = new();
    /// <summary>Gets the provider-specific root mounted as provider-neutral path <c>/</c>.</summary>
    [JsonIgnore]
    public abstract string MountRoot { get; }
    internal virtual IEnumerable<string> GetValidationErrors() => [];

    /// <summary>Validates the provider connection settings.</summary>
    /// <returns>A configuration validation result containing every detected error.</returns>
    public ConfigValidationResult Validate()
    {
        var errors = GetValidationErrors()
            .Concat((TransferLimits ?? new StorageTransferLimitsConfig()).GetValidationErrors("TransferLimits."))
            .ToArray();
        return errors.Length == 0
            ? ConfigValidationResult.Valid()
            : ConfigValidationResult.Invalid(errors);
    }
}

/// <summary>Provides non-generic access to a provider's named connection collection.</summary>
public abstract class ProviderStorageConfigBase : ConfigModelBase
{
    internal abstract IEnumerable<KeyValuePair<string, StorageConnectionConfigBase>> EnumerateConnections();
    internal abstract bool ContainsConnection(string connectionId);
    internal abstract ProviderStorageConfigBase DeepClone();
    internal abstract void SetConnection(string connectionId, StorageConnectionConfigBase connection);
    internal abstract bool RemoveConnection(string connectionId);
}

/// <summary>Defines configuration for a provider with multiple independently named connections.</summary>
/// <typeparam name="TConnection">Provider-specific connection configuration type.</typeparam>
public abstract class ProviderStorageConfigBase<TConnection> : ProviderStorageConfigBase
    where TConnection : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the provider connections keyed by case-insensitive connection ID.</summary>
    public Dictionary<string, TConnection> Connections { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Connections is null)
            return ConfigValidationResult.Invalid("Connections cannot be null");
        foreach (var (id, connection) in Connections)
        {
            if (string.IsNullOrWhiteSpace(id))
                errors.Add("Connection IDs cannot be blank");
            if (connection is null)
            {
                errors.Add($"Connection '{id}' configuration is required");
                continue;
            }
            foreach (var error in connection.Validate().Errors)
                errors.Add($"Connection '{id}': {error}");
        }
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }

    internal override IEnumerable<KeyValuePair<string, StorageConnectionConfigBase>> EnumerateConnections() =>
        Connections.Select(pair => new KeyValuePair<string, StorageConnectionConfigBase>(pair.Key, pair.Value));

    internal override bool ContainsConnection(string connectionId) => Connections.Keys.Any(
        candidate => string.Equals(candidate, connectionId, StringComparison.OrdinalIgnoreCase));

    internal override ProviderStorageConfigBase DeepClone()
    {
        var json = JsonSerializer.Serialize(this, GetType());
        var clone = (ProviderStorageConfigBase<TConnection>)JsonSerializer.Deserialize(json, GetType())!;
        clone.Connections = new Dictionary<string, TConnection>(clone.Connections, StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    internal override void SetConnection(string connectionId, StorageConnectionConfigBase connection)
    {
        RemoveConnection(connectionId);
        Connections[connectionId] = (TConnection)connection;
    }

    internal override bool RemoveConnection(string connectionId)
    {
        var key = Connections.Keys.FirstOrDefault(
            candidate => string.Equals(candidate, connectionId, StringComparison.OrdinalIgnoreCase));
        return key is not null && Connections.Remove(key);
    }
}
