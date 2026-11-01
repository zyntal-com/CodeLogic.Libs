using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Defines named local filesystem storage connections.</summary>
[ConfigSection("storage.local")]
public sealed class LocalStorageConfig : ConfigModelBase
{
    /// <summary>Gets or sets local connections keyed by case-insensitive connection ID.</summary>
    [ConfigField(Label = "Connections", Description = "Named local or mounted filesystem roots.", Group = "Connections", Order = 0)]
    public Dictionary<string, LocalConnectionConfig> Connections { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Connections is null)
            return ConfigValidationResult.Invalid("Connections cannot be null");
        foreach (var connection in Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.Key))
                errors.Add("Local connection IDs cannot be blank");
            var validation = connection.Value?.Validate() ?? ConfigValidationResult.Invalid("Connection configuration is required");
            if (!validation.IsValid)
                errors.Add($"Connection '{connection.Key}': {string.Join(", ", validation.Errors)}");
        }
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}

/// <summary>Defines one root-confined local or mounted-filesystem connection.</summary>
/// <remarks>
/// Paths are checked against <see cref="RootPath"/> (no parent traversal, no rooted paths, no links leading out)
/// when they are resolved. This is traversal protection for caller-supplied paths, not a sandbox: a process that
/// can modify the tree under the root concurrently can replace a checked path with a link between the check
/// and the file being opened.
/// </remarks>
public sealed class LocalConnectionConfig
{
    /// <summary>Gets or sets whether this connection is enabled.</summary>
    [ConfigField(Label = "Enabled", Description = "Enable this local storage connection.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the absolute filesystem root exposed by the connection. A relative path is a validation error:
    /// it would resolve against the process's current directory, which a service host does not keep stable.
    /// </summary>
    [ConfigField(Label = "Root Path", Required = true, Description = "Absolute local path or mounted UNC root exposed by this connection.", Placeholder = "C:\\data or \\\\server\\share", RequiresRestart = true, Group = "Connection", Order = 10)]
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether links resolving within the configured root may be followed.</summary>
    [ConfigField(Label = "Follow Links", Description = "Allow links only when their resolved target remains under the configured root.", RequiresRestart = true, Group = "Security", Order = 20)]
    public bool FollowLinks { get; set; }

    /// <summary>Gets or sets upload and download speed limits for this connection.</summary>
    public StorageTransferLimitsConfig TransferLimits { get; set; } = new();

    /// <summary>Validates the local connection settings.</summary>
    /// <returns>A configuration validation result.</returns>
    public ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Enabled && string.IsNullOrWhiteSpace(RootPath))
            errors.Add("RootPath is required for an enabled local connection");
        else if (Enabled && !Path.IsPathFullyQualified(RootPath))
            errors.Add("RootPath must be an absolute path (a relative root would depend on the process's working directory)");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
