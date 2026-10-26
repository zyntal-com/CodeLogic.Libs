using CL.MSSQL.Models;

namespace CL.MSSQL.Core;

/// <summary>
/// Collects the parameters a <see cref="SqlExpressionTranslator"/> pass emits for captured
/// values, so column expressions never splice user data into SQL text. Equal values share
/// one parameter name: a grouped query translates its key once for <c>GROUP BY</c> and again
/// for <c>g.Key</c> in the SELECT list, and the server only treats the two as the same
/// expression when their text is identical.
/// </summary>
internal sealed class SqlParameterSink
{
    private readonly string _prefix;
    private readonly Dictionary<string, object?> _parameters = new();
    private readonly Dictionary<(object Value, StorageType StorageType), string> _byValue = new();

    public SqlParameterSink(string prefix = "@ex_")
    {
        _prefix = prefix;
    }

    /// <summary>Parameters added so far, keyed by name (including the <c>@</c>).</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>
    /// Returns the parameter name bound to <paramref name="value"/>, adding it on first use.
    /// The value is converted with <see cref="TypeConverter.ToDbValue(object?, StorageType)"/>,
    /// the same conversion WHERE predicates apply.
    /// </summary>
    public string Add(object value, StorageType storageType = StorageType.Default)
    {
        var key = (value, storageType);
        if (_byValue.TryGetValue(key, out var existing))
            return existing;

        var name = $"{_prefix}{_parameters.Count}";
        _parameters[name] = TypeConverter.ToDbValue(value, storageType);
        _byValue[key] = name;
        return name;
    }

    /// <summary>Copies every collected parameter into <paramref name="target"/>.</summary>
    public void CopyTo(IDictionary<string, object?> target)
    {
        foreach (var kv in _parameters)
            target[kv.Key] = kv.Value;
    }
}
