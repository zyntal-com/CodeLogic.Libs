using CL.Mqtt.Models;
using CL.Mqtt.Services;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Mqtt;

/// <summary>
/// <b>CL.Mqtt</b> — CodeLogic library for MQTT: named client connections with automatic reconnect, and an optional
/// embedded broker.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="MqttConfig"/> (→ config.mqtt.json).</description></item>
///   <item><description><b>Initialize</b> — validates the config and creates one <see cref="MqttClientService"/> per enabled
///     connection, and the <see cref="MqttBrokerService"/> when the broker is enabled. Nothing connects yet, so you can
///     register subscriptions or replace <see cref="MqttBrokerService.Authorizer"/> between Initialize and Start.</description></item>
///   <item><description><b>Start</b> — starts the broker (a broker that cannot listen fails the start), then connects every
///     connection with <see cref="MqttConnectionConfig.AutoConnect"/>. A broker that cannot be reached does not fail
///     the start: the client keeps retrying in the background and the health check reports it.</description></item>
///   <item><description><b>Stop</b> — disconnects the clients, then stops the broker and saves its retained messages.</description></item>
/// </list>
/// Config changes made while running are applied live — see <see cref="OnConfigChangedAsync"/>.
/// </para>
/// </summary>
public sealed class MqttLibrary : ILibrary, IConfigReloadable, IAsyncDisposable
{
    private const string RetainedFileName = "retained.json";

    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Mqtt",
        Name = "MQTT Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "MQTT clients with automatic reconnect and an embedded MQTT broker",
        Author = "Zyntal.com",
        Tags = ["mqtt", "iot", "broker", "pubsub"]
    };

    private readonly Lock _lock = new();
    private Dictionary<string, MqttClientService> _clients = [with(StringComparer.OrdinalIgnoreCase)];
    private LibraryContext? _context;
    private MqttConfig? _config;
    private MqttBrokerService? _broker;
    private bool _started;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");
        context.Configuration.Register<MqttConfig>("mqtt");
        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var config = context.Configuration.Get<MqttConfig>();
        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        var validation = config.Validate();
        if (!validation.IsValid)
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {string.Join("; ", validation.Errors)}");

        BuildServices(context, config);
        context.Logger.Info($"{Manifest.Name} initialized: {_clients.Count} connection(s), broker {(_broker is null ? "off" : "on")}");
        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (_broker is not null)
        {
            var started = await _broker.StartAsync().ConfigureAwait(false);
            if (started.IsFailure)
                throw new InvalidOperationException($"{Manifest.Name}: {started.Error!.Message} {started.Error.Details}");
        }

        // Connect in parallel: one slow or unreachable broker must not hold up the others.
        await Task.WhenAll(SnapshotClients().Where(c => c.Config.AutoConnect).Select(ConnectAndLogAsync)).ConfigureAwait(false);
        _started = true;
        context.Logger.Info($"{Manifest.Name} started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");
        _started = false;
        await TearDownAsync().ConfigureAwait(false);
        _context?.Logger.Info($"{Manifest.Name} stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <summary>
    /// Healthy when the broker (if enabled) is running and every auto-connect connection is connected;
    /// Degraded when a connection is down (it keeps retrying); Unhealthy when the broker is not running.
    /// </summary>
    public Task<HealthStatus> HealthCheckAsync()
    {
        if (_config is null || !_config.Enabled)
            return Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is disabled"));
        if (_broker is not null && _started && !_broker.IsRunning)
            return Task.FromResult(HealthStatus.Unhealthy($"MQTT broker on {_broker.Endpoint} is not running"));

        var clients = SnapshotClients();
        var down = clients.Where(c => c.Config.AutoConnect && !c.IsConnected).ToList();
        var broker = _broker is null ? "" : $", broker on {_broker.Endpoint}";
        if (down.Count > 0)
        {
            var names = string.Join(", ", down.Select(c => $"{c.Name} ({c.LastError ?? c.State.ToString()})"));
            return Task.FromResult(HealthStatus.Degraded($"Not connected: {names}"));
        }
        return Task.FromResult(HealthStatus.Healthy($"{clients.Count} connection(s) up{broker}"));
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart, touching only what changed:
    /// <list type="bullet">
    ///   <item><description>an added connection is created (and connected when <see cref="MqttConnectionConfig.AutoConnect"/>);</description></item>
    ///   <item><description>a removed (or disabled) connection is disconnected and disposed — code still holding it gets
    ///     <see cref="MqttErrorCodes.Disposed"/>;</description></item>
    ///   <item><description>a changed connection is reconfigured in place: it disconnects and reconnects with the new
    ///     settings, keeping its subscriptions, so references to it stay valid;</description></item>
    ///   <item><description>broker user changes apply at once; listener changes restart the broker.</description></item>
    /// </list>
    /// <see cref="MqttConfig.Enabled"/> can be switched either way. A configuration that fails
    /// <see cref="MqttConfig.Validate"/> is logged and ignored: everything keeps the last valid one.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);
        if (change.NewValue is not MqttConfig current)
            return;

        var validation = current.Validate();
        if (!validation.IsValid)
        {
            context.Logger.Error($"{Manifest.Name}: ignored invalid configuration, keeping the current one: {string.Join("; ", validation.Errors)}");
            return;
        }

        if (!current.Enabled)
        {
            if (_config is null)
                return;
            await TearDownAsync().ConfigureAwait(false);
            context.Logger.Info($"{Manifest.Name} disabled by a config change");
            return;
        }

        if (_config is null)
        {
            BuildServices(context, current);
            if (_started)
                await StartServicesAsync(cancellationToken).ConfigureAwait(false);
            context.Logger.Info($"{Manifest.Name} enabled by a config change");
            return;
        }

        var previous = _config;
        _config = current;
        await ApplyConnectionChangesAsync(context, previous, current, cancellationToken).ConfigureAwait(false);
        await ApplyBrokerChangesAsync(context, previous, current, cancellationToken).ConfigureAwait(false);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>The default connection (<see cref="MqttConfig.DefaultConnection"/>).</summary>
    /// <returns>The client, or <see cref="MqttErrorCodes.ConnectionNotFound"/>.</returns>
    public Result<MqttClientService> Client() => Client(_config?.DefaultConnection ?? "Default");

    /// <summary>A configured connection by name (case-insensitive).</summary>
    /// <returns>The client, or <see cref="MqttErrorCodes.ConnectionNotFound"/>.</returns>
    public Result<MqttClientService> Client(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
            return _clients.TryGetValue(name, out var client) ? client : MqttErrors.ConnectionNotFound(name);
    }

    /// <summary>Every configured connection, by name.</summary>
    public IReadOnlyDictionary<string, MqttClientService> Clients
    {
        get { lock (_lock) return new Dictionary<string, MqttClientService>(_clients, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>True when the embedded broker is enabled.</summary>
    public bool HasBroker => _broker is not null;

    /// <summary>The embedded broker.</summary>
    /// <exception cref="InvalidOperationException">The broker is not enabled in <c>config.mqtt.json</c>.</exception>
    public MqttBrokerService Broker =>
        _broker ?? throw new InvalidOperationException("The embedded MQTT broker is not enabled. Set broker.enabled in config.mqtt.json.");

    /// <summary>
    /// Creates a client that is not in the configuration (for example one per device or per tenant). The library
    /// does not track it: the caller connects it and disposes it.
    /// </summary>
    public MqttClientService CreateClient(string name, MqttConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var context = _context ?? throw new InvalidOperationException($"{Manifest.Name} has not been configured yet.");
        return new MqttClientService(name, config, context.Logger, context.Events);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Disconnects every client and stops the broker. <see cref="OnStopAsync"/> already did this in a normal shutdown.</summary>
    public ValueTask DisposeAsync() => new(TearDownAsync());

    /// <summary>
    /// Synchronous dispose for hosts that cannot await. Prefer <see cref="DisposeAsync"/>; after <see cref="OnStopAsync"/>
    /// there is nothing left to do and this returns at once.
    /// </summary>
    public void Dispose()
    {
        bool idle;
        lock (_lock)
            idle = _clients.Count == 0 && _broker is null;
        if (idle)
            return;
        // Off the caller's context, so a synchronization context cannot deadlock the awaits inside.
        Task.Run(TearDownAsync).GetAwaiter().GetResult();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void BuildServices(LibraryContext context, MqttConfig config)
    {
        _config = config;
        lock (_lock)
        {
            _clients = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, connection) in config.EnabledConnections)
                _clients[name] = new MqttClientService(name, connection, context.Logger, context.Events);
        }
        _broker = config.Broker.Enabled
            ? new MqttBrokerService(config.Broker, context.Logger, context.Events, Path.Combine(context.DataDirectory, RetainedFileName))
            : null;
    }

    private async Task StartServicesAsync(CancellationToken cancellationToken)
    {
        if (_broker is not null)
        {
            var started = await _broker.StartAsync(cancellationToken).ConfigureAwait(false);
            if (started.IsFailure)
                _context?.Logger.Error($"{Manifest.Name}: {started.Error!.Message} {started.Error.Details}");
        }
        await Task.WhenAll(SnapshotClients().Where(c => c.Config.AutoConnect).Select(ConnectAndLogAsync)).ConfigureAwait(false);
    }

    private async Task ApplyConnectionChangesAsync(LibraryContext context, MqttConfig previous, MqttConfig current, CancellationToken cancellationToken)
    {
        var diff = ConfigDiff.Keyed(
            previous.EnabledConnections.ToDictionary(StringComparer.OrdinalIgnoreCase),
            current.EnabledConnections.ToDictionary(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in diff.Removed.Keys)
        {
            MqttClientService? removed;
            lock (_lock)
            {
                _clients.Remove(name, out removed);
            }
            if (removed is not null)
            {
                await removed.DisposeAsync().ConfigureAwait(false);
                context.Logger.Info($"MQTT connection '{name}' removed");
            }
        }

        foreach (var (name, change) in diff.Changed)
        {
            if (Client(name).Value is { } client)
            {
                var result = await client.ReconfigureAsync(change.New, cancellationToken).ConfigureAwait(false);
                context.Logger.Info($"MQTT connection '{name}' reconfigured{(result.IsFailure ? $" (not connected yet: {result.Error!.Message})" : "")}");
            }
        }

        foreach (var (name, connection) in diff.Added)
        {
            var client = new MqttClientService(name, connection, context.Logger, context.Events);
            lock (_lock)
                _clients[name] = client;
            if (_started && connection.AutoConnect)
                await ConnectAndLogAsync(client).ConfigureAwait(false);
            context.Logger.Info($"MQTT connection '{name}' added");
        }
    }

    private async Task ApplyBrokerChangesAsync(LibraryContext context, MqttConfig previous, MqttConfig current, CancellationToken cancellationToken)
    {
        if (ConfigDiff.ValueEquals(previous.Broker, current.Broker))
            return;

        if (!current.Broker.Enabled)
        {
            if (_broker is not null)
            {
                await _broker.DisposeAsync().ConfigureAwait(false);
                _broker = null;
                context.Logger.Info("MQTT broker disabled by a config change");
            }
            return;
        }

        if (_broker is null)
        {
            _broker = new MqttBrokerService(current.Broker, context.Logger, context.Events, Path.Combine(context.DataDirectory, RetainedFileName));
            if (_started)
                await StartBrokerAndLogAsync(_broker, cancellationToken).ConfigureAwait(false);
            context.Logger.Info("MQTT broker enabled by a config change");
            return;
        }

        var result = await _broker.ReconfigureAsync(current.Broker, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            context.Logger.Error($"MQTT broker could not apply the new settings: {result.Error!.Message} {result.Error.Details}");
    }

    private async Task StartBrokerAndLogAsync(MqttBrokerService broker, CancellationToken cancellationToken)
    {
        var started = await broker.StartAsync(cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
            _context?.Logger.Error($"{Manifest.Name}: {started.Error!.Message} {started.Error.Details}");
    }

    private async Task ConnectAndLogAsync(MqttClientService client)
    {
        var result = await client.ConnectAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            var retry = client.Config.AutoReconnect ? "; retrying in the background" : "";
            _context?.Logger.Warning($"MQTT connection '{client.Name}' did not connect at start: {result.Error!.Message}{retry}");
        }
    }

    private List<MqttClientService> SnapshotClients()
    {
        lock (_lock)
            return [.. _clients.Values];
    }

    private async Task TearDownAsync()
    {
        List<MqttClientService> clients;
        lock (_lock)
        {
            clients = [.. _clients.Values];
            _clients = new(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var client in clients)
            await client.DisposeAsync().ConfigureAwait(false);

        if (_broker is not null)
        {
            await _broker.DisposeAsync().ConfigureAwait(false);
            _broker = null;
        }
        _config = null;
    }
}
