using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

public sealed partial class GoogleCloudStorageBackend
{

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(condition.ExpectedETag, current.ETag, StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} generation no longer matches the requested condition."))
            : Result.Success();
    }

    private static StorageItem ToItem(string path, GcsObject item) => new()
    {
        Path = path.TrimEnd('/'),
        Name = NameOf(path.TrimEnd('/')),
        ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
        Size = path.EndsWith('/') || !item.Size.HasValue ? null : checked((long)item.Size.Value),
        LastModified = ParseTimestamp(item.UpdatedRaw),
        ContentType = item.ContentType,
        ETag = item.ETag,
        VersionId = item.Generation?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Metadata = item.Metadata is null
            ? []
            : new Dictionary<string, string>(item.Metadata, StringComparer.Ordinal)
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    /// <summary>
    /// Parses an RFC 3339 timestamp leniently. The client library's own parser insists on exactly three
    /// fractional digits, so a server or emulator that trims them would otherwise fail the whole operation.
    /// </summary>
    internal static DateTimeOffset? ParseTimestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static Error Map(Exception exception, string operation)
    {
        if (exception is GoogleApiException google && (int)google.HttpStatusCode > 0)
            return ProviderErrorMapper.FromHttpStatus((int)google.HttpStatusCode, operation, "Google Cloud Storage");
        return ProviderErrorMapper.FromTransport(exception, operation, "Google Cloud Storage")
            ?? StorageErrors.ProviderError($"{operation}: Google Cloud Storage provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private sealed record GcsContinuationToken(string? ProviderPageToken, int Skip, string? Previous = null);
}
