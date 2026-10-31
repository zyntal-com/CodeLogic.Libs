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
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails.
    /// </returns>
    public static Result<byte[]> DecompressGzip(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
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
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails.
    /// </returns>
    public static Result<byte[]> DecompressBrotli(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var input = new MemoryStream(data);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
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
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails.
    /// </returns>
    public static Result<byte[]> DecompressZstandard(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var input = new MemoryStream(data);
            using var zstd = new ZstandardStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zstd.CopyTo(output);
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
    /// <returns>
    /// A <see cref="Result{T}"/> containing the decompressed bytes on success,
    /// or a failure result if decompression fails.
    /// </returns>
    public static Result<byte[]> DecompressLz4(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var input = new MemoryStream(data);
            using var lz4 = LZ4Stream.Decode(input);
            using var output = new MemoryStream();
            lz4.CopyTo(output);
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
    /// <returns>
    /// A <see cref="Result{T}"/> containing the original UTF-8 text on success,
    /// or a failure result if decompression or Base64 decoding fails.
    /// </returns>
    public static Result<string> DecompressString(string compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        try
        {
            var bytes = Convert.FromBase64String(compressed);
            var decompressResult = DecompressGzip(bytes);
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
