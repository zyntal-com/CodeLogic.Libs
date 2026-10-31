using CodeLogic.Core.Results;

namespace CL.Data.Errors;

/// <summary>
/// Creates CL.Data errors with stable, provider-neutral <c>data.*</c> codes. Every code is always
/// paired with the same <see cref="Error"/> kind, so callers can match on either the kind
/// (<c>ConflictError</c>, <c>TimeoutError</c>, ...) or the code.
/// </summary>
/// <remarks>
/// Messages are safe to show to callers: they never contain parameter values or connection
/// secrets. Provider diagnostics (server error numbers, SQLSTATE) go into <see cref="Error.Details"/>.
/// </remarks>
public static class DataErrors
{
    // ── NotFound ────────────────────────────────────────────────────────────

    /// <summary>A row looked up by key does not exist. Kind: NotFound.</summary>
    public const string NotFoundCode = "data.not_found";
    /// <summary>No connection with the requested name is configured. Kind: NotFound.</summary>
    public const string ConnectionUnknownCode = "data.connection_unknown";

    // ── Conflict ────────────────────────────────────────────────────────────

    /// <summary>A unique or primary key constraint rejected the write. Kind: Conflict.</summary>
    public const string DuplicateKeyCode = "data.duplicate_key";
    /// <summary>A foreign key constraint rejected the write. Kind: Conflict.</summary>
    public const string ForeignKeyViolationCode = "data.foreign_key_violation";
    /// <summary>Another constraint (check, not-null) rejected the write. Kind: Conflict.</summary>
    public const string ConstraintViolationCode = "data.constraint_violation";
    /// <summary>A generic write conflict. Kind: Conflict.</summary>
    public const string ConflictCode = "data.conflict";
    /// <summary>An optimistic-concurrency check failed (the row changed or was deleted). Kind: Conflict.</summary>
    public const string ConcurrencyCode = "data.concurrency";
    /// <summary>The server chose this transaction as a deadlock victim. Kind: Conflict.</summary>
    public const string DeadlockCode = "data.deadlock";

    // ── Timeout ─────────────────────────────────────────────────────────────

    /// <summary>The command or connection timed out. Kind: Timeout.</summary>
    public const string TimeoutCode = "data.timeout";
    /// <summary>Waiting for a row or table lock timed out. Kind: Timeout.</summary>
    public const string LockTimeoutCode = "data.lock_timeout";

    // ── Unavailable ─────────────────────────────────────────────────────────

    /// <summary>The database could not be reached. Kind: Unavailable.</summary>
    public const string UnavailableCode = "data.unavailable";
    /// <summary>The operation was cancelled by the caller. Kind: Unavailable.</summary>
    public const string CancelledCode = "data.cancelled";
    /// <summary>A lock requested with NoWait or an advisory lock could not be acquired. Kind: Unavailable.</summary>
    public const string LockNotAcquiredCode = "data.lock_not_acquired";
    /// <summary>The circuit breaker for the node is open. Kind: Unavailable.</summary>
    public const string CircuitOpenCode = "data.circuit_open";
    /// <summary>No healthy node can serve the request (all replicas and the primary are down or lagging). Kind: Unavailable.</summary>
    public const string NoHealthyNodeCode = "data.no_healthy_node";

    // ── Unauthorized ────────────────────────────────────────────────────────

    /// <summary>The server rejected the credentials or the user lacks permission. Kind: Unauthorized.</summary>
    public const string AccessDeniedCode = "data.access_denied";

    // ── Validation ──────────────────────────────────────────────────────────

    /// <summary>Invalid input or configuration. Kind: Validation.</summary>
    public const string ValidationCode = "data.validation";
    /// <summary>A LINQ expression cannot be translated to SQL. Kind: Validation.</summary>
    public const string TranslationFailedCode = "data.translation_failed";
    /// <summary>The provider does not support the requested feature. Kind: Validation.</summary>
    public const string UnsupportedCode = "data.unsupported";
    /// <summary>The operation needs a primary key and the entity has none. Kind: Validation.</summary>
    public const string NoPrimaryKeyCode = "data.no_primary_key";
    /// <summary>A single statement would exceed the provider's parameter limit. Kind: Validation.</summary>
    public const string ParameterLimitCode = "data.parameter_limit";
    /// <summary>The database schema differs from the entity model and the sync mode does not allow fixing it. Kind: Validation.</summary>
    public const string SchemaDriftCode = "data.schema_drift";
    /// <summary>A paging cursor is malformed or belongs to a different query. Kind: Validation.</summary>
    public const string InvalidCursorCode = "data.invalid_cursor";
    /// <summary>A connection names a provider that is not registered (its package or library is not loaded). Kind: Validation.</summary>
    public const string ProviderMissingCode = "data.provider_missing";
    /// <summary>A provider was built for a different CL.Data provider contract version. Kind: Validation.</summary>
    public const string ProviderIncompatibleCode = "data.provider_incompatible";
    /// <summary>A type is used as an entity but has no mapping (no generated accessor, and reflection mapping is off). Kind: Validation.</summary>
    public const string EntityNotMappedCode = "data.entity_not_mapped";
    /// <summary>A tenant-scoped entity was used without a current tenant. Kind: Validation.</summary>
    public const string TenantMissingCode = "data.tenant_missing";

