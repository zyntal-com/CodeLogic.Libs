using CL.Mqtt.Services;
using Xunit;

namespace Mqtt.Tests;

public sealed class MqttTopicTests
{
    [Theory]
    [InlineData("home/kitchen/temperature")]
    [InlineData("/leading/slash")]
    [InlineData("a")]
    [InlineData("$SYS/broker/uptime")]
    public void Valid_topics_pass(string topic) => Assert.Null(MqttTopic.ValidateTopic(topic));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("home/+/temperature")]
    [InlineData("home/#")]
    [InlineData("nul\0char")]
    public void Invalid_topics_are_rejected(string? topic) => Assert.NotNull(MqttTopic.ValidateTopic(topic));

    [Fact]
    public void Overlong_topic_is_rejected() => Assert.NotNull(MqttTopic.ValidateTopic(new string('a', 65_536)));

    [Theory]
    [InlineData("home/+/temperature")]
    [InlineData("home/#")]
    [InlineData("#")]
    [InlineData("+")]
    [InlineData("+/+/#")]
    [InlineData("home/kitchen")]
    public void Valid_filters_pass(string filter) => Assert.Null(MqttTopic.ValidateFilter(filter));

    [Theory]
    [InlineData("home/#/temperature")]
    [InlineData("home/kit#")]
    [InlineData("home/kit+chen")]
    [InlineData("")]
    public void Invalid_filters_are_rejected(string filter) => Assert.NotNull(MqttTopic.ValidateFilter(filter));

    [Theory]
    [InlineData("home/kitchen/temperature", "home/+/temperature", true)]
    [InlineData("home/kitchen/temperature", "home/#", true)]
    [InlineData("home", "home/#", true)]
    [InlineData("home/kitchen/temperature", "home/+", false)]
    [InlineData("office/kitchen/temperature", "home/#", false)]
    [InlineData("$SYS/uptime", "#", false)]
    [InlineData("$SYS/uptime", "$SYS/#", true)]
    public void Matching_follows_the_spec(string topic, string filter, bool expected) =>
        Assert.Equal(expected, MqttTopic.Matches(topic, filter));

    [Theory]
    [InlineData("home/#", "home/kitchen/+", true)]
    [InlineData("home/#", "home/#", true)]
    [InlineData("home/#", "home", true)]
    [InlineData("home/+", "home/kitchen", true)]
    [InlineData("home/+", "home/+", true)]
    [InlineData("home/+", "home/#", false)]
    [InlineData("home/+", "home/kitchen/light", false)]
    [InlineData("home/kitchen", "home/+", false)]
    [InlineData("#", "anything/at/all", true)]
    [InlineData("#", "$SYS/#", false)]
    [InlineData("home/+/state", "home/+/state", true)]
    [InlineData("home/+/state", "home/+/set", false)]
    public void Covers_checks_filter_inclusion(string allowed, string requested, bool expected) =>
        Assert.Equal(expected, MqttTopic.Covers(allowed, requested));
}
