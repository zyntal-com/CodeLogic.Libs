using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Staged content ready to promote.</summary>
internal sealed record StagedContent(string StagingPath, long Bytes, long BytesResumed, string? Sha256, string? VerifiedBy);
