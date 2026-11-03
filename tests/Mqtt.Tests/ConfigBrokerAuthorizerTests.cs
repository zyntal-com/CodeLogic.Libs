using CL.Mqtt.Models;
using CL.Mqtt.Services;
using Xunit;

namespace Mqtt.Tests;

public sealed class ConfigBrokerAuthorizerTests
{
    private static readonly MqttBrokerConfig Config = new()
    {
        Enabled = true,
        AllowAnonymous = false,
        Users =
        [
            new() { Username = "sensor", Password = "s3cret", PublishTopics = ["sensors/#"], SubscribeTopics = ["commands/sensor/+"] },
            new() { Username = "admin", Password = "root" }
        ]
    };

    private static MqttBrokerConnectRequest Request(string? user, string? password) =>
        new("client-1", user, password, "127.0.0.1:50000", false);

    [Fact]
    public async Task Credentials_must_match_a_configured_user()
    {
        var authorizer = new ConfigBrokerAuthorizer(Config);
        Assert.True(await authorizer.AuthenticateAsync(Request("sensor", "s3cret"), default));
        Assert.False(await authorizer.AuthenticateAsync(Request("sensor", "wrong"), default));
        Assert.False(await authorizer.AuthenticateAsync(Request("nobody", "s3cret"), default));
        Assert.False(await authorizer.AuthenticateAsync(Request("Sensor", "s3cret"), default));
    }

    [Fact]
    public async Task Anonymous_clients_follow_the_setting()
    {
        var authorizer = new ConfigBrokerAuthorizer(Config);
        Assert.False(await authorizer.AuthenticateAsync(Request(null, null), default));
        Assert.False(authorizer.CanPublish(null, "c", "any/topic"));

        authorizer.Update(new MqttBrokerConfig { Enabled = true, AllowAnonymous = true });
        Assert.True(await authorizer.AuthenticateAsync(Request(null, null), default));
        Assert.True(authorizer.CanPublish(null, "c", "any/topic"));
        Assert.True(authorizer.CanSubscribe(null, "c", "#"));
    }

    [Fact]
    public void Topic_permissions_limit_users()
    {
        var authorizer = new ConfigBrokerAuthorizer(Config);
        Assert.True(authorizer.CanPublish("sensor", "c", "sensors/kitchen/temperature"));
        Assert.False(authorizer.CanPublish("sensor", "c", "commands/sensor/1"));
        Assert.True(authorizer.CanSubscribe("sensor", "c", "commands/sensor/1"));
        Assert.True(authorizer.CanSubscribe("sensor", "c", "commands/sensor/+"));
        Assert.False(authorizer.CanSubscribe("sensor", "c", "commands/#"));
        Assert.True(authorizer.CanPublish("admin", "c", "anything"));
        Assert.True(authorizer.CanSubscribe("admin", "c", "#"));
        Assert.False(authorizer.CanPublish("ghost", "c", "sensors/x"));
    }

    [Fact]
    public void Connect_request_hides_the_password() =>
        Assert.DoesNotContain("s3cret", Request("sensor", "s3cret").ToString(), StringComparison.Ordinal);
}
