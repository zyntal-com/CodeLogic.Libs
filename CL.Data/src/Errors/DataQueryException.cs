using CodeLogic.Core.Results;

namespace CL.Data.Errors;

/// <summary>
/// Thrown only while enumerating a streamed query (<c>AsAsyncEnumerable</c>), where a failure cannot be returned as a
/// <see cref="Result{T}"/>. Every other CL.Data API returns its errors as results.
/// </summary>
public sealed class DataQueryException(Error error) : Exception(error.Message)
{
    /// <summary>The CL.Data error (code, kind, details).</summary>
    public Error Error { get; } = error;
}
