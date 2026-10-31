using System.Reflection;
using CL.Data.Errors;
using CL.Data.Events;
using CodeLogic.Core.Results;

namespace Data.Tests.Errors;

public sealed class DataErrorsTests
{
    // Every factory with the kind its code must always carry.
    public static TheoryData<Error, Type, string> Factories => new()
    {
        { DataErrors.NotFound("m"), typeof(NotFoundError), DataErrors.NotFoundCode },
        { DataErrors.ConnectionUnknown("x"), typeof(NotFoundError), DataErrors.ConnectionUnknownCode },
        { DataErrors.DuplicateKey("m"), typeof(ConflictError), DataErrors.DuplicateKeyCode },
        { DataErrors.ForeignKeyViolation("m"), typeof(ConflictError), DataErrors.ForeignKeyViolationCode },
        { DataErrors.ConstraintViolation("m"), typeof(ConflictError), DataErrors.ConstraintViolationCode },
        { DataErrors.Conflict("m"), typeof(ConflictError), DataErrors.ConflictCode },
        { DataErrors.Concurrency("t"), typeof(ConflictError), DataErrors.ConcurrencyCode },
        { DataErrors.Deadlock(), typeof(ConflictError), DataErrors.DeadlockCode },
        { DataErrors.Timeout("m"), typeof(TimeoutError), DataErrors.TimeoutCode },
        { DataErrors.LockTimeout(), typeof(TimeoutError), DataErrors.LockTimeoutCode },
        { DataErrors.Unavailable("m"), typeof(UnavailableError), DataErrors.UnavailableCode },
        { DataErrors.Cancelled(), typeof(UnavailableError), DataErrors.CancelledCode },
        { DataErrors.LockNotAcquired("m"), typeof(UnavailableError), DataErrors.LockNotAcquiredCode },
        { DataErrors.CircuitOpen("primary"), typeof(UnavailableError), DataErrors.CircuitOpenCode },
        { DataErrors.NoHealthyNode("main"), typeof(UnavailableError), DataErrors.NoHealthyNodeCode },
        { DataErrors.AccessDenied("m"), typeof(UnauthorizedError), DataErrors.AccessDeniedCode },
        { DataErrors.Validation("m"), typeof(ValidationError), DataErrors.ValidationCode },
        { DataErrors.TranslationFailed("m"), typeof(ValidationError), DataErrors.TranslationFailedCode },
        { DataErrors.Unsupported("m"), typeof(ValidationError), DataErrors.UnsupportedCode },
        { DataErrors.NoPrimaryKey("E"), typeof(ValidationError), DataErrors.NoPrimaryKeyCode },
        { DataErrors.ParameterLimit(3000, 2100), typeof(ValidationError), DataErrors.ParameterLimitCode },
        { DataErrors.SchemaDrift("t"), typeof(ValidationError), DataErrors.SchemaDriftCode },
        { DataErrors.InvalidCursor("m"), typeof(ValidationError), DataErrors.InvalidCursorCode },
        { DataErrors.ProviderMissing("mysql"), typeof(ValidationError), DataErrors.ProviderMissingCode },
        { DataErrors.ProviderIncompatible("mysql", 2, 1), typeof(ValidationError), DataErrors.ProviderIncompatibleCode },
        { DataErrors.EntityNotMapped("E"), typeof(ValidationError), DataErrors.EntityNotMappedCode },
        { DataErrors.TenantMissing("E"), typeof(ValidationError), DataErrors.TenantMissingCode },
        { DataErrors.ProviderError("m"), typeof(InternalError), DataErrors.ProviderErrorCode },
        { DataErrors.SchemaSyncFailed("t"), typeof(InternalError), DataErrors.SchemaSyncFailedCode },
        { DataErrors.MigrationFailed("m"), typeof(InternalError), DataErrors.MigrationFailedCode },
        { DataErrors.BulkFailed("t"), typeof(InternalError), DataErrors.BulkFailedCode },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Factory_pairs_code_with_kind(Error error, Type kind, string code)
    {
        Assert.IsType(kind, error);
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Every_code_is_unique_snake_case_and_has_a_factory()
    {
        var codes = typeof(DataErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Code", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^data\\.[a-z]+(_[a-z]+)*$", c));

        var covered = Factories.Select(row => (string)row[2]).ToHashSet();
        Assert.All(codes, c => Assert.Contains(c, covered));
    }

    [Fact]
    public void FromException_maps_cancellation_timeout_and_other()
    {
        Assert.Equal(DataErrors.CancelledCode, DataErrors.FromException(new OperationCanceledException()).Code);
        Assert.Equal(DataErrors.CancelledCode, DataErrors.FromException(new TaskCanceledException()).Code);
        Assert.Equal(DataErrors.TimeoutCode, DataErrors.FromException(new TimeoutException("slow")).Code);

        var other = DataErrors.FromException(new InvalidOperationException("secret=hunter2"));
        Assert.Equal(DataErrors.ProviderErrorCode, other.Code);
        Assert.DoesNotContain("hunter2", other.Message);   // exception text only in Details
        Assert.Contains("hunter2", other.Details);
    }

    [Fact]
    public void Events_switch_is_exhaustive_over_the_closed_hierarchy()
    {
        var at = DateTimeOffset.UnixEpoch;
        DataEvent[] events =
        [
            new QueryExecutedEvent("c", "p", "primary", at, "select", "t", TimeSpan.Zero, 1, true),
            new SlowQueryEvent("c", "p", "primary", at, "SELECT 1", TimeSpan.FromSeconds(2), null),
            new NPlusOneDetectedEvent("c", "p", "primary", at, "SELECT ?", 50, TimeSpan.FromSeconds(1)),
            new CacheHitEvent("c", "p", at, "t"),
            new CacheMissEvent("c", "p", at, "t"),
            new SchemaSyncedEvent("c", "p", at, "t", true, 1),
            new SchemaDriftDetectedEvent("c", "p", at, "t", ["drop column x"]),
            new MigrationAppliedEvent("c", "p", at, "1.0.0.1", "init", false, TimeSpan.Zero),
            new SeedAppliedEvent("c", "p", at, "roles"),
            new RetentionPurgedEvent("c", "p", at, "t", 10),
            new BulkCopyCompletedEvent("c", "p", at, "t", 1000, TimeSpan.Zero),
            new NodeHealthChangedEvent("c", "p", "replica1", at, false, "lag"),
            new CircuitStateChangedEvent("c", "p", "replica1", at, "open"),
        ];

        // No default arm: adding an event type must fail this switch at compile time (CS8509).
        static string Name(DataEvent e) => e switch
        {
            QueryExecutedEvent => "query",
            SlowQueryEvent => "slow",
            NPlusOneDetectedEvent => "n1",
            CacheHitEvent => "hit",
            CacheMissEvent => "miss",
            SchemaSyncedEvent => "synced",
            SchemaDriftDetectedEvent => "drift",
            MigrationAppliedEvent => "migration",
            SeedAppliedEvent => "seed",
            RetentionPurgedEvent => "retention",
            BulkCopyCompletedEvent => "bulk",
            NodeHealthChangedEvent => "health",
            CircuitStateChangedEvent => "circuit",
        };

        Assert.Equal(events.Length, events.Select(Name).Distinct().Count());
        Assert.All(events, e => Assert.Equal("c", e.ConnectionId));
    }
}
