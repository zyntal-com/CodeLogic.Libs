using System.IO.Compression;
using System.Text;
using CodeLogic.Core.Results;
using K4os.Compression.LZ4.Streams;

namespace CL.Common.Compression;

/// <summary>
/// Provides GZip, Brotli, Zstandard, and LZ4 compression and decompression utilities.
/// All methods return <see cref="Result{T}"/> to signal failures without throwing.
/// </summary>
public static class CompressionHelper
{
    /// <summary>
    /// Default cap on the decompressed size accepted by the <c>Decompress*</c> methods: 100 MiB.
    /// Pass a larger <c>maxOutputBytes</c> explicitly when you expect bigger payloads.
    /// </summary>
    public const long DefaultMaxDecompressedBytes = 100L * 1024 * 1024;

    /// <summary>Error code returned when decompressed output exceeds the caller's cap. Kind: validation.</summary>
    public const string OutputLimitExceededCode = "compression.output_limit_exceeded";

    private static Error OutputLimitExceeded(long maxOutputBytes) =>
        Error.Validation(OutputLimitExceededCode,
            $"Decompressed data exceeds the limit of {maxOutputBytes} bytes.");

    /// <summary>Copies at most <paramref name="maxBytes"/> bytes; returns false if the source has more.</summary>
    private static bool CopyBounded(Stream source, Stream destination, long maxBytes)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) return false;
                destination.Write(buffer, 0, read);
            }
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Compresses the given byte array using GZip compression.
    /// </summary>
    /// <param name="data">The raw bytes to compress. Must not be null.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the compressed bytes on success,
    /// or a failure result with an error description.
    /// </returns>
    public static Result<byte[]> CompressGzip(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(data, 0, data.Length);
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.gzip_compress_failed"));
        }
    }

    /// <summary>
    /// Decompresses a GZip-compressed byte array.
    /// </summary>
    /// <param name="data">The GZip-compressed bytes to decompress. Must not be null.</param>
    /// <param name="maxOutputBytes">
    /// The most decompressed bytes accepted (default <see cref="DefaultMaxDecompressedBytes"/>). Larger output
    /// fails with <see cref="OutputLimitExceededCode"/> instead of exhausting memory (decompression bomb).
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails or the output exceeds <paramref name="maxOutputBytes"/>.
    /// </returns>
    public static Result<byte[]> DecompressGzip(byte[] data, long maxOutputBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        try
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            if (!CopyBounded(gzip, output, maxOutputBytes))
                return Result<byte[]>.Failure(OutputLimitExceeded(maxOutputBytes));
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.gzip_decompress_failed"));
        }
    }

    /// <summary>
    /// Compresses the given byte array using Brotli compression.
    /// </summary>
    /// <param name="data">The raw bytes to compress. Must not be null.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the compressed bytes on success,
    /// or a failure result with an error description.
    /// </returns>
    public static Result<byte[]> CompressBrotli(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                brotli.Write(data, 0, data.Length);
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.brotli_compress_failed"));
        }
    }

    /// <summary>
    /// Decompresses a Brotli-compressed byte array.
    /// </summary>
    /// <param name="data">The Brotli-compressed bytes to decompress. Must not be null.</param>
    /// <param name="maxOutputBytes">
    /// The most decompressed bytes accepted (default <see cref="DefaultMaxDecompressedBytes"/>). Larger output
    /// fails with <see cref="OutputLimitExceededCode"/> instead of exhausting memory (decompression bomb).
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails or the output exceeds <paramref name="maxOutputBytes"/>.
    /// </returns>
    public static Result<byte[]> DecompressBrotli(byte[] data, long maxOutputBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        try
        {
            using var input = new MemoryStream(data);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            if (!CopyBounded(brotli, output, maxOutputBytes))
                return Result<byte[]>.Failure(OutputLimitExceeded(maxOutputBytes));
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.brotli_decompress_failed"));
        }
    }

    /// <summary>
    /// Compresses the given byte array using Zstandard compression
    /// (the built-in <see cref="ZstandardStream"/> added in .NET 11).
    /// Zstandard gives ratios close to Brotli at speeds much closer to LZ4.
    /// </summary>
    /// <param name="data">The raw bytes to compress. Must not be null.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the compressed bytes on success,
    /// or a failure result with an error description.
    /// </returns>
    public static Result<byte[]> CompressZstandard(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var output = new MemoryStream();
            using (var zstd = new ZstandardStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zstd.Write(data, 0, data.Length);
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.zstandard_compress_failed"));
        }
    }

    /// <summary>
    /// Decompresses a Zstandard-compressed byte array (any standard Zstandard frame,
    /// e.g. one produced by <see cref="CompressZstandard"/> or the <c>zstd</c> CLI).
    /// </summary>
    /// <param name="data">The Zstandard-compressed bytes to decompress. Must not be null.</param>
    /// <param name="maxOutputBytes">
    /// The most decompressed bytes accepted (default <see cref="DefaultMaxDecompressedBytes"/>). Larger output
    /// fails with <see cref="OutputLimitExceededCode"/> instead of exhausting memory (decompression bomb).
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails or the output exceeds <paramref name="maxOutputBytes"/>.
    /// </returns>
    public static Result<byte[]> DecompressZstandard(byte[] data, long maxOutputBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        try
        {
            using var input = new MemoryStream(data);
            using var zstd = new ZstandardStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            if (!CopyBounded(zstd, output, maxOutputBytes))
                return Result<byte[]>.Failure(OutputLimitExceeded(maxOutputBytes));
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.zstandard_decompress_failed"));
        }
    }

    /// <summary>
    /// Compresses the given byte array using LZ4 compression (via K4os.Compression.LZ4.Streams).
    /// LZ4 offers very fast compression at the cost of slightly lower compression ratios than GZip.
    /// </summary>
    /// <param name="data">The raw bytes to compress. Must not be null.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the LZ4-compressed bytes on success,
    /// or a failure result with an error description.
    /// </returns>
    public static Result<byte[]> CompressLz4(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var output = new MemoryStream();
            using (var lz4 = LZ4Stream.Encode(output, leaveOpen: true))
                lz4.Write(data, 0, data.Length);
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.lz4_compress_failed"));
        }
    }

    /// <summary>
    /// Decompresses an LZ4-compressed byte array produced by <see cref="CompressLz4"/>.
    /// </summary>
    /// <param name="data">The LZ4-compressed bytes to decompress. Must not be null.</param>
    /// <param name="maxOutputBytes">
    /// The most decompressed bytes accepted (default <see cref="DefaultMaxDecompressedBytes"/>). Larger output
    /// fails with <see cref="OutputLimitExceededCode"/> instead of exhausting memory (decompression bomb).
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails or the output exceeds <paramref name="maxOutputBytes"/>.
    /// </returns>
    public static Result<byte[]> DecompressLz4(byte[] data, long maxOutputBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        try
        {
            using var input = new MemoryStream(data);
            using var lz4 = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            if (!CopyBounded(lz4, output, maxOutputBytes))
                return Result<byte[]>.Failure(OutputLimitExceeded(maxOutputBytes));
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(Error.FromException(ex, "compression.lz4_decompress_failed"));
        }
    }

    /// <summary>
    /// Compresses a UTF-8 string using GZip and returns the result as a Base64-encoded string.
    /// Useful for storing compressed text in text-based storage systems.
    /// </summary>
    /// <param name="text">The text to compress. Must not be null.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the Base64-encoded GZip-compressed text on success,
    /// or a failure result with an error description.
    /// </returns>
    public static Result<string> CompressString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var compressResult = CompressGzip(Encoding.UTF8.GetBytes(text));
        return compressResult.IsSuccess
            ? Result<string>.Success(Convert.ToBase64String(compressResult.Value!))
            : Result<string>.Failure(compressResult.Error!);
    }

    /// <summary>
    /// Decompresses a Base64-encoded GZip-compressed string produced by <see cref="CompressString"/>.
    /// </summary>
    /// <param name="compressed">The Base64-encoded compressed string. Must not be null.</param>
    /// <param name="maxOutputBytes">The most decompressed bytes accepted (default <see cref="DefaultMaxDecompressedBytes"/>).</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the original UTF-8 text on success,
    /// or a failure result if decompression or Base64 decoding fails.
    /// </returns>
    public static Result<string> DecompressString(string compressed, long maxOutputBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        try
        {
            var bytes = Convert.FromBase64String(compressed);
            var decompressResult = DecompressGzip(bytes, maxOutputBytes);
            return decompressResult.IsSuccess
                ? Result<string>.Success(Encoding.UTF8.GetString(decompressResult.Value!))
                : Result<string>.Failure(decompressResult.Error!);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, "compression.decompress_string_failed"));
        }
    }
}
