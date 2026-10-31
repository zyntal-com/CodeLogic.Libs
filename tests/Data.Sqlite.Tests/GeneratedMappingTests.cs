using CL.Data.Mapping;
using Data.Conformance;

namespace Data.Sqlite.Tests;

/// <summary>The conformance models use generated mapping, and it equals reflection mapping.</summary>
public sealed class GeneratedMappingTests
{
    [Fact]
    public void Conformance_models_use_generated_accessors_that_match_reflection()
    {
        var types = MappingParity.GeneratedTypes(typeof(Product).Assembly);
        Assert.Contains(typeof(Product), types);
        Assert.Contains(typeof(ProductTag), types);
        Assert.Empty(types.SelectMany(MappingParity.Differences));
        if (System.Environment.GetEnvironmentVariable("CL_DATA_FORCE_REFLECTION") != "1")
            Assert.True(EntityAccessorRegistry.HasGenerated<Product>());
    }
}
