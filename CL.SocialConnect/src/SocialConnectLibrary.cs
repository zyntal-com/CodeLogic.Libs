using CL.SocialConnect.Models;
using CL.SocialConnect.Services.Discord;
using CL.SocialConnect.Services.Steam;
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

namespace CL.SocialConnect;

/// <summary>
/// <b>CL.SocialConnect</b> — CodeLogic library providing Discord webhook messaging
/// and Steam profile, authentication and sign-in services.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="SocialConnectConfig"/> (<c>config.socialconnect.json</c>).</description></item>
///   <item><description><b>Initialize</b> — validates config, creates services based on enabled flags.</description></item>
///   <item><description><b>Start</b> — logs that the library is ready.</description></item>
///   <item><description><b>Stop</b> — disposes HTTP clients and clears state.</description></item>
/// </list>
/// Config changes made while running (admin UI, reload, file watcher) are applied live —
/// see <see cref="OnConfigChangedAsync"/>.
/// </para>
/// <para>
/// Access services via the public properties:
/// <list type="bullet">
///   <item><see cref="Discord"/> — <see cref="DiscordWebhookService"/> for sending webhook messages.</item>
///   <item><see cref="Steam"/> — <see cref="SteamProfileService"/> for player profiles, bans, and games.</item>
///   <item><see cref="Auth"/> — <see cref="SteamAuthenticationService"/> for ticket-based Steam auth.</item>
///   <item><see cref="OpenId"/> — <see cref="SteamOpenIdService"/> for "Sign in through Steam" on websites.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SocialConnectLibrary : ILibrary, IConfigReloadable
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.SocialConnect",
        Name = "Social Connect Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Discord webhook messaging and Steam profile, authentication and sign-in services",
        Author = "Zyntal.com",
        Tags = ["discord", "steam", "webhook", "social", "gaming"]
    };

    private LibraryContext? _context;
    private DiscordWebhookService? _discord;
    private SteamProfileService? _steam;
    private SteamAuthenticationService? _auth;
    private SteamOpenIdService? _openId;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<SocialConnectConfig>("socialconnect");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<SocialConnectConfig>();

        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join(", ", validation.Errors);
            context.Logger.Error($"{Manifest.Name} configuration is invalid: {errors}");
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        BuildDiscord(context, config);
        BuildSteam(context, config);

        context.Logger.Info($"{Manifest.Name} initialized");
        return Task.CompletedTask;
    }

    private void BuildDiscord(LibraryContext context, SocialConnectConfig config)
    {
        _discord = null;
        if (!config.Enabled || !config.Discord.Enabled) return;

        _discord = new DiscordWebhookService(config.Discord, context.Logger, context.Events);
        context.Logger.Info("Discord webhook service initialized");
    }

    private void BuildSteam(LibraryContext context, SocialConnectConfig config)
    {
        _steam = null;
        _auth = null;
        _openId = null;
        if (!config.Enabled || !config.Steam.Enabled) return;

        // Steam profile service
        _steam = new SteamProfileService(config.Steam, context.Logger, context.Events);
        context.Logger.Info("Steam profile service initialized");

        // Steam authentication service (optional, requires AppId)
        if (config.Steam.AuthEnabled)
        {
            _auth = new SteamAuthenticationService(config.Steam, context.Logger, context.Events);
            context.Logger.Info("Steam authentication service initialized");
        }

        // Steam OpenID sign-in for websites (optional, on by default)
        if (config.Steam.OpenIdEnabled)
        {
            _openId = new SteamOpenIdService(config.Steam, context.Logger, context.Events);
            context.Logger.Info("Steam OpenID sign-in service initialized");
        }
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart, rebuilding only the service whose settings
    /// changed: Discord settings replace <see cref="Discord"/>; Steam settings replace
    /// <see cref="Steam"/>, <see cref="Auth"/> and <see cref="OpenId"/> (the profile cache starts empty). Services can be
    /// switched on or off, and so can the whole library. A replaced service is disposed only after
    /// its request timeout, so requests already running on it finish.
    /// </summary>
    public Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (change.NewValue is not SocialConnectConfig current) return Task.CompletedTask;
        var previous = change.GetOld<SocialConnectConfig>();

        if (!ConfigDiff.ValueEquals(ActiveDiscord(previous), ActiveDiscord(current)))
        {
            var old = _discord;
            BuildDiscord(context, current);
            RetireLater(old, previous?.Discord.TimeoutSeconds ?? 0);
            if (_discord is null) context.Logger.Info("Discord webhook service turned off");
        }

        if (!ConfigDiff.ValueEquals(ActiveSteam(previous), ActiveSteam(current)))
        {
            var oldSteam = _steam;
            var oldAuth = _auth;
            var oldOpenId = _openId;
            BuildSteam(context, current);
            RetireLater(oldSteam, previous?.Steam.TimeoutSeconds ?? 0);
            RetireLater(oldAuth, previous?.Steam.TimeoutSeconds ?? 0);
            RetireLater(oldOpenId, previous?.Steam.TimeoutSeconds ?? 0);
            if (_steam is null) context.Logger.Info("Steam services turned off");
        }

        return Task.CompletedTask;
    }

    private static DiscordConfig? ActiveDiscord(SocialConnectConfig? config) =>
        config is { Enabled: true, Discord.Enabled: true } ? config.Discord : null;

    private static SteamConfig? ActiveSteam(SocialConnectConfig? config) =>
        config is { Enabled: true, Steam.Enabled: true } ? config.Steam : null;

    /// <summary>Disposes a replaced service once requests already running on it have timed out at the latest.</summary>
    private static void RetireLater(IDisposable? service, int timeoutSeconds)
    {
        if (service is null) return;
        var grace = TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 10) + 5);
        _ = Task.Delay(grace).ContinueWith(_ => service.Dispose(), TaskScheduler.Default);
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;

        var services = new List<string>();
        if (_discord is not null) services.Add("Discord");
        if (_steam is not null) services.Add("Steam");
        if (_auth is not null) services.Add("SteamAuth");
        if (_openId is not null) services.Add("SteamOpenId");

        context.Logger.Info(services.Count > 0
            ? $"{Manifest.Name} started — active services: {string.Join(", ", services)}"
            : $"{Manifest.Name} started (all services disabled)"
        );

        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        DisposeServices();

        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<SocialConnectConfig>();

        if (config is { Enabled: false })
            return Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is disabled"));

        if (config is null)
            return Task.FromResult(HealthStatus.Unhealthy("Not initialized"));

        var active = new List<string>();
        if (_discord is not null) active.Add("Discord");
        if (_steam is not null) active.Add("Steam");
        if (_auth is not null) active.Add("SteamAuth");
        if (_openId is not null) active.Add("SteamOpenId");

        // Services are only ever missing because their config switches them off,
        // so an app that uses neither is healthy, not broken.
        return Task.FromResult(HealthStatus.Healthy(active.Count > 0
            ? $"Active services: {string.Join(", ", active)}"
            : "No services enabled"));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Discord webhook service for sending messages and embeds.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Discord is not initialized or disabled.
    /// </exception>
    public DiscordWebhookService Discord =>
        _discord ?? throw new InvalidOperationException("Discord service not available — check config.Discord.Enabled");

    /// <summary>
    /// The Steam profile service for fetching player profiles, ban records, and owned games.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Steam is not initialized or disabled.
    /// </exception>
    public SteamProfileService Steam =>
        _steam ?? throw new InvalidOperationException("Steam service not available — check config.Steam.Enabled");

    /// <summary>
    /// The Steam authentication service for validating session tickets.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Steam authentication is not initialized or disabled.
    /// </exception>
    public SteamAuthenticationService Auth =>
        _auth ?? throw new InvalidOperationException("Steam auth service not available — check config.Steam.AuthEnabled");

    /// <summary>
    /// The Steam OpenID service for "Sign in through Steam" on websites.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Steam or Steam sign-in is not initialized or disabled.
    /// </exception>
    public SteamOpenIdService OpenId =>
        _openId ?? throw new InvalidOperationException("Steam sign-in service not available — check config.Steam.Enabled and config.Steam.OpenIdEnabled");

    /// <summary>Returns <c>true</c> when the Steam profile service is available.</summary>
    public bool HasSteam => _steam is not null;

    /// <summary>Returns <c>true</c> when the Steam authentication service is available.</summary>
    public bool HasSteamAuth => _auth is not null;

    /// <summary>Returns <c>true</c> when the Steam OpenID sign-in service is available.</summary>
    public bool HasSteamOpenId => _openId is not null;

    /// <summary>Returns <c>true</c> when the Discord webhook service is available.</summary>
    public bool HasDiscord => _discord is not null;

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeServices();
    }

    private void DisposeServices()
    {
        _discord?.Dispose();
        _steam?.Dispose();
        _auth?.Dispose();
        _openId?.Dispose();

        _discord = null;
        _steam = null;
        _auth = null;
        _openId = null;
    }
}
