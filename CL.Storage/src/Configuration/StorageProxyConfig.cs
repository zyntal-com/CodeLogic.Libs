using System.Net;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies the kind of proxy a connection tunnels through.</summary>
public enum StorageProxyType
{
    /// <summary>Connects directly.</summary>
    None = 0,
    /// <summary>Tunnels through an HTTP proxy using <c>CONNECT</c>.</summary>
    Http = 1,
    /// <summary>Tunnels through a SOCKS4 proxy.</summary>
    Socks4 = 2,
    /// <summary>Tunnels through a SOCKS5 proxy.</summary>
    Socks5 = 3
}

/// <summary>Routes a connection through an HTTP or SOCKS proxy.</summary>
/// <remarks>
/// Supported by every provider. FTP data connections are tunnelled too, which requires passive mode.
/// SOCKS4 has no authentication, so <see cref="Username"/> and <see cref="Password"/> apply to HTTP and SOCKS5.
/// </remarks>
public sealed class StorageProxyConfig
{
    /// <summary>Gets or sets the proxy type; <see cref="StorageProxyType.None"/> disables the proxy.</summary>
    public StorageProxyType Type { get; set; } = StorageProxyType.None;

    /// <summary>Gets or sets the proxy host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the optional proxy user name.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the optional proxy password.</summary>
    [ConfigField(Label = "Proxy password", Secret = true, InputType = ConfigInputType.Password)]
    public string? Password { get; set; }

    internal bool Enabled => Type != StorageProxyType.None;

    internal IEnumerable<string> GetValidationErrors(string prefix)
    {
        if (!Enabled)
            yield break;
        if (string.IsNullOrWhiteSpace(Host))
            yield return $"{prefix}Host is required when a proxy is enabled";
        if (Port is < 1 or > 65535)
            yield return $"{prefix}Port must be between 1 and 65535";
        if (Type == StorageProxyType.Socks4 && !string.IsNullOrEmpty(Password))
            yield return $"{prefix}SOCKS4 proxies do not support passwords";
    }

    /// <summary>Builds a proxy for <see cref="System.Net.Http.HttpClient"/>-based providers.</summary>
    internal IWebProxy? ToWebProxy()
    {
        if (!Enabled)
            return null;
        var scheme = Type switch
        {
            StorageProxyType.Socks4 => "socks4",
            StorageProxyType.Socks5 => "socks5",
            _ => "http"
        };
        var proxy = new WebProxy(new Uri($"{scheme}://{Host}:{Port}")) { BypassProxyOnLocal = false };
        if (!string.IsNullOrEmpty(Username))
            proxy.Credentials = new NetworkCredential(Username, Password);
        return proxy;
    }
}
