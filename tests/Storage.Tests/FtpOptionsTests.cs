using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Providers;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers FTP connection options and TLS pinning shared with WebDAV.</summary>
public sealed class FtpOptionsTests
{
    [Fact]
    public void Certificate_pin_accepts_a_self_signed_certificate()
    {
        using var certificate = SelfSigned();
        var pins = new TlsPins([Convert.ToHexString(SHA256.HashData(certificate.RawData))], null);

        Assert.True(pins.Accepts(certificate, SslPolicyErrors.RemoteCertificateChainErrors, requireValidChain: false));
        Assert.False(pins.Accepts(certificate, SslPolicyErrors.RemoteCertificateChainErrors, requireValidChain: true));
    }

    [Fact]
    public void Public_key_pin_survives_a_reissued_certificate_with_the_same_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = SelfSigned(key, "CN=first");
        using var renewed = SelfSigned(key, "CN=renewed");
        var pins = new TlsPins(null, [TlsPins.PublicKeyPin(first)]);

        Assert.True(pins.Matches(renewed));
        using var otherKey = SelfSigned();
        Assert.False(pins.Matches(otherKey));
    }

    [Fact]
    public void Without_pins_only_a_clean_chain_is_accepted()
    {
        using var certificate = SelfSigned();
        var pins = new TlsPins(null, null);

        Assert.True(pins.Accepts(certificate, SslPolicyErrors.None, requireValidChain: false));
        Assert.False(pins.Accepts(certificate, SslPolicyErrors.RemoteCertificateNameMismatch, requireValidChain: false));
    }

    [Theory]
    [InlineData(40000, null, "ActivePortMin and ActivePortMax must be set together")]
    [InlineData(50100, 50000, "The active port range")]
    [InlineData(80, 90, "The active port range")]
    public void Active_port_range_is_validated(int min, int? max, string expected)
    {
        var config = Valid(c => { c.ActivePortMin = min; c.ActivePortMax = max; });

        Assert.Contains(config.Validate().Errors, error => error.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
#pragma warning disable SYSLIB0039
    [InlineData(SslProtocols.Tls11)]
    [InlineData(SslProtocols.Tls)]
#pragma warning restore SYSLIB0039
    public void Obsolete_tls_versions_are_rejected(SslProtocols protocol)
    {
        Assert.False(Valid(c => c.TlsProtocols = [protocol]).Validate().IsValid);
    }

    [Theory]
    [InlineData("ActiveExternalIp", "not-an-ip")]
    [InlineData("Encoding", "klingon")]
    [InlineData("ServerTimeZone", "Mars/Olympus_Mons")]
    [InlineData("LoginCommands", "SITE UMASK 022\r\nDELE x")]
    public void Invalid_options_are_rejected(string option, string value)
    {
        var config = Valid(c =>
        {
            switch (option)
            {
                case "ActiveExternalIp": c.ActiveExternalIp = value; break;
                case "Encoding": c.Encoding = value; break;
                case "ServerTimeZone": c.ServerTimeZone = value; break;
                default: c.LoginCommands = [value]; break;
            }
        });

        Assert.False(config.Validate().IsValid);
    }

    [Fact]
    public void Pins_on_plain_ftp_are_rejected()
    {
        var config = Valid(c => { c.EncryptionMode = StorageFtpEncryptionMode.None; c.TrustedPublicKeySha256 = [new string('A', 64)]; });

        Assert.Contains(config.Validate().Errors, error => error.Contains("require an encrypted", StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_full_configuration_passes()
    {
        var config = Valid(c =>
        {
            c.TlsProtocols = [SslProtocols.Tls12, SslProtocols.Tls13];
            c.ActivePortMin = 50000;
            c.ActivePortMax = 50100;
            c.ActiveExternalIp = "203.0.113.7";
            c.Encoding = "windows-1252";
            c.TransferType = StorageFtpTransferType.Ascii;
            c.ListingParser = StorageFtpListingParser.Unix;
            c.ServerTimeZone = "UTC";
            c.LoginCommands = ["SITE UMASK 022"];
        });

        Assert.True(config.Validate().IsValid, string.Join("; ", config.Validate().Errors));
    }

    private static FtpConnectionConfig Valid(Action<FtpConnectionConfig> configure)
    {
        var config = new FtpConnectionConfig { Host = "ftp.example", Username = "u", Password = "p" };
        configure(config);
        return config;
    }

    private static X509Certificate2 SelfSigned(ECDsa? key = null, string subject = "CN=test")
    {
        var owned = key is null;
        key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }
        finally
        {
            if (owned) key.Dispose();
        }
    }
}

public sealed class StorageEncodingTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("windows-1252")]
    [InlineData("iso-8859-1")]
    [InlineData("ibm437")]
    [InlineData("shift_jis")]
    public void Legacy_code_pages_used_by_old_servers_are_available(string name)
    {
        Assert.True(CL.Storage.Providers.StorageEncodings.IsKnown(name));
        Assert.True(new CL.Storage.Configuration.SftpConnectionConfig { Host = "h", Username = "u", Password = "p", AutoAcceptHostKey = true, Encoding = name }.Validate().IsValid);
    }
}
