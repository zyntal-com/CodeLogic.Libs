using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>What a promote did: its result, how a condition was enforced, and what the provider left behind.</summary>
/// <param name="Result">Success when the destination holds the staged content.</param>
/// <param name="Enforcement">How a create-only or version condition was enforced.</param>
/// <param name="Touched">
/// Whether the destination may have been changed: false when the promote was refused before the provider was
/// asked to move anything (a failed condition check), so the destination is exactly as it was.
/// </param>
/// <param name="LeftBehind">
/// Internal objects the provider could not remove after it committed (its own backup or staging copy); the
/// destination is committed even though the provider reported an error.
/// </param>
internal sealed record PromoteOutcome(Result Result, StorageConditionEnforcement Enforcement, bool Touched, IReadOnlyList<string> LeftBehind);
