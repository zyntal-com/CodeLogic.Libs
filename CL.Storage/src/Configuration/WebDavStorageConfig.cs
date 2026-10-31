using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how a WebDAV connection authenticates HTTP requests.</summary>
public enum WebDavAuthenticationMode
{
    /// <summary>Sends no authentication credentials.</summary>
    None = 0,
    /// <summary>Uses HTTP Basic authentication.</summary>
    Basic = 1,
    /// <summary>Uses an HTTP bearer token.</summary>
    BearerToken = 2,
    /// <summary>Uses the current Windows credentials.</summary>
    Windows = 3,
    /// <summary>Uses HTTP Digest authentication with <see cref="WebDavConnectionConfig.Username"/> and <see cref="WebDavConnectionConfig.Password"/>.</summary>
    Digest = 4,
    /// <summary>Uses NTLM authentication with explicit credentials.</summary>
    Ntlm = 5,
    /// <summary>Uses Negotiate (Kerberos, falling back to NTLM) with explicit credentials.</summary>
    Negotiate = 6
}

/// <summary>Defines named WebDAV connections.</summary>
[ConfigSection("storage.webdav")]
public sealed class WebDavStorageConfig : ProviderStorageConfigBase<WebDavConnectionConfig> { }

/// <summary>Defines one WebDAV endpoint connection.</summary>
public sealed class WebDavConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the absolute WebDAV endpoint URL.</summary>
    [ConfigField(Label = "Endpoint", Required = true, InputType = ConfigInputType.Url, Group = "Connection", Order = 10)]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote path mounted as the connection root.</summary>
    [ConfigField(Label = "Root", Group = "Connection", Order = 11)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP authentication mode.</summary>
    public WebDavAuthenticationMode AuthenticationMode { get; set; } = WebDavAuthenticationMode.Basic;

    /// <summary>Gets or sets the Basic authentication username.</summary>
    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string? Username { get; set; }

    /// <summary>Gets or sets the Basic authentication password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    /// <summary>Gets or sets the bearer token.</summary>
    [ConfigField(Label = "Bearer token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? BearerToken { get; set; }

    /// <summary>Gets or sets additional safe HTTP request headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];
    /// <summary>Allows clear-text HTTP only when explicitly enabled for a deliberate endpoint.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Optional SHA-256 fingerprints of trusted TLS leaf certificates.</summary>
    public List<string> TrustedCertificateSha256 { get; set; } = [];

    /// <summary>Optional SHA-256 fingerprints of trusted server public keys (SPKI); these survive certificate renewals that keep the key.</summary>
    public List<string> TrustedPublicKeySha256 { get; set; } = [];

    /// <summary>Gets or sets whether a pinned certificate must also pass normal chain validation.</summary>
    public bool RequireValidCertificateChain { get; set; }

    /// <summary>Gets or sets the optional client-certificate path (PFX/PKCS#12) for mutual TLS.</summary>
    [ConfigField(Label = "Client certificate", Group = "TLS", Order = 30)]
    public string? ClientCertificatePath { get; set; }

    /// <summary>Gets or sets the optional client-certificate password.</summary>
    [ConfigField(Label = "Client certificate password", Secret = true, InputType = ConfigInputType.Password, Group = "TLS", Order = 31)]
    public string? ClientCertificatePassword { get; set; }

    /// <summary>
    /// Gets or sets the client certificate (PFX/PKCS#12) as bytes — base64 in JSON — for credentials kept in
    /// a secret store rather than on disk. Mutually exclusive with <c>ClientCertificatePath</c>; decrypted
    /// with <c>ClientCertificatePassword</c>.
    /// </summary>
    [ConfigField(Label = "Client certificate content", Secret = true, Group = "TLS", Order = 32)]
    public byte[]? ClientCertificateContent { get; set; }

    /// <summary>Gets or sets the maximum number of concurrent HTTP connections to the server.</summary>
    public int? MaxConnectionsPerServer { get; set; }

    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets automatic retry of transient failures.</summary>
    public StorageRetryConfig Retry { get; set; } = new();

    /// <summary>Gets or sets an optional HTTP or SOCKS proxy for this connection.</summary>
    public StorageProxyConfig Proxy { get; set; } = new();

    /// <inheritdoc />
    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        foreach (var error in (Proxy ?? new StorageProxyConfig()).GetValidationErrors("Proxy."))
            yield return error;
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            yield return "Endpoint must be an absolute HTTP(S) URL";
        else
        {
            if (endpoint.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
                yield return "HTTP endpoints require AllowInsecureHttp=true";
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                yield return "Endpoint cannot contain user info, a query string, or a fragment";
        }
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        foreach (var error in (Retry ?? new StorageRetryConfig()).GetValidationErrors("Retry."))
            yield return error;
        if (AuthenticationMode == WebDavAuthenticationMode.Basic && string.IsNullOrWhiteSpace(Username))
            yield return "Basic authentication requires Username";
        if (AuthenticationMode == WebDavAuthenticationMode.BearerToken && string.IsNullOrWhiteSpace(BearerToken))
            yield return "BearerToken authentication requires BearerToken";
        foreach (var fingerprint in TrustedCertificateSha256 ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Trusted certificate fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
        foreach (var (name, value) in Headers ?? [])
            if (string.IsNullOrWhiteSpace(name) || value is null || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                yield return "Custom HTTP headers cannot be blank or contain line breaks";
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                yield return $"Custom HTTP header '{name}' is managed by the transport or authentication mode";
        if (AuthenticationMode is WebDavAuthenticationMode.Digest or WebDavAuthenticationMode.Ntlm or WebDavAuthenticationMode.Negotiate &&
            (string.IsNullOrWhiteSpace(Username) || Password is null))
            yield return $"{AuthenticationMode} authentication requires Username and Password";
        if (!string.IsNullOrWhiteSpace(ClientCertificatePath) && !System.IO.Path.IsPathFullyQualified(ClientCertificatePath))
            yield return "ClientCertificatePath must be an absolute path";
        if (!string.IsNullOrWhiteSpace(ClientCertificatePath) && ClientCertificateContent is { Length: > 0 })
            yield return "Set ClientCertificatePath or ClientCertificateContent, not both";
        foreach (var fingerprint in TrustedPublicKeySha256 ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Trusted public-key fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
        if (MaxConnectionsPerServer is < 1 or > 1024)
            yield return "MaxConnectionsPerServer must be between 1 and 1024";
    }
}
