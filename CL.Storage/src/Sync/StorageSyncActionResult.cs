using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How one planned step turned out.</summary>
/// <param name="Action">The step.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">
/// The failure, for failed and stale steps. An applied step carries one when the destination was committed but
/// something after it was not done (for example a provider's own staging or backup copy was left behind; see
/// <see cref="StorageErrorInfo.LeftBehindKey"/>).
/// </param>
/// <param name="Attempts">How many times it was tried.</param>
public sealed record StorageSyncActionResult(StorageSyncAction Action, StorageSyncActionOutcome Outcome, Error? Error = null, int Attempts = 0);
