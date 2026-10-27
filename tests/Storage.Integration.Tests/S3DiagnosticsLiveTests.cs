using CL.Storage.Errors;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>An S3 request the SDK refuses reports the SDK's reason instead of "S3 provider failed".</summary>
public sealed class S3DiagnosticsLiveTests
{
    [S3Fact]
    public async Task Unsigned_payloads_over_http_report_why_they_fail()
    {
        var config = CloudEmulators.S3(c => c.DisablePayloadSigning = true);
        if (!config.ServiceUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return; // only reproducible against a plain-HTTP emulator

        // Validation now refuses this connection; build the backend directly to see what an upload reports.
        Assert.False(config.Validate().IsValid);
        await CloudEmulators.CreateAsync(CloudEmulators.S3()); // makes sure the bucket exists
        await using var backend = new S3StorageBackendFactory().Create("emu-s3-unsigned", config, 64L << 20);

        var upload = await backend.UploadBytesAsync("diagnostics/unsigned.txt", [1, 2, 3]);
        Assert.True(upload.IsFailure);
        Assert.Equal(StorageErrors.ProviderErrorCode, upload.Error!.Code);
        Assert.Contains("must be sent over HTTPS", upload.Error.Message, StringComparison.Ordinal);
    }
}
