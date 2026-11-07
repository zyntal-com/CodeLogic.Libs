using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What the last completed sync saw on each side of one path.</summary>
/// <param name="Source">The source's version of a file, or null when absent there (or a directory).</param>
/// <param name="Destination">The destination's version of a file, or null when absent there (or a directory).</param>
/// <param name="IsDirectory">Whether the path was a directory on both sides.</param>
public sealed record StorageSyncBaselineEntry(StorageSyncIdentity? Source, StorageSyncIdentity? Destination, bool IsDirectory = false);
