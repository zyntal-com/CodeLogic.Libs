using System.Text;
using CL.Common.Hashing;
using CL.Common.Threading;
using Xunit;

namespace Common.Tests;

public class FastHashTests
{
    [Fact]
    public void Xxh3_matches_the_reference_vector_for_empty_input()
        // XXH3_64bits("") with seed 0, from the xxHash reference test vectors.
        => Assert.Equal(0x2D06800538D394C2UL, FastHash.XxHash3([]));

    [Fact]
    public void Xxh128_matches_the_reference_vector_for_empty_input()
        // XXH3_128bits("") with seed 0.
        => Assert.Equal(new UInt128(0x99AA06D3014798D8, 0x6001C324468D497F), FastHash.XxHash128(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Seed_changes_the_hash()
    {
        var data = "hello"u8;
        Assert.NotEqual(FastHash.XxHash3(data), FastHash.XxHash3(data, seed: 1));
        Assert.NotEqual(FastHash.XxHash128(data), FastHash.XxHash128(data, seed: 1));
    }

    [Fact]
    public void Text_hashes_its_utf16_bytes()
    {
        const string text = "SELECT * FROM users WHERE id = @p0";
        var expected = FastHash.XxHash128(Encoding.Unicode.GetBytes(text));
        Assert.Equal(expected, FastHash.XxHash128(text.AsSpan()));
    }

    [Fact]
    public void Hex_is_32_lowercase_chars_most_significant_first()
    {
        var hex = FastHash.ToHex(new UInt128(0x0123456789ABCDEF, 0xFEDCBA9876543210));
        Assert.Equal("0123456789abcdeffedcba9876543210", hex);
        Assert.Equal(new string('0', 32), FastHash.ToHex(UInt128.Zero));
    }

    [Fact]
    public void Incremental_hash_equals_one_shot_hash_of_the_concatenation()
    {
        var hasher = FastHasher.Create(seed: 7);
        hasher.Append("abc"u8);
        hasher.Append("déf".AsSpan());
        hasher.Append(42);
        hasher.Append(Guid.Empty);

        byte[] all = [.. "abc"u8, .. Encoding.Unicode.GetBytes("déf"), .. BitConverter.GetBytes(42), .. new byte[16]];
        Assert.Equal(FastHash.XxHash128(all, seed: 7), hasher.GetHash128());
    }

    [Fact]
    public void Default_hasher_is_a_programming_error()
        => Assert.Throws<InvalidOperationException>(() => default(FastHasher).GetHash128());
}

public class AsyncKeyedLockTests
{
    [Fact]
    public async Task Same_key_is_exclusive_and_different_keys_are_not()
    {
        var locks = new AsyncKeyedLock<string>();
        var a = await locks.LockAsync("a");
        Assert.False(locks.TryLock("a", out _));
        Assert.True(locks.TryLock("b", out var b));

        var waiter = locks.LockAsync("a");
        Assert.False(waiter.IsCompleted);
        a.Dispose();
        using (await waiter) { }
        b.Dispose();
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_take_the_lock_or_leak_the_key()
    {
        var locks = new AsyncKeyedLock<int>();
        var held = await locks.LockAsync(1);
        using var cts = new CancellationTokenSource();
        var waiter = locks.LockAsync(1, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
        Assert.Equal(0, locks.Count);
        Assert.True(locks.TryLock(1, out var again));
        again.Dispose();
    }

    [Fact]
    public async Task Many_distinct_keys_leave_nothing_behind()
    {
        var locks = new AsyncKeyedLock<UInt128>();
        for (var i = 0; i < 10_000; i++)
            using (await locks.LockAsync((UInt128)i)) { }
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Concurrent_holders_of_one_key_never_overlap()
    {
        var locks = new AsyncKeyedLock<string>();
        var inside = 0;
        var maxInside = 0;
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using (await locks.LockAsync("k"))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            }
        }));
        await Task.WhenAll(tasks);
        Assert.Equal(1, maxInside);
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public void Disposing_a_default_releaser_does_nothing()
        => default(AsyncKeyedLock<string>.Releaser).Dispose();

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
