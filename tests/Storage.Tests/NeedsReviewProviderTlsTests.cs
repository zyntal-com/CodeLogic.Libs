using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Providers;
using CL.Storage.Providers.Ftp;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Needs-review round 3, providers: TLS failure classification, client certificates, and shared pools.</summary>
public sealed class NeedsReviewProviderTlsTests
{
    private static Error Map(Exception exception) =>
        ProviderErrorMapper.FromTransport(exception, "Test", "WebDAV") ?? throw new InvalidOperationException("not classified");

    // needs-review A27 / E (ordinary TLS drop, concurrent requests): a certificate request elsewhere does not turn a drop into a credential failure
    [Fact]
    public void An_ordinary_drop_during_an_attempt_with_a_certificate_request_stays_a_transient_lost_connection()
    {
        var recorder = new ServerIdentityRecorder();
        var started = DateTimeOffset.UtcNow;
        // Another connection of the same backend was asked for a certificate while this attempt ran.
        recorder.RecordClientCertificateRequest();
        var dropped = Map(new IOException("Unable to read data", new SocketException((int)SocketError.ConnectionReset)));

        var enriched = TlsDiagnosis.Enrich(dropped, recorder, started);

        Assert.Equal(StorageErrors.ConnectionLostCode, enriched.Code);
        Assert.True(StorageErrorInfo.IsTransient(enriched));
    }

    // needs-review A27 / E (optional client certificates): a server that asks but accepts leaves no refusal behind
    [Fact]
    public async Task A_connection_whose_certificate_was_accepted_records_no_refusal_when_it_drops_later()
    {
        var recorder = new ServerIdentityRecorder();
        var started = DateTimeOffset.UtcNow;
        await using var watch = new TlsConnectionWatch(new ScriptedStream(reads: [300, -1]), recorder);

        watch.CertificateRequested();
        await watch.WriteAsync(new byte[10]);
        Assert.Equal(300, await watch.ReadAsync(new byte[1024]));
        await Assert.ThrowsAsync<IOException>(async () => _ = await watch.ReadAsync(new byte[1024]));

        Assert.Null(recorder.ClientCertificateRefusedAt);
        Assert.Equal(StorageErrors.ConnectionLostCode, TlsDiagnosis.Enrich(StorageErrors.ConnectionLost("lost"), recorder, started).Code);
    }

    // needs-review A27: a connection asked for a certificate that fails right after our reply is a refusal, on that connection
    [Fact]
    public async Task A_connection_that_fails_after_the_certificate_reply_records_a_refusal()
    {
        var recorder = new ServerIdentityRecorder();
        var started = DateTimeOffset.UtcNow;
        var watch = new TlsConnectionWatch(new ScriptedStream(reads: [24]), recorder);

        watch.CertificateRequested();
        await watch.WriteAsync(new byte[10]);
        _ = await watch.ReadAsync(new byte[1024]); // the alert
        await watch.DisposeAsync();

        Assert.NotNull(recorder.ClientCertificateRefusedAt);
        var enriched = TlsDiagnosis.Enrich(StorageErrors.ConnectionLost("lost"), recorder, started);
        Assert.Equal(StorageErrors.TlsFailureCode, enriched.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(enriched, StorageErrorInfo.TlsReasonKey, out var reason));
        Assert.Equal(TlsDiagnosis.ClientCertificateRejected, reason);
    }

    // needs-review A27: a connection never asked for a certificate records nothing, however it ends
    [Fact]
    public async Task A_connection_never_asked_for_a_certificate_records_nothing()
    {
        var recorder = new ServerIdentityRecorder();
        var watch = new TlsConnectionWatch(new ScriptedStream(reads: [-1]), recorder);
        // The selection callback also runs at the start of every handshake, with nothing from the server.
        TlsConnectionWatch.OnCertificateSelection(new object(), null, []);

        await watch.WriteAsync(new byte[10]);
        await Assert.ThrowsAsync<IOException>(async () => _ = await watch.ReadAsync(new byte[1024]));
        await watch.DisposeAsync();

        Assert.Null(recorder.ClientCertificateRefusedAt);
    }

