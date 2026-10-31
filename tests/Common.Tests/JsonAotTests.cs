using System.Text.Json.Serialization;
using CL.Common.Data;
using Xunit;

namespace Common.Tests;

public sealed record AotPerson(string Name, int Age);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AotPerson))]
public sealed partial class AotJsonContext : JsonSerializerContext;

public class JsonHelperSourceGenTests
{
    [Fact]
    public void Type_info_round_trip()
    {
        var json = JsonHelper.Serialize(new AotPerson("Ada", 36), AotJsonContext.Default.AotPerson);
        Assert.Equal("""{"name":"Ada","age":36}""", json.Value);
        Assert.Equal(new AotPerson("Ada", 36), JsonHelper.Deserialize(json.Value!, AotJsonContext.Default.AotPerson).Value);
    }

    [Fact]
    public void Context_round_trip_and_missing_type()
    {
        var json = JsonHelper.Serialize(new AotPerson("Bob", 1), AotJsonContext.Default);
        Assert.True(json.IsSuccess);
        Assert.Equal("Bob", JsonHelper.Deserialize<AotPerson>(json.Value!, AotJsonContext.Default).Value!.Name);
        Assert.Equal("not_supported", JsonHelper.Serialize(42L, AotJsonContext.Default).Error!.Code);
    }

    [Fact]
    public async Task File_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-json-{Guid.NewGuid():N}", "p.json");
        try
        {
            Assert.True((await JsonHelper.SerializeToFile(new AotPerson("Cy", 2), path, AotJsonContext.Default.AotPerson)).IsSuccess);
            Assert.Equal(2, (await JsonHelper.DeserializeFromFile(path, AotJsonContext.Default.AotPerson)).Value!.Age);
            Assert.Equal("io.file_not_found", (await JsonHelper.DeserializeFromFile(path + ".x", AotJsonContext.Default.AotPerson)).Error!.Code);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
