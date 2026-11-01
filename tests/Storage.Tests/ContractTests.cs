using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StoragePathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("//alpha///./beta/", "alpha/beta")]
    [InlineData(@"alpha\.\beta", "alpha/beta")]
    public void Normalize_returns_a_relative_slash_separated_path(string input, string expected)
    {
        var result = StoragePath.Normalize(input);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("alpha/../secret")]
    [InlineData(@"alpha\..\secret")]
    [InlineData("alpha\0secret")]
    public void Normalize_rejects_paths_that_can_escape_or_are_malformed(string input)
    {
        var result = StoragePath.Normalize(input);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.invalid_path", result.Error!.Code);
    }
}

public sealed class StorageModelTests
{
    [Fact]
    public void Models_defensively_copy_read_only_collections()
    {
        var metadata = new Dictionary<string, string> { ["color"] = "blue" };
        var item = new StorageItem
        {
            Path = "a.txt",
            Name = "a.txt",
            ItemType = StorageItemType.File,
            Metadata = metadata
        };
        var inputItems = new List<StorageItem> { item };
        var page = new StoragePage(inputItems, "opaque-token");

        metadata["color"] = "red";
        inputItems.Clear();

        Assert.Equal("blue", item.Metadata["color"]);
        Assert.Single(page.Items);
        Assert.Equal("opaque-token", page.ContinuationToken);
    }

    [Fact]
    public void Provider_and_item_enums_expose_the_neutral_contract()
    {
        Assert.Equal(
            new[] { "Local", "S3", "Ftp", "Sftp", "WebDav", "AzureBlob", "GoogleCloudStorage", "OpenStackSwift" },
            Enum.GetNames<StorageProvider>());
        Assert.Equal(new[] { "File", "Directory", "Link" }, Enum.GetNames<StorageItemType>());
    }

    [Fact]
    public void Capabilities_expose_granular_features_limits_and_legacy_projections()
    {
        var capabilities = new StorageCapabilities(
            StorageFeature.VirtualDirectories |
            StorageFeature.FileCopy |
            StorageFeature.ServerSideCopy |
            StorageFeature.RangeReads |
            StorageFeature.MetadataRead |
            StorageFeature.ServerPagination |
            StorageFeature.SignedReadUrls,
            new StorageLimits { MaxPageSize = 5_000, MaxObjectBytes = 10_000_000 });

        Assert.True(capabilities.Supports(StorageFeature.VirtualDirectories));
        Assert.True(capabilities.Supports(StorageFeature.SignedReadUrls));
        Assert.False(capabilities.Supports(StorageFeature.MetadataWrite));
        Assert.True(capabilities.Directories);
        Assert.True(capabilities.NativeCopy);
        Assert.False(capabilities.NativeMove);
        Assert.True(capabilities.RangeReads);
        Assert.True(capabilities.Metadata);
        Assert.True(capabilities.ServerPagination);
        Assert.Equal(5_000, capabilities.Limits.MaxPageSize);
        Assert.Equal(10_000_000, capabilities.Limits.MaxObjectBytes);
    }
}

