using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>The outcome of writing to staging: the content, or the error and what was left behind.</summary>
internal sealed record StagedWriteResult(StagedContent? Content, Error? Error, string? StagingLeft, long BytesStaged)
{
    /// <summary>Whether the caller cancelled; a resumable write's staged bytes are kept, anything else removed.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Why a staging object that should have been removed was not.</summary>
    public Error? CleanupError { get; init; }

    /// <summary>
    /// Whether the write used a resumable staging object, so <see cref="StagingLeft"/> is a prefix a later
    /// attempt may continue. False when the request was not resumable, or when its part file was in use by
    /// another transfer and a private staging object was used instead.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>
    /// The hold on a resumable part file, on success only: the caller keeps it until the part file has been
    /// promoted (or settled) and then releases it, so no other transfer appends to it in between.
    /// </summary>
    public PartLease? Lease { get; init; }

    public bool IsSuccess => Content is not null;
}