    // ── Internal ────────────────────────────────────────────────────────────

    /// <summary>A database or driver failure with no narrower mapping. Kind: Internal.</summary>
    public const string ProviderErrorCode = "data.provider_error";
    /// <summary>Schema synchronization failed. Kind: Internal.</summary>
    public const string SchemaSyncFailedCode = "data.schema_sync_failed";
    /// <summary>A migration failed. Kind: Internal.</summary>
    public const string MigrationFailedCode = "data.migration_failed";
    /// <summary>A bulk copy failed. Kind: Internal.</summary>
    public const string BulkFailedCode = "data.bulk_failed";

    // ── Factories ───────────────────────────────────────────────────────────

    /// <summary>Creates a <see cref="NotFoundCode"/> error.</summary>
    public static Error NotFound(string message, string? details = null) => Error.NotFound(NotFoundCode, message, details);
    /// <summary>Creates a <see cref="ConnectionUnknownCode"/> error for <paramref name="connectionId"/>.</summary>
    public static Error ConnectionUnknown(string connectionId) =>
        Error.NotFound(ConnectionUnknownCode, $"No database connection named '{connectionId}' is configured.");

    /// <summary>Creates a <see cref="DuplicateKeyCode"/> error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Constraint or index name and provider diagnostics, when known.</param>
    public static Error DuplicateKey(string message, string? details = null) => Error.Conflict(DuplicateKeyCode, message, details);
    /// <summary>Creates a <see cref="ForeignKeyViolationCode"/> error.</summary>
    public static Error ForeignKeyViolation(string message, string? details = null) => Error.Conflict(ForeignKeyViolationCode, message, details);
    /// <summary>Creates a <see cref="ConstraintViolationCode"/> error.</summary>
    public static Error ConstraintViolation(string message, string? details = null) => Error.Conflict(ConstraintViolationCode, message, details);
    /// <summary>Creates a <see cref="ConflictCode"/> error.</summary>
    public static Error Conflict(string message, string? details = null) => Error.Conflict(ConflictCode, message, details);
    /// <summary>Creates a <see cref="ConcurrencyCode"/> error for a row of <paramref name="table"/>.</summary>
    public static Error Concurrency(string table, string? details = null) =>
        Error.Conflict(ConcurrencyCode, $"The row in '{table}' was changed or deleted by someone else.", details);
    /// <summary>Creates a <see cref="DeadlockCode"/> error.</summary>
    public static Error Deadlock(string? details = null) =>
        Error.Conflict(DeadlockCode, "The transaction was chosen as a deadlock victim; retry it.", details);

    /// <summary>Creates a <see cref="TimeoutCode"/> error.</summary>
    public static Error Timeout(string message, string? details = null) => Error.Timeout(TimeoutCode, message, details);
    /// <summary>Creates a <see cref="LockTimeoutCode"/> error.</summary>
    public static Error LockTimeout(string? details = null) =>
        Error.Timeout(LockTimeoutCode, "Timed out waiting for a lock.", details);

    /// <summary>Creates an <see cref="UnavailableCode"/> error.</summary>
    public static Error Unavailable(string message, string? details = null) => Error.Unavailable(UnavailableCode, message, details);
    /// <summary>Creates a <see cref="CancelledCode"/> error.</summary>
    public static Error Cancelled(string? details = null) =>
        Error.Unavailable(CancelledCode, "The database operation was cancelled.", details);
    /// <summary>Creates a <see cref="LockNotAcquiredCode"/> error.</summary>
    public static Error LockNotAcquired(string message, string? details = null) => Error.Unavailable(LockNotAcquiredCode, message, details);
    /// <summary>Creates a <see cref="CircuitOpenCode"/> error for <paramref name="node"/>.</summary>
    public static Error CircuitOpen(string node, string? details = null) =>
        Error.Unavailable(CircuitOpenCode, $"Database node '{node}' is temporarily disabled after repeated failures.", details);
    /// <summary>Creates a <see cref="NoHealthyNodeCode"/> error for <paramref name="connectionId"/>.</summary>
    public static Error NoHealthyNode(string connectionId, string? details = null) =>
        Error.Unavailable(NoHealthyNodeCode, $"No healthy database node is available for connection '{connectionId}'.", details);

