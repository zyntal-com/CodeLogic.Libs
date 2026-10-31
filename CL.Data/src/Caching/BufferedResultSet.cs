using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace CL.Data.Caching;

/// <summary>
/// A result set held in memory: column names, field types and raw row values as the driver returned them. Cached
/// queries keep these and replay them through <see cref="CreateReader"/>, so every hit materializes fresh objects
/// through the normal read path (converters included) and callers can never change what is cached.
/// </summary>
internal sealed class BufferedResultSet
{
    private BufferedResultSet(string[] names, Type[] types, object?[][] rows, long size)
    {
        Names = names;
        Types = types;
        Rows = rows;
        Size = size;
    }

    public string[] Names { get; }
    public Type[] Types { get; }
    public object?[][] Rows { get; }

    /// <summary>Approximate memory use in bytes (the cache's size limit counts these).</summary>
    public long Size { get; }

    /// <summary>Reads every row of <paramref name="reader"/> into memory.</summary>
    public static async ValueTask<BufferedResultSet> CaptureAsync(DbDataReader reader, CancellationToken ct)
    {
        var count = reader.FieldCount;
        var names = new string[count];
        var types = new Type[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = reader.GetName(i);
            types[i] = reader.GetFieldType(i);
        }

        List<object?[]> rows = [];
        long size = 64 + count * 48L;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[count];
            for (var i = 0; i < count; i++)
            {
                var value = reader.GetValue(i);
                row[i] = value is DBNull ? null : value;
                size += row[i] switch
                {
                    null => 8,
                    string s => 24 + s.Length * 2L,
                    byte[] b => 24 + b.Length,
                    _ => 24,
                };
            }
            rows.Add(row);
            size += 32;
        }
        return new BufferedResultSet(names, types, [.. rows], size);
    }

    /// <summary>A reader over the buffered rows.</summary>
    public DbDataReader CreateReader() => new Reader(this);

    private sealed class Reader(BufferedResultSet set) : DbDataReader
    {
        private int _row = -1;

        private object? Value(int ordinal) => set.Rows[_row][ordinal];

        public override int FieldCount => set.Names.Length;
        public override bool HasRows => set.Rows.Length > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read() => ++_row < set.Rows.Length;
        public override bool NextResult() => false;
        public override string GetName(int ordinal) => set.Names[ordinal];
        [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2063", Justification = "Same as IL2073: driver-provided field types.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2073",
            Justification = "The types come from the driver's own reader (GetFieldType), so they are already preserved.")]
        public override Type GetFieldType(int ordinal) => set.Types[ordinal];
        public override string GetDataTypeName(int ordinal) => set.Types[ordinal].Name;

        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < set.Names.Length; i++)
                if (string.Equals(set.Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException($"No column named '{name}'.");
        }

        public override bool IsDBNull(int ordinal) => Value(ordinal) is null;
        public override object GetValue(int ordinal) => Value(ordinal) ?? DBNull.Value;

        public override int GetValues(object[] values)
        {
            var n = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }

        public override T GetFieldValue<T>(int ordinal) => Value(ordinal) is T typed ? typed : (T)Convert.ChangeType(Value(ordinal)!, typeof(T), CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Value(ordinal), CultureInfo.InvariantCulture);
        public override byte GetByte(int ordinal) => Convert.ToByte(Value(ordinal), CultureInfo.InvariantCulture);
        public override char GetChar(int ordinal) => Convert.ToChar(Value(ordinal), CultureInfo.InvariantCulture);
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(Value(ordinal), CultureInfo.InvariantCulture);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(Value(ordinal), CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(Value(ordinal), CultureInfo.InvariantCulture);
        public override float GetFloat(int ordinal) => Convert.ToSingle(Value(ordinal), CultureInfo.InvariantCulture);
        public override Guid GetGuid(int ordinal) => Value(ordinal) is Guid g ? g : Guid.Parse(Convert.ToString(Value(ordinal), CultureInfo.InvariantCulture)!);
        public override short GetInt16(int ordinal) => Convert.ToInt16(Value(ordinal), CultureInfo.InvariantCulture);
        public override int GetInt32(int ordinal) => Convert.ToInt32(Value(ordinal), CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(Value(ordinal), CultureInfo.InvariantCulture);
        public override string GetString(int ordinal) => Convert.ToString(Value(ordinal), CultureInfo.InvariantCulture)!;

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            var bytes = (byte[])Value(ordinal)!;
            if (buffer is null) return bytes.Length;
            var n = (int)Math.Min(length, bytes.Length - dataOffset);
            Array.Copy(bytes, dataOffset, buffer, bufferOffset, n);
            return n;
        }

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            var text = GetString(ordinal);
            if (buffer is null) return text.Length;
            var n = (int)Math.Min(length, text.Length - dataOffset);
            text.CopyTo((int)dataOffset, buffer, bufferOffset, n);
            return n;
        }

        public override IEnumerator GetEnumerator() => new DbEnumerator(this);
    }
}
