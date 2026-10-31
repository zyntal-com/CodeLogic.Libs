using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CL.Storage.Providers.Sftp;

/// <summary>
/// Remembers host keys rejected during connect. SSH.NET reports an untrusted host key as a generic
/// key-exchange failure, so the rejection is recorded here to classify it precisely.
/// </summary>
internal static class SftpHostKeyTracker
{
    private static readonly ConditionalWeakTable<BaseClient, string> Rejected = [];

    public static void MarkRejected(BaseClient client, string fingerprint) =>
        Rejected.AddOrUpdate(client, fingerprint);

    public static async Task ConnectAsync(BaseClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException error) when (Rejected.TryGetValue(client, out var fingerprint))
        {
            throw new SftpHostKeyRejectedException(fingerprint, error);
        }
    }
}

/// <summary>Raised when the server's host key is not trusted by the configured fingerprints.</summary>
internal sealed class SftpHostKeyRejectedException(string fingerprint, Exception inner)
    : SshConnectionException("The SSH host key was not trusted.", inner)
{
    public string Fingerprint { get; } = fingerprint;
}
