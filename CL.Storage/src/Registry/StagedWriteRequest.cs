using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>What to write through a staging object, and what to check before it may be promoted.</summary>
internal sealed record StagedWriteRequest
{
    /// <summary>The final destination path the staged content will be promoted to.</summary>
    public required string Path { get; init; }
    /// <summary>Content type, metadata, parent creation, and progress for the staging upload.</summary>
    public StorageUploadOptions Upload { get; init; } = new();
    /// <summary>The exact number of bytes the source must deliver.</summary>
    public long? ExpectedLength { get; init; }
    /// <summary>Whether to hash the content and confirm the staged object.</summary>
    public bool Verify { get; init; }
    /// <summary>The digest the content must have.</summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>When set, staging is resumable: its name derives from this key, and it survives failures.</summary>
    public string? ResumeKey { get; init; }
    /// <summary>An explicit resumable staging path, from a resume token.</summary>
    public string? StagingPath { get; init; }

    public bool Resumable => ResumeKey is not null || StagingPath is not null;
}
