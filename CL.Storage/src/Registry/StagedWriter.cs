using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>
/// Writes content through a staging object next to the destination: hashed and counted while it streams,
/// checked against an expected length and digest, confirmed on the destination, and only then promoted.
/// The destination is never half-written. Resumable writes keep the staging object on failure under a name
/// derived from the destination and the source's identity, so a later attempt, even from another process,
/// appends only the missing tail.
/// </summary>
internal static partial class StagedWriter
{
    internal const long PauseWriterThreshold = 1_048_576;
    internal const long ResumeWriterThreshold = 524_288;
    internal const int SegmentSize = 65_536;
    private const int StagingNameAttempts = 8;
    internal const string ResumablePrefix = ".cl-storage-part-";

    /// <summary>How many times <see cref="ConfirmPromotedAsync"/> reads a committed destination back on transient errors.</summary>
    private const int ReadBackAttempts = 3;
}
