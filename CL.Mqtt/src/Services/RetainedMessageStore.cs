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
/// One writer runs at a time; a change that arrives during a write is written by the next round.
/// </summary>
internal sealed class RetainedMessageStore : IAsyncDisposable
{
    /// <summary>How long changes are collected before they are written.</summary>
    public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private List<MqttApplicationMessage>? _pending;
    private Task? _writer;
    private readonly Func<Task>? _beforeWrite;

    public RetainedMessageStore(string path, ILogger logger, Func<Task>? beforeWrite = null)
    {
        _path = path;
        _logger = logger;
        _beforeWrite = beforeWrite;
    }

    /// <summary>Reads the stored messages; an unreadable file is logged and treated as empty.</summary>
    public async Task<List<MqttApplicationMessage>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var stream = File.OpenRead(_path);
            await using var streamLifetime = stream.ConfigureAwait(false);
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
            _pending = all;
            _writer ??= Task.Run(WriteLoopAsync);
        }
    }

    /// <summary>Waits for the running write and writes anything still pending.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? writer;
        lock (_lock)
            writer = _writer;
        if (writer is not null)
            await writer.ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Writes the pending set after <see cref="WriteDelay"/>, again while new changes keep arriving, then exits.</summary>
    private async Task WriteLoopAsync()
    {
        while (true)
        {
            await Task.Delay(WriteDelay).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
            lock (_lock)
            {
                if (_pending is null)
                {
                    _writer = null;
                    return;
                }
            }
        }
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
            if (_beforeWrite is not null) await _beforeWrite().ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var stream = File.Create(temp);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, [.. snapshot.Select(ToEntry)], RetainedJsonContext.Default.ListRetainedEntry).ConfigureAwait(false);
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never let a failed write fault the writer task: the next change schedules another attempt.
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
