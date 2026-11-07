using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// TLS listener of the embedded broker (<see cref="MqttBrokerConfig.Tls"/>). Without it, every password and
/// payload reaches the broker in clear, which is only acceptable on <c>127.0.0.1</c>.
/// </summary>
public sealed class MqttBrokerTlsConfig
{
    /// <summary>Open a TLS listener. Off by default.</summary>
    [ConfigField(Label = "Enabled", Group = "TLS", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>TCP port of the TLS listener (8883 is the standard MQTT-over-TLS port).</summary>
    [ConfigField(Label = "TLS Port", Min = 1, Max = 65535, Group = "TLS", Order = 1)]
    public int Port { get; set; } = 8883;

    /// <summary>Path of the server certificate with its private key (PKCS#12 / .pfx).</summary>
    [ConfigField(Label = "Certificate Path", Placeholder = "broker.pfx", Group = "TLS", Order = 2)]
    public string? CertificatePath { get; set; }

    /// <summary>Password of the PKCS#12 file, if it has one.</summary>
    [ConfigField(Label = "Certificate Password", InputType = ConfigInputType.Password, Secret = true, Group = "TLS", Order = 3)]
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Close the plain (non-TLS) listener, so every client must use TLS. Off by default so existing clients keep
    /// working while they are moved over.
    /// </summary>
    [ConfigField(Label = "Disable Plain Port", Group = "TLS", Order = 4)]
    public bool DisablePlainPort { get; set; }

    /// <summary>Checks the TLS settings; <paramref name="path"/> prefixes every error path.</summary>
    public IEnumerable<ConfigFieldError> Validate(string path)
    {
        if (!Enabled)
            yield break;
        if (Port is < 1 or > 65535)
            yield return new($"{path}.port", "TLS port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(CertificatePath))
        {
            yield return new($"{path}.certificatePath", "A certificate (.pfx) is required for TLS.");
            yield break;
        }
        if (!Path.IsPathFullyQualified(CertificatePath))
            yield return new($"{path}.certificatePath", "The certificate path must be fully qualified.");
        else if (!File.Exists(CertificatePath))
            yield return new($"{path}.certificatePath", $"Certificate file '{CertificatePath}' does not exist.");
        else if (TryLoadCertificate() is { } error)
            yield return new($"{path}.certificatePath", error);
    }

    /// <summary>Loads the certificate with its private key.</summary>
    /// <exception cref="CryptographicException">The file cannot be read or has no private key.</exception>
    internal X509Certificate2 LoadCertificate()
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath!, CertificatePassword);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException("The certificate has no private key.");
        }
        return certificate;
    }

    private string? TryLoadCertificate()
    {
        try
        {
            using var certificate = LoadCertificate();
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return $"Certificate '{CertificatePath}' cannot be loaded: {ex.Message}";
        }
    }

    internal bool ListenerEquals(MqttBrokerTlsConfig other) =>
        Enabled == other.Enabled
        && Port == other.Port
        && DisablePlainPort == other.DisablePlainPort
        && string.Equals(CertificatePath, other.CertificatePath, StringComparison.Ordinal)
        && string.Equals(CertificatePassword, other.CertificatePassword, StringComparison.Ordinal);
}
