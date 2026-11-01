using CodeLogic.Core.Configuration;
using System.ComponentModel.DataAnnotations;

namespace CL.GitHelper.Models;

/// <summary>
/// Configuration for a single Git repository entry.
/// </summary>
public sealed class RepositoryConfiguration
{
    /// <summary>
    /// Unique identifier for this repository (e.g., "Default", "MyRepo").
    /// </summary>
    [ConfigField(Label = "ID", Required = true, Group = "Identity", Order = 0)]
    public string Id { get; set; } = "Default";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [ConfigField(Label = "Display Name", Group = "Identity", Order = 1)]
    public string Name { get; set; } = "Default Repository";

    /// <summary>
    /// Remote repository URL — HTTPS or SSH (e.g., "https://github.com/org/repo.git").
    /// </summary>
    [ConfigField(Label = "Repository URL", Required = true,
        Placeholder = "https://github.com/org/repo.git or git@github.com:org/repo.git",
        Group = "Identity", Order = 2)]
    public string RepositoryUrl { get; set; } = "";

    /// <summary>
    /// Local directory path where the repository is (or will be) cloned.
    /// Relative paths are resolved from <see cref="GitHelperConfig.BaseDirectory"/>.
    /// </summary>
    [ConfigField(Label = "Local Path", Required = true,
        Description = "Absolute or relative to the base directory.",
        Group = "Identity", Order = 3)]
    public string LocalPath { get; set; } = "";

    /// <summary>
    /// Default branch name used when no branch is specified (e.g., "main", "master").
    /// </summary>
    [ConfigField(Label = "Default Branch", Placeholder = "main", Group = "Identity", Order = 4)]
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Username for HTTPS authentication. Leave null for anonymous or SSH access.
    /// </summary>
    [ConfigField(Label = "HTTPS Username", Description = "Leave blank for anonymous or SSH access.",
        Group = "Authentication", Order = 10)]
    public string? Username { get; set; }

    /// <summary>
    /// Password or Personal Access Token for HTTPS authentication.
    /// Leave null for anonymous or SSH access.
    /// </summary>
    [ConfigField(Label = "HTTPS Password / PAT", InputType = ConfigInputType.Password, Secret = true,
        Description = "Personal Access Token is recommended over a plaintext password.",
        Group = "Authentication", Order = 11)]
    public string? Password { get; set; }

    /// <summary>
    /// Path to an SSH private key file for SSH-URL repositories.
    /// </summary>
    [ConfigField(Label = "SSH Key Path", Description = "Absolute path to the private key for SSH repos.",
        Group = "Authentication", Order = 12, Collapsed = true)]
    public string? SshKeyPath { get; set; }

    /// <summary>
    /// Passphrase for the SSH private key, if encrypted.
    /// </summary>
    [ConfigField(Label = "SSH Key Passphrase", InputType = ConfigInputType.Password, Secret = true,
        Group = "Authentication", Order = 13, Collapsed = true)]
    public string? SshPassphrase { get; set; }

    /// <summary>
    /// Whether to automatically fetch on library start-up.
    /// </summary>
    [ConfigField(Label = "Auto-fetch on Start", Group = "Sync", Order = 20)]
    public bool AutoFetch { get; set; } = false;

