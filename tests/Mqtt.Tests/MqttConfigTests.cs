using CL.Mqtt.Models;
using Xunit;

namespace Mqtt.Tests;

public sealed class MqttConfigTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.True(new MqttConfig().Validate().IsValid);

    [Fact]
    public void Disabled_config_is_always_valid()
    {
        var config = new MqttConfig { Enabled = false, DefaultConnection = "Missing" };
        config.Connections["x"] = new MqttConnectionConfig { Host = "" };
        Assert.True(config.Validate().IsValid);
    }

    [Fact]
    public void Default_connection_must_exist()
    {
        var config = new MqttConfig { DefaultConnection = "Main" };
        config.Connections["Other"] = new MqttConnectionConfig();
        var result = config.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.FieldErrors, e => e.Path == "defaultConnection");
    }

    [Fact]
    public void Connection_names_are_case_insensitive()
    {
        var config = new MqttConfig { DefaultConnection = "default" };
        config.Connections = new Dictionary<string, MqttConnectionConfig> { ["Default"] = new() };
        Assert.True(config.Validate().IsValid);
    }

    [Fact]
    public void Connection_settings_are_checked()
    {
        var config = new MqttConfig();
        config.Connections["Default"] = new MqttConnectionConfig
        {
            Host = " ",
            Port = 70000,
            ReconnectMinDelaySeconds = 30,
            ReconnectMaxDelaySeconds = 10,
            CleanSession = false,
            WillTopic = "status/#"
        };
        var paths = config.Validate().FieldErrors.Select(e => e.Path).ToList();
        Assert.Contains("connections.Default.host", paths);
        Assert.Contains("connections.Default.port", paths);
        Assert.Contains("connections.Default.reconnectMinDelaySeconds", paths);
        Assert.Contains("connections.Default.clientId", paths);
        Assert.Contains("connections.Default.willTopic", paths);
    }

    [Fact]
    public void Broker_settings_are_checked_when_enabled()
    {
        var config = new MqttConfig
        {
            Broker = new MqttBrokerConfig
            {
                Enabled = true,
                BindAddress = "not-an-ip",
                Users =
                [
                    new() { Username = "a", Password = "x" },
                    new() { Username = "a", Password = "y" },
                    new() { Username = "b", Password = "", PublishTopics = ["bad/#/filter"] }
                ]
            }
        };
        var errors = config.Validate().FieldErrors;
        Assert.Contains(errors, e => e.Path == "broker.bindAddress");
        Assert.Contains(errors, e => e.Message.Contains("listed twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path == "broker.users[2].password");
        Assert.Contains(errors, e => e.Message.Contains("'#'", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_broker_is_not_checked() =>
        Assert.True(new MqttConfig { Broker = new MqttBrokerConfig { Enabled = false, BindAddress = "nope" } }.Validate().IsValid);
}
