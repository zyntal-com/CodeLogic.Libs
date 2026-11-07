using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.S3;

public sealed partial class S3StorageBackend
{

    /// <summary>
    /// Deletes a moved source only while it is still the version that was copied: its ETag (and version, when one
    /// was pinned) is compared first, and the delete carries <c>If-Match</c> where the server enforces it on deletes
    /// (elsewhere the comparison just before is the guard). Runs after the destination committed, so it is not cancelled.
    /// </summary>
    private async Task<Result> DeleteMovedSourceAsync(string sourceKey, S3PinnedSource copied)
    {
        try
        {
            var sendIfMatch = await ConditionStateAsync(S3ProbedCondition.DeleteMatch, CancellationToken.None).ConfigureAwait(false) == S3ConditionState.Enforced;
            var current = await HeadAsync(sourceKey, CancellationToken.None).ConfigureAwait(false);
            if (current is null)
                return Result.Success();
            if (!StagedWriter.SameETag(copied.ETag, current.ETag) ||
                (copied.VersionId is not null && !string.Equals(copied.VersionId, current.VersionId, StringComparison.Ordinal)))
                return Result.Failure(StorageErrors.Conflict("The S3 source changed after it was copied, so it was not deleted."));
            var request = new DeleteObjectRequest { BucketName = _bucket, Key = sourceKey };
            if (sendIfMatch) request.IfMatch = current.ETag;
            await _client.DeleteObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result.Failure(StorageErrors.Conflict("The S3 source changed after it was copied, so it was not deleted."));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Delete moved S3 source")); }
    }

    private static bool IsNonStandard(S3StorageClass? storageClass) =>
        storageClass is not null && !string.IsNullOrEmpty(storageClass.Value) && storageClass != S3StorageClass.Standard;

    private static bool IsEncrypted(ServerSideEncryptionMethod? method) =>
        method is not null && !string.IsNullOrEmpty(method.Value) && method != ServerSideEncryptionMethod.None;

    /// <summary>
    /// Whether an upload's condition (create-only, or <see cref="StorageUploadOptions.Condition"/>) can be sent with the
    /// request that commits: the server enforces it there. Otherwise it is checked just before instead. An upload
    /// without a condition needs no probe.
    /// </summary>
    private async ValueTask<bool> UploadConditionsEnforcedAsync(StorageUploadOptions options, CancellationToken cancellationToken)
    {
        if (options.Overwrite && options.Condition is null or { IsEmpty: true }) return true;
        var condition = options.Overwrite ? S3ProbedCondition.PutMatch : S3ProbedCondition.PutCreateOnly;
        return await ConditionStateAsync(condition, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
    }

    /// <inheritdoc />
    /// <remarks>
    /// With <see cref="S3ConditionalRequestSupport.Auto"/> the answer comes from the connection's probe (run here when
    /// it has not run yet): <see cref="StorageConditionEnforcement.Atomic"/> only for a condition the server was seen to
    /// enforce on the request that commits (<c>PutObject</c>/<c>CompleteMultipartUpload</c> for uploads,
    /// <c>CopyObject</c> for server-side copies, <c>DeleteObject</c> for deletes). MinIO, for one, ignores both
    /// conditions on <c>CopyObject</c> and <c>If-Match</c> on <c>DeleteObject</c>. An upload with a version condition is
    /// staged and promoted with <c>CopyObject</c> unless the server enforces <c>If-Match</c> on both requests (the
    /// <see cref="StorageFeature.ConditionalUpdate"/> flag), so it is <see cref="StorageConditionEnforcement.Atomic"/>
    /// only then.
    /// </remarks>
    async ValueTask<StorageConditionEnforcement> IStorageConditionEnforcementSource.GetEnforcementAsync(StorageConditionKind kind, bool serverSideCopy, CancellationToken cancellationToken)
    {
        var probed = kind switch
        {
            StorageConditionKind.CreateOnly => serverSideCopy ? S3ProbedCondition.CopyCreateOnly : S3ProbedCondition.PutCreateOnly,
            StorageConditionKind.MatchVersion => serverSideCopy ? S3ProbedCondition.CopyMatch : S3ProbedCondition.PutMatch,
            _ => S3ProbedCondition.DeleteMatch
        };
        var enforced = await ConditionStateAsync(probed, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
        // As StorageTransferPipeline.NeedsConditionStaging decides: the promote's CopyObject must enforce it too.
        if (enforced && kind == StorageConditionKind.MatchVersion && !serverSideCopy)
            enforced = await ConditionStateAsync(S3ProbedCondition.CopyMatch, cancellationToken).ConfigureAwait(false) == S3ConditionState.Enforced;
        return enforced ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.CheckedBeforeCommit;
    }

    /// <summary>
    /// Whether the server enforces a condition: by setting, or by the connection's probe. A conclusive probe is kept
    /// for the connection's life. One that could not finish is kept too, as "not enforced", until its back-off ends,
    /// so a failing server is not probed again by every conditional request.
    /// </summary>
    private async ValueTask<S3ConditionState> ConditionStateAsync(S3ProbedCondition condition, CancellationToken cancellationToken)
    {
        switch (ConditionalRequests)
        {
            case S3ConditionalRequestSupport.NotEnforced:
                return S3ConditionState.Ignored;
            case S3ConditionalRequestSupport.Enforced:
                return S3ConditionState.Enforced;
        }
        var probe = Volatile.Read(ref _conditionProbe);
        if (probe is null || (!probe.Conclusive && Clock() >= probe.RetryAt))
        {
            await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                probe = _conditionProbe;
                if (probe is null || (!probe.Conclusive && Clock() >= probe.RetryAt))
                {
                    probe = await ProbeConditionsAsync(cancellationToken).ConfigureAwait(false);
                    if (!probe.Conclusive)
                    {
                        var failures = Math.Min(++_inconclusiveProbes, 6);
                        probe = probe with { RetryAt = Clock() + InconclusiveProbeBackoff * (1 << (failures - 1)) };
                    }
                    Volatile.Write(ref _conditionProbe, probe);
                }
            }
            finally { _probeGate.Release(); }
        }
        return probe.StateOf(condition);
    }

    /// <summary>
    /// Asks the server whether it enforces <c>If-None-Match</c> and <c>If-Match</c> on <c>PutObject</c> and on
    /// <c>CopyObject</c>, and <c>If-Match</c> on <c>DeleteObject</c>, with two one-byte <c>.cl-storage-probe-*</c>
    /// objects under the prefix that are removed afterwards. A request answered 412 enforces its condition; one that
    /// succeeds ignores it; one answered 400 or 501 rejects the header. <c>CompleteMultipartUpload</c> is taken to
    /// behave as <c>PutObject</c> does.
    /// </summary>
    /// <remarks>
    /// The probe's requests are real writes (four PUTs, two COPYs, three DELETEs): on a versioned bucket they leave
    /// noncurrent versions and delete markers of the probe objects, on an Object Lock bucket versions that cannot be
    /// deleted until their retention ends, and they raise event notifications and replication like any write. See
    /// <see cref="S3ConditionalRequestSupport.Auto"/>.
    /// </remarks>
    private async Task<S3ConditionProbe> ProbeConditionsAsync(CancellationToken cancellationToken)
    {
        var stem = $"{_keyPrefix}.cl-storage-probe-{Guid.NewGuid():N}";
        var first = stem + "-a";
        var second = stem + "-b";
        const string WrongETag = "\"00000000000000000000000000000000\"";
        try
        {
            foreach (var key in new[] { first, second })
            {
                var content = new MemoryStream([0x2A], writable: false);
                await using var contentLifetime = content.ConfigureAwait(false);
                await _client.PutObjectAsync(NewPutRequest(key, content, "application/octet-stream", null), cancellationToken).ConfigureAwait(false);
            }
            var putCreateOnly = await ProbeAsync(async () =>
            {
                var content = new MemoryStream([0x2B], writable: false);
                await using var contentLifetime = content.ConfigureAwait(false);
                var request = NewPutRequest(second, content, "application/octet-stream", null);
                request.IfNoneMatch = "*";
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var putMatch = await ProbeAsync(async () =>
            {
                var content = new MemoryStream([0x2C], writable: false);
                await using var contentLifetime = content.ConfigureAwait(false);
                var request = NewPutRequest(second, content, "application/octet-stream", null);
                request.IfMatch = WrongETag;
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var copyCreateOnly = await ProbeAsync(() => _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = first,
                DestinationBucket = _bucket,
                DestinationKey = second,
                IfNoneMatch = "*"
            }, cancellationToken)).ConfigureAwait(false);
            var copyMatch = await ProbeAsync(() => _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = first,
                DestinationBucket = _bucket,
                DestinationKey = second,
                IfMatch = WrongETag
            }, cancellationToken)).ConfigureAwait(false);
            var delete = await ProbeAsync(() => _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = second,
                IfMatch = WrongETag
            }, cancellationToken)).ConfigureAwait(false);
            return new S3ConditionProbe(putCreateOnly, putMatch, copyCreateOnly, copyMatch, delete, Conclusive: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Without an answer nothing is assumed enforced, and no header is sent that the server might reject.
            return new S3ConditionProbe(S3ConditionState.Unknown, S3ConditionState.Unknown, S3ConditionState.Unknown,
                S3ConditionState.Unknown, S3ConditionState.Unknown, Conclusive: false);
        }
        finally
        {
            foreach (var key in new[] { first, second })
            {
                try { await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* A probe object left behind is an internal name that listings hide. */ }
            }
        }
    }

    private static async Task<S3ConditionState> ProbeAsync(Func<Task> request)
    {
        try
        {
            await request().ConfigureAwait(false);
            return S3ConditionState.Ignored;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return S3ConditionState.Enforced;
        }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented)
        {
            return S3ConditionState.Rejected;
        }
    }

    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var result = Normalize(path);
        return result.IsFailure || result.Value!.Length > 0
            ? result
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private string ToKey(string path) => _keyPrefix + path;
    private string ToDirectoryPrefix(string path) => path.Length == 0 ? _keyPrefix : ToKey(path).TrimEnd('/') + "/";
    /// <summary>The storage path of a key under the mounted prefix, or null for a key the library cannot address (see <see cref="ObjectKeyPaths"/>).</summary>
    private string? FromKey(string key) => ObjectKeyPaths.FromKey(_keyPrefix, key);
    private static string NameOf(string path) => path.Split('/')[^1];
    /// <summary>
    /// User metadata under the names it was written with: the SDK reports keys with the <c>x-amz-meta-</c>
    /// header prefix, which would otherwise leak into every read and round trip.
    /// </summary>
    private static Dictionary<string, string> UserMetadata(MetadataCollection metadata) =>
        metadata.Keys.ToDictionary(
            key => key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? key["x-amz-meta-".Length..] : key,
            key => metadata[key],
            StringComparer.Ordinal);

    private static StorageItem DirectoryItem(string path) => new() { Path = path, Name = path.Length == 0 ? string.Empty : NameOf(path), ItemType = StorageItemType.Directory };
}
