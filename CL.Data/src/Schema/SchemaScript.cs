using System.Text;

namespace CL.Data.Schema;

/// <summary>The scripted changes for one table.</summary>
/// <param name="Table">Table name (schema-qualified when the provider has schemas).</param>
/// <param name="Changes">Changes the level allows, in order.</param>
/// <param name="Pending">Changes the level does not allow (not scripted; listed as comments).</param>
/// <param name="Statements">The DDL for <paramref name="Changes"/>, in order.</param>
public sealed record SchemaScriptTable(string Table, IReadOnlyList<SchemaChange> Changes, IReadOnlyList<SchemaChange> Pending, IReadOnlyList<string> Statements);

/// <summary>
/// DDL that would bring a database in line with its models, produced by
/// <see cref="SchemaSync.ScriptAsync(IReadOnlyList{CL.Data.Mapping.EntityMap}, SchemaSyncLevel, CancellationToken)"/>
/// without running anything.
/// </summary>
/// <param name="Provider">Provider name.</param>
/// <param name="Level">Sync level the script was planned at.</param>
/// <param name="SessionSetup">Statements to run on the session first (outside any transaction).</param>
/// <param name="SessionTeardown">Statements to run on the session last.</param>
/// <param name="Tables">Tables with changes, referenced tables first. Tables that already match are left out.</param>
public sealed record SchemaScript(string Provider, SchemaSyncLevel Level, IReadOnlyList<string> SessionSetup,
    IReadOnlyList<string> SessionTeardown, IReadOnlyList<SchemaScriptTable> Tables)
{
    /// <summary>True when every table matches its model (nothing to apply and nothing pending).</summary>
    public bool IsEmpty => Tables.Count == 0;

    /// <summary>True when some changes are outside <see cref="Level"/>.</summary>
    public bool HasPending => Tables.Any(t => t.Pending.Count > 0);

    /// <summary>Every statement in order, setup and teardown included (empty when there is nothing to apply).</summary>
    public IReadOnlyList<string> Statements =>
        Tables.All(t => t.Statements.Count == 0) ? [] : [.. SessionSetup, .. Tables.SelectMany(t => t.Statements), .. SessionTeardown];

    /// <summary>
    /// The script as SQL text: one statement per line ending in <c>;</c>, a comment per table listing its changes, and
    /// pending changes as comments. Empty when there is nothing to apply or report.
    /// </summary>
    public string ToSql()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        sb.Append("-- CL.Data schema script (").Append(Provider).Append(", level ").Append(Level).AppendLine(")");
        var hasStatements = Tables.Any(t => t.Statements.Count > 0);
        if (hasStatements)
            foreach (var statement in SessionSetup) sb.Append(statement).AppendLine(";");
        foreach (var table in Tables)
        {
            sb.AppendLine().Append("-- ").AppendLine(table.Table);
            foreach (var change in table.Changes) sb.Append("--   ").AppendLine(change.Describe());
            foreach (var change in table.Pending) sb.Append("--   PENDING (not allowed at ").Append(Level).Append("): ").AppendLine(change.Describe());
            foreach (var statement in table.Statements) sb.Append(statement).AppendLine(";");
        }
        if (hasStatements)
        {
            sb.AppendLine();
            foreach (var statement in SessionTeardown) sb.Append(statement).AppendLine(";");
        }
        return sb.ToString();
    }
}