    /// <summary>
    /// How often (in minutes) to fetch in the background while the library runs. 0 disables the
    /// timer. Independent of <see cref="AutoFetch"/>, which fetches once at start-up.
    /// </summary>
    [ConfigField(Label = "Auto-fetch Interval (min)", Min = 0,
        Description = "Background fetch interval. 0 disables.",
        Group = "Sync", Order = 21)]
    public int AutoFetchIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Timeout in seconds for this repository's network operations (clone, fetch, the fetch step
    /// of pull, push). 0 uses <see cref="GitHelperConfig.DefaultTimeoutSeconds"/>. A transfer that
    /// runs longer is aborted at its next progress callback and returns <c>git.timeout</c>.
    /// </summary>
    [Range(0, 3600)]
    [ConfigField(Label = "Timeout (s)", Min = 0, Max = 3600,
        Description = "Network operation timeout. 0 = library default.",
        Group = "Sync", Order = 22, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Validates that at minimum a URL and local path are set.
    /// </summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(RepositoryUrl) &&
        !string.IsNullOrWhiteSpace(LocalPath);
}

/// <summary>
/// Root configuration for <c>CL.GitHelper</c>.
/// Serialized to / from <c>config.githelper.json</c> in the library's config directory.
/// </summary>
[ConfigSection("githelper")]
public sealed class GitHelperConfig : ConfigModelBase
{
    /// <summary>
    /// Whether the library is active. When false, all operations are no-ops.
    /// </summary>
    [ConfigField(Label = "Enabled", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base directory for resolving relative <see cref="RepositoryConfiguration.LocalPath"/> values.
    /// Defaults to a <c>repositories</c> sub-folder inside the library's data directory.
    /// Leave empty to resolve paths relative to the process working directory.
    /// </summary>
    [ConfigField(Label = "Base Directory",
        Description = "Root folder for cloned repos. Blank = library data directory.",
        Group = "General", Order = 1)]
    public string BaseDirectory { get; set; } = "";

    /// <summary>
    /// List of configured repositories. At least one must be present.
    /// </summary>
    public List<RepositoryConfiguration> Repositories { get; set; } =
    [
        new RepositoryConfiguration
        {
            Id = "Default",
            Name = "My Repository",
            RepositoryUrl = "https://github.com/username/repository.git",
            LocalPath = "my-repo",
            DefaultBranch = "main"
        }
    ];

    /// <summary>
    /// Network operation timeout for repositories whose <see cref="RepositoryConfiguration.TimeoutSeconds"/> is 0.
    /// </summary>
    [Range(1, 3600)]
    [ConfigField(Label = "Default Timeout (s)", Min = 1, Max = 3600, Group = "Defaults", Order = 10)]
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of repositories operated on concurrently during batch calls.
    /// </summary>
    [Range(1, 20)]
    [ConfigField(Label = "Max Concurrent Ops", Min = 1, Max = 20,
        Description = "Parallelism limit for batch operations.",
        Group = "Defaults", Order = 11)]
    public int MaxConcurrentOperations { get; set; } = 3;

    /// <summary>
    /// Whether to keep <c>GitRepository</c> instances in a memory cache between calls.
    /// </summary>
    [ConfigField(Label = "Enable Repository Cache", Group = "Cache", Order = 20, Collapsed = true)]
    public bool EnableRepositoryCaching { get; set; } = true;

    /// <summary>
    /// How long (minutes) a cached repository entry lives before being evicted. 0 = never expire.
    /// </summary>
    [Range(0, 1440)]
    [ConfigField(Label = "Cache TTL (min)", Min = 0, Max = 1440,
        Description = "0 disables eviction.", Group = "Cache", Order = 21, Collapsed = true)]
    public int CacheTimeoutMinutes { get; set; } = 30;

    /// <summary>Returns the <see cref="RepositoryConfiguration"/> with the given ID, or null.</summary>
    public RepositoryConfiguration? GetRepository(string id) =>
        Repositories.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Repositories == null || Repositories.Count == 0)
            errors.Add("At least one repository must be configured.");
        else
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var repository in Repositories)
            {
                if (repository is null) { errors.Add("Repository entries must not be null."); continue; }
                if (string.IsNullOrWhiteSpace(repository.Id))
                    errors.Add("Every repository needs an Id.");
                else if (!ids.Add(repository.Id))
                    errors.Add($"Repository Id '{repository.Id}' is used more than once.");
                if (repository.TimeoutSeconds is < 0 or > 3600)
                    errors.Add($"Repository '{repository.Id}': TimeoutSeconds must be between 0 and 3600.");
                if (repository.AutoFetchIntervalMinutes < 0)
                    errors.Add($"Repository '{repository.Id}': AutoFetchIntervalMinutes must not be negative.");
            }
        }

        if (DefaultTimeoutSeconds is < 1 or > 3600)
            errors.Add("DefaultTimeoutSeconds must be between 1 and 3600.");

        if (MaxConcurrentOperations is < 1 or > 20)
            errors.Add("MaxConcurrentOperations must be between 1 and 20.");

        if (CacheTimeoutMinutes is < 0 or > 1440)
            errors.Add("CacheTimeoutMinutes must be between 0 and 1440.");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}
