using System.Text;
using System.Text.Json.Serialization;
using CL.Mqtt.Models;
using Xunit;

namespace Mqtt.Tests;

public sealed class MqttMessageTests
{
    public sealed record Reading(string Sensor, double Value);

    private static MqttMessage Message(string payload) => new("sensors/kitchen", Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void Payload_reads_as_text() => Assert.Equal("21.5 °C", Message("21.5 °C").PayloadAsString());

    [Fact]
    public void Payload_reads_as_json()
    {
        var reading = Message("""{"Sensor":"kitchen","Value":21.5}""").ReadJson<Reading>().ValueOrThrow();
        Assert.Equal(new Reading("kitchen", 21.5), reading);
    }

    [Fact]
    public void Payload_reads_as_json_with_source_generation()
    {
        var reading = Message("""{"Sensor":"hall","Value":19}""").ReadJson(TestJsonContext.Default.Reading).ValueOrThrow();
        Assert.Equal("hall", reading.Sensor);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Bad_json_is_an_error(string payload) =>
        Assert.Equal(MqttErrorCodes.InvalidJson, Message(payload).ReadJson<Reading>().Error!.Code);

    [Fact]
    public void Publish_option_presets()
    {
        Assert.Equal(MqttQos.AtLeastOnce, MqttPublishOptions.Default.Qos);
        Assert.False(MqttPublishOptions.Default.Retain);
        Assert.True(MqttPublishOptions.Retained.Retain);
    }
}

[JsonSerializable(typeof(MqttMessageTests.Reading))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
