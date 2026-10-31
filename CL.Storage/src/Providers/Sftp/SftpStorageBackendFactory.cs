using System.Runtime.CompilerServices;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CL.Storage.Providers.Sftp;

internal sealed class SftpStorageBackendFactory : IStorageBackendFactory, IIsolatedStorageBackendFactory
{
    public Type ConfigurationType => typeof(SftpConnectionConfig);
    public StorageProvider Provider => StorageProvider.Sftp;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
        => Create(connectionId, configuration, maxBufferedDownloadBytes, observer, share: true);

    public IStorageBackend CreateIsolated(string connectionId, object configuration, long maxBufferedDownloadBytes)
        => Create(connectionId, configuration, maxBufferedDownloadBytes, observer: null, share: false);

    private static SftpStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer, bool share)
    {
        // Registrations with identical settings share one pool, so re-registering keeps warm sessions. The pool works
        // from a copy of the settings, so the caller's object changing later cannot make it differ from its key.
        var (value, key) = ProviderSettingsKey.Snapshot((SftpConnectionConfig)configuration);
        SharedPool<SftpClient> Build()
        {
            var recorder = new ServerIdentityRecorder();
            return new SharedPool<SftpClient>(SftpStorageBackend.CreatePool(() => CreateClient(value, recorder), value.Session), recorder);
        }
        // The observer is the registering library's own, so it identifies the library the pool lingers for.
        var shared = share ? SharedResources.Acquire(key, Build, pool => pool.Pool.DisposeAsync(), observer) : Build();
        var linger = TimeSpan.FromSeconds((value.Session ?? new StorageSessionConfig()).LingerSeconds);
        return new SftpStorageBackend(
            connectionId,
            share ? new SharedPoolHandle<SftpClient>(key, shared, linger) : new OwnedPool<SftpClient>(shared.Pool),
            value.Root,
            maxBufferedDownloadBytes,
            value.Retry,
            observer)
        {
            CommandClientFactory = value.AllowRawCommands ? () => CreateCommandClient(value) : null,
            Identity = shared.Identity,
            ListingScope = key
        };
    }

    internal static SftpClient CreateClient(SftpConnectionConfig value, ServerIdentityRecorder? identity = null) =>
        CreateClient(value, identity, connection =>
        {
            var client = new SftpClient(connection) { OperationTimeout = connection.Timeout };
            if (value.BufferSize is { } bufferSize)
                client.BufferSize = (uint)bufferSize;
            return client;
        });

    /// <summary>Creates an SSH shell client with exactly the same authentication, host trust, proxy, and tunnel.</summary>
    internal static SshClient CreateCommandClient(SftpConnectionConfig value) =>
        CreateClient(value, null, connection => new SshClient(connection));

    private static TClient CreateClient<TClient>(SftpConnectionConfig value, ServerIdentityRecorder? identity, Func<ConnectionInfo, TClient> create)
        where TClient : BaseClient
    {
        var timeout = TimeSpan.FromSeconds(value.TimeoutSeconds);
        var tunnel = value.JumpHost is null ? null : SftpJumpTunnel.Open(value, timeout);
        try
        {
            // Through a jump host SFTP connects to the local end of the tunnel and must not use the
            // proxy, which already carried the jump connection.
            var connection = new ConnectionInfo(
                tunnel is null ? value.Host : "127.0.0.1",
                tunnel is null ? value.Port : tunnel.LocalPort,
                value.Username,
                tunnel is null ? ProxyType(value.Proxy) : ProxyTypes.None,
                tunnel is null && value.Proxy?.Enabled == true ? value.Proxy.Host : null,
                tunnel is null && value.Proxy?.Enabled == true ? value.Proxy.Port : 0,
                tunnel is null ? value.Proxy?.Username : null,
                tunnel is null ? value.Proxy?.Password : null,
                [.. AuthenticationMethods(value)])
            {
                Timeout = timeout,
                Encoding = StorageEncodings.Get(value.Encoding)
            };
            ApplyAlgorithms(connection, value);
            var client = create(connection);
            if (value.Session is { KeepAliveSeconds: > 0 } session)
                client.KeepAliveInterval = TimeSpan.FromSeconds(session.KeepAliveSeconds);
            // The target key is checked against the real host name, even when reached through a tunnel.
            var trust = new SshHostTrust(value.AutoAcceptHostKey, value.HostKeyFingerprints, value.KnownHostsPath, value.Host, value.Port);
            client.HostKeyReceived += (_, eventArgs) =>
            {
                eventArgs.CanTrust = trust.IsTrusted(eventArgs.HostKey, eventArgs.FingerPrintSHA256);
                identity?.RecordHostKey(eventArgs.HostKeyName, eventArgs.FingerPrintSHA256, eventArgs.CanTrust);
                if (!eventArgs.CanTrust)
                    SftpHostKeyTracker.MarkRejected(client, $"SHA256:{eventArgs.FingerPrintSHA256}");
            };
            if (tunnel is not null)
                SftpJumpTunnel.Attach(client, tunnel);
            return client;
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    /// <summary>Builds the methods offered to the server, in the order OpenSSH tries them.</summary>
    internal static IEnumerable<AuthenticationMethod> AuthenticationMethods(SftpConnectionConfig value)
    {
        var mode = value.AuthenticationMode;
        if (mode is SftpAuthenticationMode.PrivateKey or SftpAuthenticationMode.Auto)
        {
            var keys = LoadKeys(value.PrivateKeyPath, value.PrivateKeyContent, value.AdditionalPrivateKeyPaths, value.PrivateKeyPassphrase);
            if (keys.Length > 0)
                yield return new PrivateKeyAuthenticationMethod(value.Username, keys);
        }
        if (value.Password is not null && mode is SftpAuthenticationMode.Password or SftpAuthenticationMode.Auto)
            yield return new PasswordAuthenticationMethod(value.Username, value.Password);
        if (value.Password is not null && mode is SftpAuthenticationMode.KeyboardInteractive or SftpAuthenticationMode.Auto)
            yield return KeyboardInteractive(value.Username, value.Password);
    }

    internal static IPrivateKeySource[] LoadKeys(string? path, string? content, IEnumerable<string>? additionalPaths, string? passphrase)
    {
        var keys = new List<IPrivateKeySource>();
        if (!string.IsNullOrWhiteSpace(path))
            keys.Add(string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase));
        if (!string.IsNullOrWhiteSpace(content))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            keys.Add(string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase));
        }
        foreach (var extra in additionalPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(extra))
                keys.Add(string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(extra) : new PrivateKeyFile(extra, passphrase));
        }
        return [.. keys];
    }

    /// <summary>Answers every password-style prompt with the configured password.</summary>
    private static KeyboardInteractiveAuthenticationMethod KeyboardInteractive(string username, string password)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (_, eventArgs) =>
        {
            foreach (var prompt in eventArgs.Prompts)
                prompt.Response = password;
        };
        return method;
    }

    private static void ApplyAlgorithms(ConnectionInfo connection, SftpConnectionConfig value)
    {
        SshAlgorithmNames.Restrict(connection.KeyExchangeAlgorithms, value.KeyExchangeAlgorithms);
        SshAlgorithmNames.Restrict(connection.Encryptions, value.Ciphers);
        SshAlgorithmNames.Restrict(connection.HmacAlgorithms, value.MacAlgorithms);
        SshAlgorithmNames.Restrict(connection.HostKeyAlgorithms, value.HostKeyAlgorithms);
    }

    internal static ProxyTypes ProxyType(StorageProxyConfig? proxy) => proxy?.Type switch
    {
        StorageProxyType.Http => ProxyTypes.Http,
        StorageProxyType.Socks4 => ProxyTypes.Socks4,
        StorageProxyType.Socks5 => ProxyTypes.Socks5,
        _ => ProxyTypes.None
    };
}

