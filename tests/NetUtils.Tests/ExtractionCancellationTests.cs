using CL.NetUtils.Services;
using Xunit;

namespace NetUtils.Tests;

public sealed class ExtractionCancellationTests
{
    [Fact]
    public void Extraction_honors_a_cancelled_token_before_opening_or_installing_files()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GeoIpService.ExtractDatabase("missing.tar.gz", "missing.mmdb", "GeoLite2-City.mmdb", cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Decompression_checks_the_operation_token_on_every_read()
    {
        using var cancelled = new CancellationTokenSource();
        using var source = new MemoryStream(new byte[32]);
        using var bounded = new BoundedReadStream(source, 100, "archive", cancelled.Token);
        Assert.Equal(0, bounded.ReadByte());
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => bounded.ReadByte());
    }

    [Fact]
    public void Shared_log_fields_cannot_inject_newlines_or_directional_formatting()
    {
        Assert.Equal("a?b?c?d", InputText.ForLog("a\rb\nc\u202ed"));
        Assert.EndsWith("...", InputText.ForLog(new string('x', 100)));
    }
}
