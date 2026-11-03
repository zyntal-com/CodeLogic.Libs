using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// An account of the embedded broker (<see cref="MqttBrokerConfig.Users"/>) with its topic permissions.
/// Permissions are topic filters, so wildcards work: <c>sensors/#</c>, <c>home/+/state</c>.
/// </summary>
public sealed class MqttBrokerUser
{
    /// <summary>The user name the client sends.</summary>
    [ConfigField(Label = "Username", Required = true, Order = 0)]
    public string Username { get; set; } = "";

    /// <summary>The password the client sends (compared in constant time).</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, Order = 1)]
    public string Password { get; set; } = "";

    /// <summary>Topics this user may publish to. Empty allows every topic.</summary>
    [ConfigField(Label = "Publish Topics", Description = "Topic filters; empty allows all.", Order = 2)]
    public List<string> PublishTopics { get; set; } = [];

    /// <summary>
    /// Topic filters this user may subscribe to. A subscription is allowed when one of these covers it:
    /// <c>home/#</c> covers <c>home/kitchen/+</c>, but <c>home/+</c> does not cover <c>home/#</c>. Empty allows every filter.
    /// </summary>
    [ConfigField(Label = "Subscribe Topics", Description = "Topic filters; empty allows all.", Order = 3)]
    public List<string> SubscribeTopics { get; set; } = [];
}
