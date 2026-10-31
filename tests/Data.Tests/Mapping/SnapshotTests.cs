using CL.Data;
using CL.Data.Mapping;
using CL.Data.Mapping.Reflection;
using CodeLogic.Core.Results;

namespace Data.Tests.Mapping;

[Table(Name = "snap_blobs")]
public sealed class SnapBlob
{
    [Column(Primary = true)] public int Id { get; set; }
    public byte[] Payload { get; set; } = [];
    public List<string> Tags { get; set; } = [];   // a reference type compared by reference
    public string? Note { get; set; }
}

public sealed class SnapshotTests
{
    public static TheoryData<string> Accessors => ["generated", "reflection"];

    private static IEntityAccessor<SnapBlob> Accessor(string kind) =>
        kind == "generated" ? EntityAccessorRegistry.Get<SnapBlob>().ValueOrThrow() : new ReflectionEntityAccessor<SnapBlob>();

    [Theory]
    [MemberData(nameof(Accessors))]
    public void In_place_byte_array_changes_are_seen(string kind)
    {
        var accessor = Accessor(kind);
        var blob = new SnapBlob { Id = 1, Payload = [1, 2, 3], Note = "n" };
        var snapshot = accessor.Snapshot(blob);
        Assert.Empty(snapshot.ChangedProperties(blob, accessor.Map));

        blob.Payload[0] = 9;           // same array, new content
        blob.Note = "n";               // same value
        Assert.Equal([nameof(SnapBlob.Payload)], snapshot.ChangedProperties(blob, accessor.Map));

        blob.Payload = [9, 2, 3];      // new array, equal content: not a change since the edit above
        var again = accessor.Snapshot(blob);
        blob.Payload = [9, 2, 3];
        blob.Note = null;
        Assert.Equal([nameof(SnapBlob.Note)], again.ChangedProperties(blob, accessor.Map));
    }
}
