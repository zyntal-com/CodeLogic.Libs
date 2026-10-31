using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Test helpers for the non-generic <see cref="Result"/>, which has no ValueOrThrow.</summary>
public static class ResultAssertions
{
    /// <summary>Throws when <paramref name="result"/> failed.</summary>
    public static void Ensure(this Result result)
    {
        if (result.IsFailure) throw new InvalidOperationException($"Result is failure: {result.Error}");
    }
}
