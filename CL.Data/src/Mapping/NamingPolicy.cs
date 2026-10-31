namespace CL.Data.Mapping;

/// <summary>
/// How table and column names are derived from class and property names when no explicit
/// <c>Name</c> is given on <c>[Table]</c> or <c>[Column]</c>. Set per connection.
/// </summary>
public enum NamingPolicy
{
    /// <summary>Use the C# name unchanged (<c>CreatedAt</c>).</summary>
    AsIs,

    /// <summary>Lower snake case (<c>created_at</c>).</summary>
    SnakeCase,

    /// <summary>Camel case (<c>createdAt</c>).</summary>
    CamelCase,
}
