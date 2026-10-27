using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Providers;
using CL.Storage.Providers.S3;
using CL.Storage.Registry;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// S3 failures say what went wrong: the SDK's own reason for refusing a request, the request id of a service
/// error, a configuration that can never upload, and a throttled log line per failure.
/// </summary>
public sealed class S3DiagnosticsTests
{
    [Fact]
    public void Client_errors_keep_the_sdk_reason()
    {
        var error = S3StorageBackend.Map(new AmazonClientException("When DisablePayloadSigning is true, the request must be sent over HTTPS."), "Upload S3 object");
        Assert.Equal(StorageErrors.ProviderErrorCode, error.Code);
        Assert.Contains("the request must be sent over HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Contains("exception=AmazonClientException", error.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_unknown_errors_stay_generic()
    {
        var error = S3StorageBackend.Map(new InvalidOperationException("C:\\secret\\path"), "Upload S3 object");
        Assert.Equal("Upload S3 object: S3 provider failed.", error.Message);
        Assert.DoesNotContain("secret", error.Message + error.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_errors_carry_the_request_id()
    {
        var s3 = new AmazonS3Exception("Access Denied", ErrorType.Sender, "AccessDenied", "REQ123;x=1", HttpStatusCode.Forbidden);
        var error = S3StorageBackend.Map(s3, "Upload S3 object");
        Assert.Equal(StorageErrors.PermissionDeniedCode, error.Code);
        Assert.Contains("requestId=REQ123x1", error.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed for https://bucket.s3.amazonaws.com/key?X-Amz-Signature=abc123&X-Amz-Credential=AKIAEXAMPLE", "Failed for https://bucket.s3.amazonaws.com/key?[redacted]")]
    [InlineData("Invalid key AKIAIOSFODNN7EXAMPLE in request", "Invalid key [redacted] in request")]
    [InlineData("password=hunter2, token: abc.def", "password=[redacted], token=[redacted]")]
    [InlineData("First line\nsecond line with secrets", "First line")]
    [InlineData("   ", "")]
    public void Provider_messages_are_sanitized(string message, string expected) =>
        Assert.Equal(expected, ProviderErrorMapper.SafeProviderMessage(message));

    [Fact]
    public void Provider_messages_are_capped() =>
        Assert.Equal(301, ProviderErrorMapper.SafeProviderMessage(new string('a', 1000)).Length);

    [Theory]
    [InlineData("http://s3:9000", true, false)]
    [InlineData("http://s3:9000", false, true)]
    [InlineData("https://account.r2.cloudflarestorage.com", true, true)]
    public void Unsigned_payloads_need_https(string serviceUrl, bool disablePayloadSigning, bool valid)
    {
        var config = new S3ConnectionConfig
        {
            Bucket = "b",
            ServiceUrl = serviceUrl,
            Region = "auto",
            AllowInsecureHttp = serviceUrl.StartsWith("http://", StringComparison.Ordinal),
            ForcePathStyle = true,
            AuthenticationMode = S3AuthenticationMode.StaticCredentials,
            AccessKey = "k",
            SecretKey = "s",
            DisablePayloadSigning = disablePayloadSigning
        };
        var result = config.Validate();
        Assert.Equal(valid, result.IsValid);
        if (!valid)
            Assert.Contains(result.Errors, e => e.Contains("DisablePayloadSigning requires an HTTPS ServiceUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StorageErrors.NotFoundCode, true)]
    [InlineData(StorageErrors.ConflictCode, true)]
    [InlineData(StorageErrors.CancelledCode, true)]
    [InlineData(StorageErrors.InvalidPathCode, true)]
    [InlineData(StorageErrors.ProviderErrorCode, false)]
    [InlineData(StorageErrors.PermissionDeniedCode, false)]
    public void Routine_failures_are_not_logged(string code, bool routine) => Assert.Equal(routine, StorageConnectionObserver.IsRoutine(code));

    [Fact]
    public void Failure_logs_are_throttled_per_operation_and_code()
    {
        var observer = new StorageConnectionObserver(() => null, () => null);
        var now = DateTimeOffset.UtcNow;
        Assert.True(observer.ShouldLog("Default", "upload", StorageErrors.ProviderErrorCode, now));
        Assert.False(observer.ShouldLog("Default", "upload", StorageErrors.ProviderErrorCode, now.AddSeconds(30)));
        Assert.True(observer.ShouldLog("Default", "delete", StorageErrors.ProviderErrorCode, now.AddSeconds(30)));
        Assert.True(observer.ShouldLog("Default", "upload", StorageErrors.TimeoutCode, now.AddSeconds(30)));
        Assert.True(observer.ShouldLog("Default", "upload", StorageErrors.ProviderErrorCode, now + StorageConnectionObserver.LogInterval));
    }
}
