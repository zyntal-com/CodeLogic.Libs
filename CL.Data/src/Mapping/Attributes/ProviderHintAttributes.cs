namespace CL.Data;

// Provider hints live in the core package so entities can carry them without referencing a driver.
// A provider reads its own hints and ignores the others.

/// <summary>MySQL table options.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class MySqlTableAttribute : Attribute
{
    /// <summary>Storage engine (null = connection default, usually InnoDB).</summary>
    public string? Engine { get; set; }

    /// <summary>Table character set.</summary>
    public string? Charset { get; set; }

    /// <summary>Table collation.</summary>
    public string? Collation { get; set; }

    /// <summary>Row format (<c>DYNAMIC</c>, <c>COMPRESSED</c>, ...).</summary>
    public string? RowFormat { get; set; }
}

/// <summary>MySQL column options.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MySqlColumnAttribute : Attribute
{
    /// <summary>Exact native type, e.g. <c>MEDIUMTEXT</c>.</summary>
    public string? Type { get; set; }

    /// <summary>UNSIGNED integer.</summary>
    public bool Unsigned { get; set; }

    /// <summary>
    /// Column character set. <b>Not applied yet</b>: schema sync does not emit it. Set <see cref="Collation"/> instead (a
    /// MySQL collation implies its character set, for example <c>ascii_bin</c>).
    /// </summary>
    [Obsolete("Not applied by schema sync yet; set Collation (which implies the character set) instead.")]
    public string? Charset { get; set; }

    /// <summary>Column collation.</summary>
    public string? Collation { get; set; }
}

/// <summary>PostgreSQL table options, applied when schema sync creates the table (an existing table is not altered).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class PostgresTableAttribute : Attribute
{
    /// <summary>UNLOGGED table (faster, not crash-safe, not replicated).</summary>
    public bool Unlogged { get; set; }

    /// <summary>Tablespace.</summary>
    public string? Tablespace { get; set; }

    /// <summary>Fill factor (10-100).</summary>
    public int FillFactor { get; set; }
}

/// <summary>PostgreSQL column options.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PostgresColumnAttribute : Attribute
{
    /// <summary>Exact native type, e.g. <c>jsonb</c>, <c>inet</c>, <c>text[]</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Column collation.</summary>
    public string? Collation { get; set; }
}

/// <summary>SQL Server table options, applied when schema sync creates the table (an existing table is not altered).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SqlServerTableAttribute : Attribute
{
    /// <summary>File group.</summary>
    public string? FileGroup { get; set; }

    /// <summary>Data compression (<c>NONE</c>, <c>ROW</c>, <c>PAGE</c>; other values are ignored).</summary>
    public string? DataCompression { get; set; }
}

/// <summary>SQL Server column options.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqlServerColumnAttribute : Attribute
{
    /// <summary>Exact native type, e.g. <c>nvarchar(max)</c>, <c>datetime2(3)</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Column collation.</summary>
    public string? Collation { get; set; }

    /// <summary>SPARSE column. <b>Not applied yet</b>: schema sync does not emit it.</summary>
    [Obsolete("Not applied by schema sync yet.")]
    public bool Sparse { get; set; }
}

/// <summary>SQLite table options.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SqliteTableAttribute : Attribute
{
    /// <summary>WITHOUT ROWID table (needs a primary key).</summary>
    public bool WithoutRowId { get; set; }

    /// <summary>STRICT table (column types enforced).</summary>
    public bool Strict { get; set; }
}

/// <summary>SQLite column options.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqliteColumnAttribute : Attribute
{
    /// <summary>Exact declared type.</summary>
    public string? Type { get; set; }

    /// <summary>Collation (<c>NOCASE</c>, <c>RTRIM</c>, <c>BINARY</c>).</summary>
    public string? Collation { get; set; }
}
