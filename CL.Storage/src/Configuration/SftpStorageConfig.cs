using CL.Storage.Abstractions;
using CL.Storage.Providers.Sftp;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an SFTP connection authenticates the client.</summary>
public enum SftpAuthenticationMode
{
    /// <summary>Authenticates with a password.</summary>
    Password = 0,
    /// <summary>Authenticates with a private key and optional passphrase.</summary>
    PrivateKey = 1,
    /// <summary>Answers the server's keyboard-interactive password prompt with <see cref="SftpConnectionConfig.Password"/>.</summary>
    KeyboardInteractive = 2,
    /// <summary>
    /// Offers every configured method in the order OpenSSH uses: private keys, then password, then
    /// keyboard-interactive. Also satisfies servers that require several methods, such as key and password.
    /// </summary>
    Auto = 3
}

/// <summary>Defines named SFTP connections.</summary>
[ConfigSection("storage.sftp")]
public sealed class SftpStorageConfig : ProviderStorageConfigBase<SftpConnectionConfig> { }

/// <summary>Defines one SSH File Transfer Protocol connection.</summary>
public sealed class SftpConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the SSH server host.</summary>
    [ConfigField(Label = "Host", Required = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the SSH server port.</summary>
    [ConfigField(Label = "Port", Group = "Connection", Order = 11)]
    public int Port { get; set; } = 22;

    /// <summary>Gets or sets the remote directory mounted as the connection root.</summary>
    [ConfigField(Label = "Root", Group = "Connection", Order = 12)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the login username.</summary>
    [ConfigField(Label = "Username", Required = true, Group = "Credentials", Order = 20)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the client authentication mode.</summary>
    public SftpAuthenticationMode AuthenticationMode { get; set; } = SftpAuthenticationMode.Password;

    /// <summary>Gets or sets the login password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    /// <summary>Gets or sets the absolute private-key file path.</summary>
    [ConfigField(Label = "Private key", Group = "Credentials", Order = 22)]
    public string? PrivateKeyPath { get; set; }

    /// <summary>Gets or sets a private key supplied as text (OpenSSH or PEM format), for keys kept in a secret store.</summary>
    [ConfigField(Label = "Private key (inline)", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 24)]
    public string? PrivateKeyContent { get; set; }

    /// <summary>Gets or sets further absolute private-key paths offered after the primary key.</summary>
    /// <remarks>All keys share <see cref="PrivateKeyPassphrase"/>.</remarks>
    public List<string> AdditionalPrivateKeyPaths { get; set; } = [];

    /// <summary>Gets or sets the private-key passphrase.</summary>
    [ConfigField(Label = "Private key passphrase", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>
    /// Gets or sets whether to trust any SSH server host key. This disables host-key verification and
    /// should only be used for trusted development environments or servers whose key cannot be pinned.
    /// </summary>
    [ConfigField(Label = "Auto-accept host key", Description = "Trust any SSH server host key. This disables host-key verification.", Group = "Connection", Order = 13)]
    public bool AutoAcceptHostKey { get; set; }

    /// <summary>Trusted server host-key fingerprints, in SHA256:base64 or hexadecimal form.</summary>
    public List<string> HostKeyFingerprints { get; set; } = [];

    /// <summary>Gets or sets an OpenSSH <c>known_hosts</c> file whose entries are trusted, in addition to <see cref="HostKeyFingerprints"/>.</summary>
    /// <remarks>Plain, hashed (<c>|1|</c>), wildcard, and <c>[host]:port</c> entries are supported; <c>@revoked</c> keys are always rejected.</remarks>
    public string? KnownHostsPath { get; set; }

    /// <summary>Gets or sets allowed key-exchange algorithms in preference order; empty keeps the library defaults.</summary>
    public List<string> KeyExchangeAlgorithms { get; set; } = [];

    /// <summary>Gets or sets allowed ciphers in preference order; empty keeps the library defaults.</summary>
    public List<string> Ciphers { get; set; } = [];

    /// <summary>Gets or sets allowed MAC algorithms in preference order; empty keeps the library defaults.</summary>
    public List<string> MacAlgorithms { get; set; } = [];

    /// <summary>Gets or sets allowed host-key algorithms in preference order; empty keeps the library defaults.</summary>
    public List<string> HostKeyAlgorithms { get; set; } = [];

    /// <summary>Gets or sets the character encoding of remote file names, such as <c>utf-8</c> or <c>windows-1252</c>.</summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>Gets or sets the SFTP read/write buffer size in bytes; larger buffers help on high-latency links.</summary>
    public int? BufferSize { get; set; }

    /// <summary>Gets or sets an optional SSH bastion the connection is tunnelled through.</summary>
    public SftpJumpHostConfig? JumpHost { get; set; }

    /// <summary>Gets or sets the operation timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets session pooling, keep-alive, and concurrency limits.</summary>
    public StorageSessionConfig Session { get; set; } = new();

    /// <summary>Gets or sets automatic retry of transient failures.</summary>
    public StorageRetryConfig Retry { get; set; } = new();

    /// <summary>
    /// Gets or sets whether raw commands may be sent through <see cref="Abstractions.IStorageCommandService"/>.
    /// Off by default: commands are not confined to <c>Root</c> and can do anything the account may do.
    /// </summary>
    public bool AllowRawCommands { get; set; }

    /// <summary>Gets or sets an optional HTTP or SOCKS proxy for this connection.</summary>
    public StorageProxyConfig Proxy { get; set; } = new();

    /// <inheritdoc />
    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        foreach (var error in (Proxy ?? new StorageProxyConfig()).GetValidationErrors("Proxy."))
            yield return error;
        if (string.IsNullOrWhiteSpace(Host))
            yield return "Host is required";
        if (Port is < 1 or > 65535)
            yield return "Port must be between 1 and 65535";
        if (string.IsNullOrWhiteSpace(Username))
            yield return "Username is required";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        foreach (var error in (Session ?? new StorageSessionConfig()).GetValidationErrors("Session."))
            yield return error;
        foreach (var error in (Retry ?? new StorageRetryConfig()).GetValidationErrors("Retry."))
            yield return error;
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        var hasKey = !string.IsNullOrWhiteSpace(PrivateKeyPath) || !string.IsNullOrWhiteSpace(PrivateKeyContent) ||
            AdditionalPrivateKeyPaths is { Count: > 0 };
        switch (AuthenticationMode)
        {
            case SftpAuthenticationMode.Password or SftpAuthenticationMode.KeyboardInteractive when Password is null:
                yield return $"{AuthenticationMode} authentication requires Password";
                break;
            case SftpAuthenticationMode.PrivateKey when !hasKey:
                yield return "Private-key authentication requires PrivateKeyPath or PrivateKeyContent";
                break;
            case SftpAuthenticationMode.Auto when !hasKey && Password is null:
                yield return "Auto authentication requires a private key or Password";
                break;
        }
        foreach (var path in new[] { PrivateKeyPath }.Concat(AdditionalPrivateKeyPaths ?? []))
        {
            if (!string.IsNullOrWhiteSpace(path) && !System.IO.Path.IsPathFullyQualified(path))
                yield return $"Private-key path '{path}' must be an absolute path";
        }
        foreach (var error in SshHostTrust.GetValidationErrors(AutoAcceptHostKey, HostKeyFingerprints, KnownHostsPath, string.Empty))
            yield return error;
        foreach (var error in SshAlgorithmNames.GetValidationErrors(KeyExchangeAlgorithms, Ciphers, MacAlgorithms, HostKeyAlgorithms))
            yield return error;
        if (!Providers.StorageEncodings.IsKnown(Encoding))
            yield return $"Encoding '{Encoding}' is not a supported character encoding";
        if (BufferSize is < 1024 or > 4 * 1024 * 1024)
            yield return "BufferSize must be between 1024 and 4194304 bytes";
        if (JumpHost is not null)
        {
            foreach (var error in JumpHost.GetValidationErrors())
                yield return $"JumpHost.{error}";
        }
    }

}

/// <summary>Defines an SSH bastion ("jump host") an SFTP connection is tunnelled through.</summary>
/// <remarks>
/// The client opens an SSH session to the jump host, forwards a local port to the target
/// <see cref="SftpConnectionConfig.Host"/>, and runs SFTP through that tunnel. The target's host key is
/// still verified against the target's own settings. A configured proxy applies to the jump host.
/// </remarks>
public sealed class SftpJumpHostConfig
{
    /// <summary>Gets or sets the jump host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the jump host SSH port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>Gets or sets the jump host login.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the jump host password, when password authentication is used.</summary>
    [ConfigField(Label = "Jump host password", Secret = true, InputType = ConfigInputType.Password)]
    public string? Password { get; set; }

    /// <summary>Gets or sets an absolute private-key path for the jump host.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Gets or sets a private key for the jump host supplied as text.</summary>
    [ConfigField(Label = "Jump host private key (inline)", Secret = true, InputType = ConfigInputType.Password)]
    public string? PrivateKeyContent { get; set; }

    /// <summary>Gets or sets the jump host private-key passphrase.</summary>
    [ConfigField(Label = "Jump host private key passphrase", Secret = true, InputType = ConfigInputType.Password)]
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Gets or sets whether to trust any jump host key; development use only.</summary>
    public bool AutoAcceptHostKey { get; set; }

    /// <summary>Gets or sets trusted jump host key fingerprints (SHA256:base64 or hexadecimal).</summary>
    public List<string> HostKeyFingerprints { get; set; } = [];

    /// <summary>Gets or sets an OpenSSH <c>known_hosts</c> file used to verify the jump host.</summary>
    public string? KnownHostsPath { get; set; }

    internal IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Host))
            yield return "Host is required";
        if (Port is < 1 or > 65535)
            yield return "Port must be between 1 and 65535";
        if (string.IsNullOrWhiteSpace(Username))
            yield return "Username is required";
        if (Password is null && string.IsNullOrWhiteSpace(PrivateKeyPath) && string.IsNullOrWhiteSpace(PrivateKeyContent))
            yield return "A Password, PrivateKeyPath, or PrivateKeyContent is required";
        if (!string.IsNullOrWhiteSpace(PrivateKeyPath) && !System.IO.Path.IsPathFullyQualified(PrivateKeyPath))
            yield return "PrivateKeyPath must be an absolute path";
        foreach (var error in SshHostTrust.GetValidationErrors(AutoAcceptHostKey, HostKeyFingerprints, KnownHostsPath, string.Empty))
            yield return error;
    }
}
