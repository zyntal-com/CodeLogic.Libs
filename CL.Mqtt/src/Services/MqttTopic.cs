using System.Text;
using MQTTnet;

namespace CL.Mqtt.Services;

/// <summary>
/// Topic names and topic filters, following the MQTT specification:
/// <list type="bullet">
///   <item><description>A <b>topic name</b> is where a message is published: <c>home/kitchen/temperature</c>. No wildcards.</description></item>
///   <item><description>A <b>topic filter</b> is what a subscription matches: <c>+</c> matches exactly one level
///     (<c>home/+/temperature</c>), <c>#</c> matches any number of trailing levels (<c>home/#</c>).</description></item>
/// </list>
/// Topics starting with <c>$</c> (such as <c>$SYS/...</c>) are not matched by a leading wildcard.
/// </summary>
public static class MqttTopic
{
    /// <summary>The longest topic or filter MQTT allows, in UTF-8 bytes.</summary>
    public const int MaxLength = 65_535;

    /// <summary>Checks a topic name to publish to.</summary>
    /// <returns>Null when valid; otherwise a description of the problem.</returns>
    public static string? ValidateTopic(string? topic)
    {
        if (CheckCommon(topic, "topic") is { } common)
            return common;
        return topic!.AsSpan().IndexOfAny('+', '#') >= 0
            ? $"Topic '{topic}' contains a wildcard; wildcards are only allowed in subscriptions."
            : null;
    }

    /// <summary>Checks a topic filter to subscribe to.</summary>
    /// <returns>Null when valid; otherwise a description of the problem.</returns>
    public static string? ValidateFilter(string? filter)
    {
        if (CheckCommon(filter, "filter") is { } common)
            return common;

        var levels = filter!.Split('/');
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            if (level.Contains('#', StringComparison.Ordinal) && (level != "#" || i != levels.Length - 1))
                return $"Filter '{filter}': '#' must be a whole level and the last one.";
            if (level.Contains('+', StringComparison.Ordinal) && level != "+")
                return $"Filter '{filter}': '+' must be a whole level.";
        }
        return null;
    }

    /// <summary>True when <paramref name="topic"/> matches <paramref name="filter"/>. Invalid input never matches.</summary>
    public static bool Matches(string topic, string filter) =>
        MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch;

    /// <summary>
    /// True when every topic matched by <paramref name="requested"/> is also matched by <paramref name="allowed"/>.
    /// Used for subscription permissions: <c>home/#</c> covers <c>home/+/state</c>, but <c>home/+</c> does not cover <c>home/#</c>.
    /// </summary>
    public static bool Covers(string allowed, string requested)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(requested);
        var a = allowed.Split('/');
        var r = requested.Split('/');

        // A leading wildcard does not match $-topics, so it cannot cover a filter that starts with '$'.
        if (r[0].StartsWith('$') && (a[0] == "#" || a[0] == "+"))
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "#")
                return true;                 // covers this level and everything below it
            if (i >= r.Length)
                return false;                // allowed is longer: requested matches shorter topics allowed does not
            if (r[i] == "#")
                return false;                // requested reaches deeper than allowed's fixed levels
            if (a[i] == "+")
                continue;                    // any single level, including a requested '+'
            if (r[i] == "+" || !string.Equals(a[i], r[i], StringComparison.Ordinal))
                return false;
        }
        return a.Length == r.Length;
    }

    private static string? CheckCommon(string? value, string what)
    {
        if (string.IsNullOrEmpty(value))
            return $"The {what} cannot be empty.";
        if (value.Contains('\0', StringComparison.Ordinal))
            return $"The {what} cannot contain a NUL character.";
        if (Encoding.UTF8.GetByteCount(value) > MaxLength)
            return $"The {what} is longer than {MaxLength} bytes.";
        return null;
    }
}
