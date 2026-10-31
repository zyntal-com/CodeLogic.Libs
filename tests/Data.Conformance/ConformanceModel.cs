using CL.Data;

namespace Data.Conformance;

/// <summary>Table names of the conformance model.</summary>
public static class ConformanceTables
{
    /// <summary>Products.</summary>
    public const string Products = "conf_products";
    /// <summary>Product tags (composite key).</summary>
    public const string Tags = "conf_tags";
    /// <summary>Tenant notes.</summary>
    public const string Notes = "conf_notes";
}

/// <summary>Product status, stored as text.</summary>
public enum ProductStatus
{
    /// <summary>Draft.</summary>
    Draft,
    /// <summary>Live.</summary>
    Live,
    /// <summary>Retired.</summary>
    Retired,
}

/// <summary>The main conformance entity: identity key, unique SKU, decimals, booleans, audit, token, soft delete.</summary>
[Table(Name = ConformanceTables.Products)]
public sealed class Product
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Unique SKU.</summary>
    [Column(Size = 64, Unique = true)] public string Sku { get; set; } = "";
    /// <summary>Name.</summary>
    [Column(Size = 200)] public string Name { get; set; } = "";
    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
    /// <summary>Price.</summary>
    [Column(Precision = 18, Scale = 2)] public decimal Price { get; set; }
    /// <summary>Stock.</summary>
    public int Stock { get; set; }
    /// <summary>Active flag.</summary>
    public bool Active { get; set; } = true;
    /// <summary>Status stored as text.</summary>
    [Column(Size = 16, Converter = typeof(CL.Data.Mapping.Conversion.EnumToStringConverter<ProductStatus>))]
    public ProductStatus Status { get; set; }
    /// <summary>External reference.</summary>
    public Guid Ref { get; set; } = Guid.NewGuid();
    /// <summary>Created.</summary>
    [CreatedAt] public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Updated.</summary>
    [UpdatedAt] public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Concurrency token.</summary>
    [ConcurrencyToken] public int Version { get; set; }
    /// <summary>Soft delete.</summary>
    [SoftDelete] public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A composite-key entity.</summary>
[Table(Name = ConformanceTables.Tags)]
public sealed class ProductTag
{
    /// <summary>Product.</summary>
    [Column(Primary = true, KeyOrder = 1)] public long ProductId { get; set; }
    /// <summary>Tag.</summary>
    [Column(Primary = true, KeyOrder = 2, Size = 32)] public string Tag { get; set; } = "";
    /// <summary>Weight.</summary>
    public int Weight { get; set; }
}

/// <summary>A tenant-scoped entity.</summary>
[Table(Name = ConformanceTables.Notes)]
public sealed class TenantNote
{
    /// <summary>Key.</summary>
    [Column(Primary = true)] public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Tenant.</summary>
    [TenantKey] public int TenantId { get; set; }
    /// <summary>Text.</summary>
    [Column(Size = 500)] public string Text { get; set; } = "";
}