/// <summary>An SSH session to a jump host with a local port forwarded to the SFTP target.</summary>
internal sealed class SftpJumpTunnel : IDisposable
{
    private static readonly ConditionalWeakTable<BaseClient, SftpJumpTunnel> Tunnels = [];
    private readonly SshClient _jump;
    private readonly ForwardedPortLocal _forward;

    private SftpJumpTunnel(SshClient jump, ForwardedPortLocal forward)
    {
        _jump = jump;
        _forward = forward;
    }

    public int LocalPort => (int)_forward.BoundPort;

    public static SftpJumpTunnel Open(SftpConnectionConfig target, TimeSpan timeout)
    {
        var hop = target.JumpHost!;
        var methods = new List<AuthenticationMethod>();
        var keys = SftpStorageBackendFactory.LoadKeys(hop.PrivateKeyPath, hop.PrivateKeyContent, null, hop.PrivateKeyPassphrase);
        if (keys.Length > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(hop.Username, keys));
        if (hop.Password is not null)
            methods.Add(new PasswordAuthenticationMethod(hop.Username, hop.Password));
        var proxy = target.Proxy;
        var connection = new ConnectionInfo(
            hop.Host,
            hop.Port,
            hop.Username,
            SftpStorageBackendFactory.ProxyType(proxy),
            proxy?.Enabled == true ? proxy.Host : null,
            proxy?.Enabled == true ? proxy.Port : 0,
            proxy?.Username,
            proxy?.Password,
            [.. methods])
        {
            Timeout = timeout
        };
        var jump = new SshClient(connection);
        var trust = new SshHostTrust(hop.AutoAcceptHostKey, hop.HostKeyFingerprints, hop.KnownHostsPath, hop.Host, hop.Port);
        string? rejected = null;
        jump.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = trust.IsTrusted(eventArgs.HostKey, eventArgs.FingerPrintSHA256);
            if (!eventArgs.CanTrust)
                rejected = $"SHA256:{eventArgs.FingerPrintSHA256}";
        };
        try
        {
            jump.Connect();
            // Port 0 lets the OS pick a free loopback port; only this process can reach it.
            var forward = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
            jump.AddForwardedPort(forward);
            forward.Start();
            return new SftpJumpTunnel(jump, forward);
        }
        catch (SshConnectionException error) when (rejected is not null)
        {
            jump.Dispose();
            throw new SftpHostKeyRejectedException(rejected, error);
        }
        catch
        {
            jump.Dispose();
            throw;
        }
    }

    public static void Attach(BaseClient client, SftpJumpTunnel tunnel) => Tunnels.AddOrUpdate(client, tunnel);

    /// <summary>Closes the tunnel that carried <paramref name="client"/>, if any.</summary>
    public static void Close(BaseClient client)
    {
        if (Tunnels.TryGetValue(client, out var tunnel))
        {
            Tunnels.Remove(client);
            tunnel.Dispose();
        }
    }

    public void Dispose()
    {
        try { if (_forward.IsStarted) _forward.Stop(); }
        catch { /* The session may already be gone. */ }
        try { if (_jump.IsConnected) _jump.Disconnect(); }
        catch { /* Best effort. */ }
        _forward.Dispose();
        _jump.Dispose();
    }
}