    /// <summary>Creates an <see cref="AccessDeniedCode"/> error.</summary>
    public static Error AccessDenied(string message, string? details = null) => Error.Unauthorized(AccessDeniedCode, message, details);

    /// <summary>Creates a <see cref="ValidationCode"/> error.</summary>
    public static Error Validation(string message, string? details = null) => Error.Validation(ValidationCode, message, details);
    /// <summary>Creates a <see cref="TranslationFailedCode"/> error.</summary>
    /// <param name="message">What could not be translated, e.g. the method name.</param>
    /// <param name="details">The expression text, when helpful.</param>
    public static Error TranslationFailed(string message, string? details = null) => Error.Validation(TranslationFailedCode, message, details);
    /// <summary>Creates an <see cref="UnsupportedCode"/> error.</summary>
    public static Error Unsupported(string message, string? details = null) => Error.Validation(UnsupportedCode, message, details);
    /// <summary>Creates a <see cref="NoPrimaryKeyCode"/> error for <paramref name="entity"/>.</summary>
    public static Error NoPrimaryKey(string entity) =>
        Error.Validation(NoPrimaryKeyCode, $"Entity '{entity}' has no primary key, which this operation needs.");
    /// <summary>Creates a <see cref="ParameterLimitCode"/> error.</summary>
    public static Error ParameterLimit(int parameters, int limit) =>
        Error.Validation(ParameterLimitCode, $"The statement needs {parameters} parameters; the provider allows {limit}.");
    /// <summary>Creates a <see cref="SchemaDriftCode"/> error.</summary>
    public static Error SchemaDrift(string table, string? details = null) =>
        Error.Validation(SchemaDriftCode, $"Table '{table}' differs from its entity model and the sync mode does not allow the change.", details);
    /// <summary>Creates an <see cref="InvalidCursorCode"/> error.</summary>
    public static Error InvalidCursor(string message) => Error.Validation(InvalidCursorCode, message);
    /// <summary>Creates a <see cref="ProviderMissingCode"/> error for <paramref name="provider"/>.</summary>
    public static Error ProviderMissing(string provider) =>
        Error.Validation(ProviderMissingCode,
            $"No database provider named '{provider}' is registered. Install and load its package (for example CodeLogic.Data.MySql).");
    /// <summary>Creates a <see cref="ProviderIncompatibleCode"/> error.</summary>
    public static Error ProviderIncompatible(string provider, int providerVersion, int expectedVersion) =>
        Error.Validation(ProviderIncompatibleCode,
            $"Provider '{provider}' implements contract version {providerVersion}; this CL.Data needs {expectedVersion}. Update the provider package.");
    /// <summary>Creates an <see cref="EntityNotMappedCode"/> error for <paramref name="entity"/>.</summary>
    public static Error EntityNotMapped(string entity) =>
        Error.Validation(EntityNotMappedCode,
            $"Type '{entity}' has no generated mapping. Mark it partial with [Table] or [DataRow], or allow reflection mapping.");
    /// <summary>Creates a <see cref="TenantMissingCode"/> error for <paramref name="entity"/>.</summary>
    public static Error TenantMissing(string entity) =>
        Error.Validation(TenantMissingCode, $"Entity '{entity}' is tenant-scoped but no tenant is set.");

    /// <summary>Creates a <see cref="ProviderErrorCode"/> error.</summary>
    public static Error ProviderError(string message, string? details = null) => Error.Internal(ProviderErrorCode, message, details);
    /// <summary>Creates a <see cref="SchemaSyncFailedCode"/> error.</summary>
    public static Error SchemaSyncFailed(string table, string? details = null, Error? inner = null) =>
        Error.Internal(SchemaSyncFailedCode, $"Schema synchronization failed for '{table}'.", details, inner);
    /// <summary>Creates a <see cref="MigrationFailedCode"/> error.</summary>
    public static Error MigrationFailed(string migration, string? details = null, Error? inner = null) =>
        Error.Internal(MigrationFailedCode, $"Migration '{migration}' failed.", details, inner);
    /// <summary>Creates a <see cref="BulkFailedCode"/> error.</summary>
    public static Error BulkFailed(string table, string? details = null, Error? inner = null) =>
        Error.Internal(BulkFailedCode, $"Bulk copy into '{table}' failed.", details, inner);

    /// <summary>
    /// Maps an exception that no provider classifier recognised: cancellation becomes
    /// <see cref="CancelledCode"/>, a <see cref="TimeoutException"/> becomes <see cref="TimeoutCode"/>,
    /// anything else <see cref="ProviderErrorCode"/>. The exception message goes into the details,
    /// never the message.
    /// </summary>
    public static Error FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => Cancelled(),
            TimeoutException => Timeout("The database operation timed out.", exception.Message),
            _ => ProviderError("The database operation failed.", $"{exception.GetType().Name}: {exception.Message}"),
        };
    }
}
