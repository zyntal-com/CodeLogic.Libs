using System.Buffers;
using CL.Imaging.Internal;
using Xunit;

namespace Imaging.Tests;

public sealed class ConcurrentBufferTests
{
    [Fact]
    public async Task A_last_release_cannot_race_reference_acquisition_and_resurrect_the_buffer()
    {
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(16);
            rented.AsSpan(0, 16).Fill(42);
            var buffer = new PooledBuffer(rented, 16);
            using var start = new ManualResetEventSlim();
            var acquire = Task.Run(() =>
            {
                start.Wait();
                try { buffer.AddRef(); return true; }
                catch (ObjectDisposedException) { return false; }
            });
            var release = Task.Run(() => { start.Wait(); buffer.Dispose(); });
            start.Set();
            await Task.WhenAll(acquire, release);
            if (await acquire)
            {
                Assert.All(buffer.Span.ToArray(), b => Assert.Equal(42, b));
                buffer.Dispose();
            }
            Assert.Throws<ObjectDisposedException>(buffer.AddRef);
        }
    }
}