public sealed class StorageOptionsTests
{
    [Fact]
    public void Options_have_safe_documented_defaults()
    {
        Assert.Equal(1000, new StorageListOptions().PageSize);
        Assert.True(new StorageUploadOptions().Overwrite);
        Assert.True(new StorageUploadOptions().CreateParents);
        Assert.True(new StorageTransferOptions().Overwrite);
        Assert.True(new StorageTransferOptions().CreateParents);
        Assert.Null(new StorageDownloadOptions().MaxBufferedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void List_options_reject_non_positive_page_sizes(int pageSize)
    {
        var result = new StorageListOptions { PageSize = pageSize }.Validate();

        Assert.True(result.IsFailure);
        Assert.Equal("storage.invalid_path", result.Error!.Code);
    }

    [Fact]
    public void Download_options_reject_negative_or_overflowing_ranges_and_non_positive_limits()
    {
        AssertInvalid(new StorageDownloadOptions { Offset = -1 });
        AssertInvalid(new StorageDownloadOptions { Length = 0 });
        AssertInvalid(new StorageDownloadOptions { MaxBufferedBytes = 0 });
        AssertInvalid(new StorageDownloadOptions { Offset = long.MaxValue, Length = 1 });

        static void AssertInvalid(StorageDownloadOptions options)
        {
            var result = options.Validate();
            Assert.True(result.IsFailure);
            Assert.Equal("storage.invalid_path", result.Error!.Code);
        }
    }
}

public sealed class StorageErrorTests
{
    [Fact]
    public void Error_factories_use_stable_codes()
    {
        Error[] errors =
        [
            StorageErrors.InvalidPath("bad"),
            StorageErrors.InvalidContent("bad content"),
            StorageErrors.NotFound("missing"),
            StorageErrors.Unauthorized("denied"),
            StorageErrors.Timeout("slow"),
            StorageErrors.Conflict("exists"),
            StorageErrors.Unavailable("offline"),
            StorageErrors.Unsupported("nope"),
            StorageErrors.TooLarge("large"),
            StorageErrors.PartialFailure("partial"),
            StorageErrors.ProviderError("failed"),
            StorageErrors.AuthenticationFailed("auth"),
            StorageErrors.PermissionDenied("denied"),
            StorageErrors.TlsFailure("tls"),
            StorageErrors.HostKeyRejected("host key"),
            StorageErrors.ConnectionFailed("connect"),
            StorageErrors.ConnectionLost("lost"),
            StorageErrors.ServerBusy("busy"),
            StorageErrors.QuotaExceeded("quota")
        ];

        Assert.Equal(
            new[]
            {
                "storage.invalid_path", "storage.invalid_content", "storage.not_found", "storage.unauthorized", "storage.timeout",
                "storage.conflict", "storage.unavailable", "storage.unsupported", "storage.too_large",
                "storage.partial_failure", "storage.provider_error", "storage.authentication_failed",
                "storage.permission_denied", "storage.tls_failure", "storage.host_key_rejected", "storage.connection_failed",
                "storage.connection_lost", "storage.server_busy", "storage.quota_exceeded"
            },
            errors.Select(error => error.Code));
    }
}

public sealed class StorageConfigurationTests
{
    [Fact]
    public void Config_defaults_match_the_library_contract()
    {
        var storage = new StorageConfig();
        var local = new LocalConnectionConfig();

        Assert.True(storage.Enabled);
        Assert.Equal("Default", storage.DefaultConnection);
        Assert.Equal(10, storage.HealthCheckTimeoutSeconds);
        Assert.Equal(67_108_864, storage.MaxBufferedDownloadBytes);
        Assert.True(local.Enabled);
        Assert.Equal(string.Empty, local.RootPath);
        Assert.False(local.FollowLinks);
    }

    [Fact]
    public void Config_validation_rejects_invalid_sizes_and_required_local_root()
    {
        Assert.False(new StorageConfig { HealthCheckTimeoutSeconds = 0 }.Validate().IsValid);
        Assert.False(new StorageConfig { MaxBufferedDownloadBytes = 0 }.Validate().IsValid);
        Assert.False(new LocalConnectionConfig().Validate().IsValid);
        Assert.False(new LocalConnectionConfig { RootPath = "root" }.Validate().IsValid);
        Assert.False(new LocalConnectionConfig { RootPath = "./data" }.Validate().IsValid);
        Assert.True(new LocalConnectionConfig { RootPath = Path.GetTempPath() }.Validate().IsValid);
        Assert.True(new LocalConnectionConfig { RootPath = "root", Enabled = false }.Validate().IsValid);
        Assert.False(new LocalStorageConfig
        {
            Connections = new Dictionary<string, LocalConnectionConfig>
            {
                ["Default"] = new() { RootPath = "" }
            }
        }.Validate().IsValid);
    }

    [Fact]
    public void Valid_local_configuration_passes()
    {
        var config = new LocalStorageConfig
        {
            Connections = new Dictionary<string, LocalConnectionConfig>
            {
                ["Default"] = new() { RootPath = Path.GetTempPath() }
            }
        };

        Assert.True(config.Validate().IsValid);
    }

    [Fact]
    public void Configuration_validation_handles_explicit_json_null_collections()
    {
        Assert.False(new LocalStorageConfig { Connections = null! }.Validate().IsValid);
        Assert.False(new S3StorageConfig { Connections = null! }.Validate().IsValid);
        Assert.False(new SftpConnectionConfig
        {
            Host = "sftp.example.test",
            Username = "user",
            Password = "password",
            HostKeyFingerprints = null!
        }.Validate().IsValid);
        Assert.False(new WebDavConnectionConfig
        {
            Endpoint = "https://dav.example.test",
            AuthenticationMode = WebDavAuthenticationMode.None,
            Headers = new Dictionary<string, string> { ["Authorization"] = "secret" }
        }.Validate().IsValid);
    }

    [Fact]
    public void Sftp_requires_a_pinned_sha256_host_key_unless_auto_accept_is_enabled()
    {
        var withoutPin = new SftpConnectionConfig
        {
            Host = "sftp.example.test",
            Username = "user",
            Password = "password"
        };
        var withPin = new SftpConnectionConfig
        {
            Host = "sftp.example.test",
            Username = "user",
            Password = "password",
            HostKeyFingerprints = [new string('A', 64)]
        };
        var withAutoAccept = new SftpConnectionConfig
        {
            Host = "sftp.example.test",
            Username = "user",
            Password = "password",
            AutoAcceptHostKey = true
        };

        Assert.False(withoutPin.Validate().IsValid);
        Assert.True(withPin.Validate().IsValid);
        Assert.True(withAutoAccept.Validate().IsValid);
    }

    [Fact]
    public void WebDav_requires_https_unless_clear_text_is_explicit_and_validates_pins()
    {
        var insecureByDefault = new WebDavConnectionConfig
        {
            Endpoint = "http://localhost:8080/dav",
            AuthenticationMode = WebDavAuthenticationMode.None
        };
        var explicitInsecure = new WebDavConnectionConfig
        {
            Endpoint = "http://localhost:8080/dav",
            AuthenticationMode = WebDavAuthenticationMode.None,
            AllowInsecureHttp = true
        };
        var invalidPin = new WebDavConnectionConfig
        {
            Endpoint = "https://dav.example.test/",
            AuthenticationMode = WebDavAuthenticationMode.Windows,
            TrustedCertificateSha256 = ["not-a-sha256-pin"]
        };

        Assert.False(insecureByDefault.Validate().IsValid);
        Assert.True(explicitInsecure.Validate().IsValid);
        Assert.False(invalidPin.Validate().IsValid);
    }

    [Fact]
    public void Ftp_certificate_pins_require_tls()
    {
        var config = new FtpConnectionConfig
        {
            Host = "ftp.example.test",
            EncryptionMode = StorageFtpEncryptionMode.None,
            TrustedCertificateSha256 = [new string('B', 64)]
        };

        Assert.False(config.Validate().IsValid);
    }

    [Fact]
    public void S3_custom_endpoints_require_https_unless_clear_text_is_explicit()
    {
        var insecureByDefault = new S3ConnectionConfig
        {
            Bucket = "bucket",
            ServiceUrl = "http://localhost:9000"
        };
        var explicitInsecure = new S3ConnectionConfig
        {
            Bucket = "bucket",
            ServiceUrl = "http://localhost:9000",
            AllowInsecureHttp = true
        };
        var embeddedQuery = new S3ConnectionConfig
        {
            Bucket = "bucket",
            ServiceUrl = "https://s3.example.test?secret=value"
        };

        Assert.False(insecureByDefault.Validate().IsValid);
        Assert.True(explicitInsecure.Validate().IsValid);
        Assert.False(embeddedQuery.Validate().IsValid);
    }
}

public sealed class CertificateFingerprintTests
{
    private const string Hex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Base64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Theory]
    [InlineData(Hex)]
    [InlineData("00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F")]
    [InlineData(Base64)]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("SHA256:" + Base64)]
    [InlineData("SHA256:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    public void Sha256_fingerprint_normalization_accepts_canonical_hex_and_base64(string value)
    {
        Assert.True(CertificateFingerprint.TryNormalizeSha256(value, out var normalized));
        Assert.Equal(Hex, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SHA256:")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8==")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwd Hh8")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh*")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E")]
    public void Sha256_fingerprint_normalization_rejects_malformed_or_noncanonical_values(string value)
    {
        Assert.False(CertificateFingerprint.TryNormalizeSha256(value, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }
}

public sealed class NativeConnectionLeaseTests
{
    [Fact]
    public async Task DisposeAsync_releases_the_client_exactly_once()
    {
        var releases = 0;
        var client = new object();
        var lease = new NativeConnectionLease<object>(client, _ =>
        {
            Interlocked.Increment(ref releases);
            return ValueTask.CompletedTask;
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lease.DisposeAsync().AsTask()));

        Assert.Same(client, lease.Client);
        Assert.Equal(1, releases);
    }
}

public sealed class AdvancedCapabilityContractTests
{
    [Fact]
    public void Version_capable_provider_types_expose_the_version_contract()
    {
        Assert.True(typeof(IStorageVersionService).IsAssignableFrom(
            typeof(CL.Storage.Providers.S3.S3StorageBackend)));
        Assert.True(typeof(IStorageVersionService).IsAssignableFrom(
            typeof(CL.Storage.Providers.Azure.AzureBlobStorageBackend)));
        Assert.True(typeof(IStorageVersionService).IsAssignableFrom(
            typeof(CL.Storage.Providers.GoogleCloud.GoogleCloudStorageBackend)));
    }

    [Fact]
    public void WebDav_metadata_read_capability_has_a_callable_optional_contract()
    {
        Assert.True(typeof(IStorageMetadataService).IsAssignableFrom(
            typeof(CL.Storage.Providers.WebDav.WebDavStorageBackend)));
    }

    [Fact]
    public void Tag_capable_provider_types_expose_the_tag_contract()
    {
        Assert.True(typeof(IStorageTagService).IsAssignableFrom(
            typeof(CL.Storage.Providers.S3.S3StorageBackend)));
        Assert.True(typeof(IStorageTagService).IsAssignableFrom(
            typeof(CL.Storage.Providers.Azure.AzureBlobStorageBackend)));
    }
}
