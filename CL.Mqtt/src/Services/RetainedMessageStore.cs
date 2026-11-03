using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLogic.Core.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace CL.Mqtt.Services;

/// <summary>
/// Keeps the broker's retained messages in a JSON file. Changes are written at most every
/// <see cref="WriteDelay"/> (a burst of retained publishes costs one write), and once more on stop.
/// The file is written to a temporary name and then moved, so a crash never leaves half a file.
/// </summary>
internal sealed class RetainedMessageStore : IAsyncDisposable
{
    /// <summary>How long changes are collected before they are written.</summary>
    public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private List<MqttApplicationMessage>? _pending;
    private Task _writer = Task.CompletedTask;

    public RetainedMessageStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Reads the stored messages; an unreadable file is logged and treated as empty.</summary>
    public async Task<List<MqttApplicationMessage>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            var entries = await JsonSerializer.DeserializeAsync(stream, RetainedJsonContext.Default.ListRetainedEntry, cancellationToken).ConfigureAwait(false) ?? [];
            return [.. entries.Where(e => !string.IsNullOrEmpty(e.Topic)).Select(ToMessage)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.Warning($"MQTT broker: could not read retained messages from {_path}, starting empty: {ex.Message}");
            return [];
        }
    }

    /// <summary>Schedules a write of the full retained set (the broker passes the whole set on every change).</summary>
    public void Schedule(List<MqttApplicationMessage> all)
    {
        lock (_lock)
        {
            var writerIdle = _pending is null;
            _pending = all;
            if (writerIdle)
                _writer = Task.Run(WriteSoonAsync);
        }
    }

    /// <summary>Writes anything still pending.</summary>
    public async ValueTask DisposeAsync()
    {
        await _writer.ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteSoonAsync()
    {
        await Task.Delay(WriteDelay).ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    private async Task FlushAsync()
    {
        List<MqttApplicationMessage>? snapshot;
        lock (_lock)
        {
            snapshot = _pending;
            _pending = null;
        }
        if (snapshot is null)
            return;

        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, [.. snapshot.Select(ToEntry)], RetainedJsonContext.Default.ListRetainedEntry).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"MQTT broker: could not save retained messages to {_path}: {ex.Message}");
        }
    }

    private static RetainedEntry ToEntry(MqttApplicationMessage m) =>
        new(m.Topic, Convert.ToBase64String(m.Payload.ToArray()), (int)m.QualityOfServiceLevel, m.ContentType);

    private static MqttApplicationMessage ToMessage(RetainedEntry e)
    {
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(e.Topic)
            .WithPayload(Convert.FromBase64String(e.Payload ?? ""))
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(e.Qos, 0, 2))
            .WithRetainFlag(true);
        if (!string.IsNullOrEmpty(e.ContentType))
            builder.WithContentType(e.ContentType);
        return builder.Build();
    }
}

/// <summary>One retained message as stored on disk. The payload is base64 because it can be any bytes.</summary>
internal sealed record RetainedEntry(string Topic, string? Payload, int Qos, string? ContentType);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(List<RetainedEntry>))]
internal sealed partial class RetainedJsonContext : JsonSerializerContext;
