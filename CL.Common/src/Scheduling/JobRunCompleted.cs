using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

/// <summary>A finished run.</summary>
/// <param name="Name">The job's name.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Error">Its error; null when it succeeded.</param>
public readonly record struct JobRunCompleted(string Name, DateTimeOffset StartedAt, TimeSpan Duration, Error? Error);
