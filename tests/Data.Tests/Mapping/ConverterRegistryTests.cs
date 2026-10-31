using CL.Data.Mapping.Conversion;

namespace Data.Tests.Mapping;

public sealed class ConverterRegistryTests
{
    private readonly record struct RegistryId(long Value);

    private sealed class IdConverter : ValueConverter<RegistryId, long>
    {
        public override long ToStore(RegistryId value) => value.Value;
        public override RegistryId FromStore(long value) => new(value);
    }

    [Fact]
    public void Nullable_lookup_and_removal_use_the_same_registration()
    {
        var converter = new IdConverter();
        ValueConverters.Register(converter);
        try
        {
            Assert.True(ValueConverters.TryGet(typeof(RegistryId?), out var found));
            Assert.Same(converter, found);
            Assert.True(ValueConverters.Unregister(typeof(RegistryId?)));
            Assert.False(ValueConverters.TryGet(typeof(RegistryId), out _));
            Assert.False(ValueConverters.Unregister(typeof(RegistryId?)));
        }
        finally
        {
            ValueConverters.Unregister(typeof(RegistryId));
        }
    }

    [Fact]
    public void Concurrent_registrations_do_not_lose_version_increments()
    {
        var before = ValueConverters.Version;
        try
        {
            Parallel.For(0, 10000, _ => ValueConverters.Register(new IdConverter()));
            Assert.True(ValueConverters.Version - before >= 10000);
        }
        finally
        {
            ValueConverters.Unregister(typeof(RegistryId));
        }
    }
}