    // needs-review A27 / E (Linux EOF): OpenSSL's end of stream after the handshake is a lost connection
    [Fact]
    public void An_OpenSSL_error_on_an_established_connection_is_a_lost_connection_but_during_the_handshake_a_TLS_failure()
    {
        var eof = new CryptographicException("error:0A000126:SSL routines::unexpected eof while reading");
        var badMac = new CryptographicException("error:0A000119:SSL routines::decryption failed or bad record mac");
        var refused = new CryptographicException("error:0A00045C:SSL routines::tlsv13 alert certificate required");

        Assert.Equal(StorageErrors.ConnectionLostCode, Map(new IOException("read failed", eof)).Code);
        Assert.Equal(StorageErrors.ConnectionLostCode, Map(new IOException("read failed", badMac)).Code);
        Assert.Equal(StorageErrors.TlsFailureCode, Map(new AuthenticationException("handshake", eof)).Code);
        var alert = Map(new IOException("read failed", refused));
        Assert.Equal(StorageErrors.TlsFailureCode, alert.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(alert, StorageErrorInfo.TlsReasonKey, out var reason));
        Assert.Equal(TlsDiagnosis.ClientCertificateRejected, reason);
    }

    // needs-review A27: SChannel record errors mid-stream are lost connections
    [Theory]
    [InlineData(unchecked((int)0x80090330))] // SEC_E_DECRYPT_FAILURE
    [InlineData(unchecked((int)0x8009030F))] // SEC_E_MESSAGE_ALTERED
    public void An_SChannel_record_error_on_an_established_connection_is_a_lost_connection(int status)
    {
        var error = Map(new IOException("The decryption operation failed, see inner exception.", new Win32Exception(status)));

        Assert.Equal(StorageErrors.ConnectionLostCode, error.Code);
        Assert.True(StorageErrorInfo.IsTransient(error));
        Assert.True(StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var hint));
        Assert.Equal(TlsDiagnosis.ConnectionInterrupted, hint);
    }

    // needs-review A27: an SSPI status from Negotiate/NTLM authentication is not a client-certificate problem
    [Fact]
    public void An_SSPI_Negotiate_error_is_not_a_refused_client_certificate()
    {
        var negotiate = new HttpRequestException("auth", new Win32Exception(unchecked((int)0x8009030E))); // SEC_E_NO_CREDENTIALS

        var error = Map(negotiate);

        Assert.NotEqual(StorageErrors.TlsFailureCode, error.Code);
        Assert.False(TlsDiagnosis.IsPlatformTlsFailure(negotiate));
    }

    // needs-review A28: macOS cannot use ephemeral keys, so it gets the default (temporary keychain) import
    [Fact]
    public void Key_storage_is_ephemeral_only_where_the_platform_supports_it()
    {
        Assert.Equal(X509KeyStorageFlags.EphemeralKeySet, ClientCertificates.KeyStorageFlags(windows: false, apple: false));
        Assert.Equal(X509KeyStorageFlags.DefaultKeySet, ClientCertificates.KeyStorageFlags(windows: false, apple: true));
        Assert.Equal(X509KeyStorageFlags.DefaultKeySet, ClientCertificates.KeyStorageFlags(windows: true, apple: false));
    }

    // needs-review B65 / E (PFX without key): a certificate without its private key is refused when loaded
    [Fact]
    public void A_PFX_without_a_private_key_is_refused()
    {
        using var key = RSA.Create(2048);
        using var withKey = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var noKey = publicOnly.Export(X509ContentType.Pkcs12, "pw");
        var full = withKey.Export(X509ContentType.Pkcs12, "pw");

        var refused = Assert.Throws<CryptographicException>(() => ClientCertificates.Load(null, noKey, "pw"));
        using var loaded = ClientCertificates.Load(null, full, "pw");

        Assert.Contains("private key", refused.Message, StringComparison.Ordinal);
        Assert.True(loaded!.HasPrivateKey);
    }

    // needs-review B66 / E (pool linger race): a natural linger timeout leaves nothing a later Acquire trips over
    [Fact]
    public async Task A_resource_acquired_again_after_its_linger_ran_out_is_fresh_and_released_cleanly()
    {
        var key = $"linger-{Guid.NewGuid():N}";
        var disposed = 0;
        var first = SharedResources.Acquire(key, () => new object(), _ => { Interlocked.Increment(ref disposed); return ValueTask.CompletedTask; });
        // Released with a linger that runs out on its own, while another user arrives right then.
        var release = SharedResources.ReleaseAsync(key, TimeSpan.FromMilliseconds(30)).AsTask();
        await Task.Delay(30);
        var second = SharedResources.Acquire(key, () => new object(), _ => { Interlocked.Increment(ref disposed); return ValueTask.CompletedTask; });
        await release;
        await SharedResources.ReleaseAsync(key, TimeSpan.Zero);

        Assert.NotNull(second);
        Assert.True(ReferenceEquals(first, second) ? disposed == 1 : disposed == 2, $"disposed {disposed} time(s)");
        Assert.False(SharedResources.Holds(key));
    }

    // needs-review B66: many racing acquires against expiring lingers never throw and never leak a holder
    [Fact]
    public async Task Racing_acquires_against_expiring_lingers_never_throw_or_leak()
    {
        var key = $"race-{Guid.NewGuid():N}";
        for (var round = 0; round < 200; round++)
        {
            SharedResources.Acquire(key, () => new object(), _ => ValueTask.CompletedTask);
            var release = SharedResources.ReleaseAsync(key, TimeSpan.FromMilliseconds(1)).AsTask();
            await Task.Delay(round % 3);
            SharedResources.Acquire(key, () => new object(), _ => ValueTask.CompletedTask);
            await release;
            await SharedResources.ReleaseAsync(key, TimeSpan.Zero);
        }
        Assert.False(SharedResources.Holds(key));
    }

    // needs-review B66: the shared pool works from a copy of the settings, so a later change cannot make it differ from its key
    [Fact]
    public void A_shared_pool_takes_a_copy_of_the_settings()
    {
        var settings = new FtpConnectionConfig { Host = "ftp.example", Username = "u", Password = "p" };
        var (copy, key) = ProviderSettingsKey.Snapshot(settings);

        settings.Host = "changed.example";

        Assert.Equal("ftp.example", copy.Host);
        Assert.Equal(ProviderSettingsKey.For(copy), key);
        Assert.NotEqual(ProviderSettingsKey.For(settings), key);
    }

    // needs-review E (FTP mutual TLS through a shared pool): one certificate per shared pool, disposed with it, loaded outside the lock
    [Fact]
    public async Task FTP_registrations_with_the_same_client_certificate_share_one_pool_and_one_certificate()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var config = new FtpConnectionConfig
        {
            Host = $"mtls-{Guid.NewGuid():N}.example",
            Username = "u",
            Password = "p",
            EncryptionMode = StorageFtpEncryptionMode.Explicit,
            ClientCertificateContent = certificate.Export(X509ContentType.Pkcs12, "pw"),
            ClientCertificatePassword = "pw"
        };
        var factory = new FtpStorageBackendFactory();
        

        var first = (FtpStorageBackend)factory.Create("a", config, 1 << 20);
        var second = (FtpStorageBackend)factory.Create("b", config, 1 << 20);

        var key2 = ProviderSettingsKey.For(config);
        Assert.True(SharedResources.Holds(key2));
        Assert.Same(first.Pool, second.Pool);
        await first.DisposeAsync();
        Assert.True(SharedResources.Holds(key2));
        await second.DisposeAsync();
        Assert.False(SharedResources.Holds(key2));
    }

    // needs-review C (providers): an upload from a non-seekable stream is attempted once, and its failure is still explained
    [Fact]
    public async Task A_non_seekable_upload_failure_is_enriched_like_any_other()
    {
        var policy = new ProviderRetryPolicy(new StorageRetryConfig { RetryCount = 3 }, "c", CL.Storage.Models.StorageProvider.WebDav)
        {
            Enrich = (error, _) => StorageErrors.TlsFailure(error.Message, "tlsReason=server_certificate_rejected")
        };
        await using var source = new NonSeekable();

        var result = await policy.ExecuteUploadAsync("Upload", source, _ => Task.FromResult(Result<int>.Failure(StorageErrors.ConnectionLost("lost"))), CancellationToken.None);

        Assert.Equal(StorageErrors.TlsFailureCode, result.Error?.Code);
    }

    // needs-review B66: a new session is reported to the registration that opened it, not to every user of a shared pool
    [Fact]
    public async Task A_new_session_is_reported_only_to_the_caller_that_opened_it()
    {
        await using var pool = new ProviderClientPool<object>(
            () => new object(),
            (_, _) => Task.CompletedTask,
            _ => true,
            _ => ValueTask.CompletedTask,
            new ProviderPoolOptions(MaxSessions: 2, MaxIdle: 2));
        int first = 0, second = 0;

        var a = await pool.RentAsync(CancellationToken.None, () => first++);
        var b = await pool.RentAsync(CancellationToken.None, () => second++);
        await pool.ReturnAsync(a);
        var reused = await pool.RentAsync(CancellationToken.None, () => first++);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        await pool.ReturnAsync(b);
        await pool.ReturnAsync(reused);
    }

    private sealed class NonSeekable : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A transport whose reads return the scripted byte counts; -1 throws as a dropped connection does.</summary>
    private sealed class ScriptedStream(int[] reads) : Stream
    {
        private int _next;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var scripted = _next < reads.Length ? reads[_next++] : 0;
            if (scripted < 0) throw new IOException("dropped", new SocketException((int)SocketError.ConnectionReset));
            return Math.Min(scripted, count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
