using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an S3 connection obtains AWS-compatible credentials.</summary>
public enum S3AuthenticationMode
{
    /// <summary>Uses the SDK default credential chain.</summary>
    DefaultCredentialChain = 0,
    /// <summary>Uses the access key, secret key, and optional session token in configuration.</summary>
    StaticCredentials = 1
}

/// <summary>Says whether an S3-compatible server enforces the conditions sent on writes, copies, and deletes.</summary>
public enum S3ConditionalRequestSupport
{
    /// <summary>
    /// Whether the server enforces <c>If-None-Match</c> and <c>If-Match</c> on <c>PutObject</c> (and so on
    /// <c>CompleteMultipartUpload</c>) and on <c>CopyObject</c>, and <c>If-Match</c> on <c>DeleteObject</c>, is probed
    /// once per connection, the first time a conditional write, copy, or delete needs to know. A condition the probe
    /// finds enforced is sent with the request that commits; one it finds ignored or rejected (400/501) is not sent
    /// and is checked immediately before the commit instead (reported as
    /// <see cref="CL.Storage.Models.StorageConditionEnforcement.CheckedBeforeCommit"/>). A probe that cannot finish
    /// (a network error, missing permissions) assumes nothing is enforced and is tried again after a back-off.
    /// <para>
    /// The probe writes, overwrites, copies, and deletes two one-byte <c>.cl-storage-probe-*</c> objects under the
    /// prefix, so it needs <c>PutObject</c> and <c>DeleteObject</c> permission there. Its requests are ordinary writes:
    /// on a versioned bucket they leave noncurrent versions and delete markers behind (and on an Object Lock bucket
    /// versions that cannot be removed until their retention ends), and they trigger event notifications, replication,
    /// and access logging like any other write. Choose <see cref="Enforced"/> or <see cref="NotEnforced"/> to avoid it.
    /// </para>
    /// </summary>
    Auto = 0,
    /// <summary>The server enforces every condition in the request that commits; nothing is probed.</summary>
    Enforced = 1,
    /// <summary>
    /// The server does not enforce conditions: none are sent, the connection does not declare conditional
    /// capabilities, and every condition is checked immediately before the request that commits.
    /// </summary>
    NotEnforced = 2
}

/// <summary>Defines named Amazon S3 and S3-compatible connections.</summary>
[ConfigSection("storage.s3")]
public sealed class S3StorageConfig : ProviderStorageConfigBase<S3ConnectionConfig> { }

/// <summary>Defines one isolated Amazon S3 or S3-compatible bucket connection.</summary>
public sealed class S3ConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the bucket name.</summary>
    [ConfigField(Label = "Bucket", Required = true, Group = "Connection", Order = 10)]
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the key prefix mounted as the connection root.</summary>
    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional custom S3-compatible service URL.</summary>
    [ConfigField(Label = "Service URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? ServiceUrl { get; set; }

    /// <summary>Gets or sets the signing region.</summary>
    [ConfigField(Label = "Region", Group = "Connection", Order = 13)]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Gets or sets the credential acquisition mode.</summary>
    public S3AuthenticationMode AuthenticationMode { get; set; } = S3AuthenticationMode.DefaultCredentialChain;

    /// <summary>Gets or sets the static access key ID.</summary>
    [ConfigField(Label = "Access Key", Secret = true, Group = "Credentials", Order = 20)]
    public string? AccessKey { get; set; }

    /// <summary>Gets or sets the static secret access key.</summary>
    [ConfigField(Label = "Secret Key", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? SecretKey { get; set; }

    /// <summary>Gets or sets an optional temporary-credential session token.</summary>
    [ConfigField(Label = "Session Token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? SessionToken { get; set; }

    /// <summary>Gets or sets whether requests use path-style bucket addressing.</summary>
    public bool ForcePathStyle { get; set; }
    /// <summary>Allows a clear-text custom S3-compatible endpoint only when deliberately enabled.</summary>
    public bool AllowInsecureHttp { get; set; }
    /// <summary>Gets or sets whether payload signing is disabled for compatible services.</summary>
    public bool DisablePayloadSigning { get; set; }
    /// <summary>Gets or sets whether SDK default response checksum validation is disabled.</summary>
    public bool DisableDefaultChecksumValidation { get; set; }
    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Gets or sets the maximum SDK retry count.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Gets or sets the multipart upload part size in bytes.</summary>
    public int MultipartPartSizeBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>Gets or sets the size at which multipart upload begins.</summary>
    public long MultipartThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Gets or sets an optional HTTP or SOCKS proxy for this connection.</summary>
    public StorageProxyConfig Proxy { get; set; } = new();

    /// <summary>
    /// Gets or sets whether the server enforces request conditions; <see cref="S3ConditionalRequestSupport.Auto"/>
    /// by default. Set <see cref="S3ConditionalRequestSupport.NotEnforced"/> for an S3-compatible server that
    /// ignores or rejects <c>If-None-Match</c> and <c>If-Match</c>, so conditions are checked by the library instead.
    /// </summary>
    public S3ConditionalRequestSupport ConditionalRequests { get; set; } = S3ConditionalRequestSupport.Auto;

    /// <inheritdoc />
    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        foreach (var error in (Proxy ?? new StorageProxyConfig()).GetValidationErrors("Proxy."))
            yield return error;
        if (string.IsNullOrWhiteSpace(Bucket))
            yield return "Bucket is required";
        if (string.IsNullOrWhiteSpace(Region))
            yield return "Region is required";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (MaxRetries < 0)
            yield return "MaxRetries cannot be negative";
        if (MultipartPartSizeBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
            yield return "MultipartPartSizeBytes must be between 5 MiB and 512 MiB";
        if (MultipartThresholdBytes < MultipartPartSizeBytes)
            yield return "MultipartThresholdBytes must be at least MultipartPartSizeBytes";

        var prefix = StoragePath.Normalize(Prefix ?? string.Empty);
        if (prefix.IsFailure)
            yield return "Prefix is invalid";

        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                yield return "ServiceUrl must be an absolute HTTP(S) URL";
            }
            else
            {
                if (uri.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) &&
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    yield return "Cloudflare R2 endpoints require HTTPS";
                }
                else if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
                {
                    yield return "HTTP S3-compatible endpoints require AllowInsecureHttp=true";
                }

                // The AWS SDK refuses unsigned payloads on plain HTTP ("the request must be sent over HTTPS"),
                // so every upload would fail while reads keep working.
                if (uri.Scheme == Uri.UriSchemeHttp && DisablePayloadSigning)
                    yield return "DisablePayloadSigning requires an HTTPS ServiceUrl: the AWS SDK refuses unsigned payloads over HTTP";

                if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    yield return "ServiceUrl cannot contain user info, a query string, or a fragment";
            }
        }

        if (AuthenticationMode == S3AuthenticationMode.StaticCredentials &&
            (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)))
        {
            yield return "StaticCredentials requires AccessKey and SecretKey";
        }
    }
}
