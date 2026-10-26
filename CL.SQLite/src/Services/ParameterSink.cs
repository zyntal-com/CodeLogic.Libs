namespace CL.SQLite.Services;

/// <summary>
/// Collects the parameters a <see cref="SQLiteColumnExpressionTranslator"/> pass emits for
/// captured values, so column expressions never splice user data into SQL text. Equal values
/// share one parameter name.
/// </summary>
internal sealed class SQLiteParameterSink
{
    private readonly string _prefix;
    private readonly Dictionary<string, object?> _parameters = new();
    private readonly Dictionary<object, string> _byValue = new();

    /// <summary>
    /// Initializes a new sink whose parameter names are <paramref name="prefix"/> followed by
    /// an ordinal (<c>@ex_0</c>, <c>@ex_1</c>, …).
    /// </summary>
    public SQLiteParameterSink(string prefix = "@ex_")
    {
        _prefix = prefix;
    }

    /// <summary>Parameters added so far, keyed by name (including the <c>@</c>).</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>
    /// Returns the parameter name bound to <paramref name="value"/>, adding it on first use.
    /// The value is converted with <see cref="SQLiteValueConverter.ToDbValue"/> — the same
    /// conversion the repository writes with — so enums bind as their underlying number,
    /// booleans as 1/0, and dates and GUIDs as the text CL.SQLite stores.
    /// </summary>
    public string Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_byValue.TryGetValue(value, out var existing))
            return existing;

        var name = $"{_prefix}{_parameters.Count}";
        _parameters[name] = SQLiteValueConverter.ToDbValue(value, value.GetType());
        _byValue[value] = name;
        return name;
    }

    /// <summary>Copies every collected parameter into <paramref name="target"/>.</summary>
    public void CopyTo(IDictionary<string, object?> target)
    {
        foreach (var kv in _parameters)
            target[kv.Key] = kv.Value;
    }
}
