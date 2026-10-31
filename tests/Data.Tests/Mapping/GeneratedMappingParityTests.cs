using CL.Data.Mapping;
using Data.Conformance;

namespace Data.Tests.Mapping;

/// <summary>Every type with generated mapping in this assembly maps exactly as reflection maps it.</summary>
public sealed class GeneratedMappingParityTests
{
    [Fact]
    public void Generated_maps_equal_reflection_maps()
    {
        var types = MappingParity.GeneratedTypes(typeof(GeneratedMappingParityTests).Assembly);
        Assert.Contains(typeof(TestUser), types);
        Assert.Empty(types.SelectMany(MappingParity.Differences));
    }
}
