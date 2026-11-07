using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Configuration;

namespace CL.Storage.Providers.Sftp;

/// <summary>
/// Decides whether an SSH server's host key is trusted, from pinned SHA-256 fingerprints and an
/// OpenSSH <c>known_hosts</c> file.
/// </summary>
internal sealed class SshHostTrust
{
    private readonly bool _autoAccept;
    private readonly HashSet<string> _fingerprints;
    private readonly IReadOnlyList<KnownHostEntry> _knownHosts;
    private readonly string _hostToken;

    public SshHostTrust(bool autoAccept, IEnumerable<string>? fingerprints, string? knownHostsPath, string host, int port)
    {
        _autoAccept = autoAccept;
        _fingerprints = (fingerprints ?? [])
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .Select(fingerprint => fingerprint!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _knownHosts = string.IsNullOrWhiteSpace(knownHostsPath) ? [] : Parse(File.ReadAllLines(knownHostsPath));
        _hostToken = HostToken(host, port);
    }

    /// <summary>Returns whether the key is trusted. A key listed as <c>@revoked</c> is never trusted.</summary>
    /// <param name="hostKey">Raw public-key blob as sent by the server.</param>
    /// <param name="sha256Fingerprint">SHA-256 fingerprint reported by the client library.</param>
    public bool IsTrusted(byte[] hostKey, string sha256Fingerprint)
    {
        if (_knownHosts.Any(entry => entry.Revoked && entry.Key.AsSpan().SequenceEqual(hostKey)))
            return false;
        if (_autoAccept)
            return true;
        if (CertificateFingerprint.TryNormalizeSha256(sha256Fingerprint, out var normalized) && _fingerprints.Contains(normalized))
            return true;
        return _knownHosts.Any(entry => !entry.Revoked && entry.Matches(_hostToken) && entry.Key.AsSpan().SequenceEqual(hostKey));
    }

    internal static IEnumerable<string> GetValidationErrors(bool autoAccept, List<string>? fingerprints, string? knownHostsPath, string prefix)
    {
        if (!autoAccept && (fingerprints is null || fingerprints.Count == 0) && string.IsNullOrWhiteSpace(knownHostsPath))
            yield return $"{prefix}At least one HostKeyFingerprint or a KnownHostsPath is required";
        foreach (var fingerprint in fingerprints ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"{prefix}Host-key fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
        if (!string.IsNullOrWhiteSpace(knownHostsPath) && !Path.IsPathFullyQualified(knownHostsPath))
            yield return $"{prefix}KnownHostsPath must be an absolute path";
    }

    /// <summary>OpenSSH writes the default port bare and any other port as <c>[host]:port</c>.</summary>
    internal static string HostToken(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    internal static IReadOnlyList<KnownHostEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<KnownHostEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var revoked = false;
            if (fields[0].StartsWith('@'))
            {
                // CA lines vouch for certificates, which are not plain host keys; skip them.
                if (!fields[0].Equals("@revoked", StringComparison.Ordinal))
                    continue;
                revoked = true;
                fields = fields[1..];
            }
            if (fields.Length < 3)
                continue;
            byte[] key;
            try { key = Convert.FromBase64String(fields[2]); }
            catch (FormatException) { continue; }
            entries.Add(new KnownHostEntry(fields[0].Split(','), fields[1], key, revoked));
        }
        return entries;
    }

    internal sealed record KnownHostEntry(string[] Patterns, string KeyType, byte[] Key, bool Revoked)
    {
        public bool Matches(string hostToken)
        {
            var matched = false;
            foreach (var pattern in Patterns)
            {
                var negated = pattern.StartsWith('!');
                var body = negated ? pattern[1..] : pattern;
                if (!PatternMatches(body, hostToken))
                    continue;
                if (negated)
                    return false;
                matched = true;
            }
            return matched;
        }

        private static bool PatternMatches(string pattern, string hostToken)
        {
            if (pattern.StartsWith("|1|", StringComparison.Ordinal))
                return HashedMatches(pattern, hostToken);
            if (pattern.IndexOfAny(['*', '?']) < 0)
                return string.Equals(pattern, hostToken, StringComparison.OrdinalIgnoreCase);
            try
            {
                return WildcardRegex(pattern).IsMatch(hostToken);
            }
            catch (RegexMatchTimeoutException)
            {
                return false; // a pattern that cannot be evaluated in time trusts nothing
            }
        }

        /// <summary>
        /// Wildcard patterns come from a <c>known_hosts</c> file that is read once per connection, so each is compiled
        /// once, with a match timeout: a file is operator-controlled, but a pathological pattern must not stall the
        /// host-key callback (which runs on the SSH session's thread).
        /// </summary>
        private static Regex WildcardRegex(string pattern) =>
            WildcardRegexes.GetOrAdd(pattern, static key =>
            {
                if (WildcardRegexes.Count >= 256) WildcardRegexes.Clear(); // bounded: a file has a handful of patterns
                var expression = "^" + Regex.Escape(key).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
                return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, WildcardMatchTimeout);
            });

        private static readonly TimeSpan WildcardMatchTimeout = TimeSpan.FromMilliseconds(250);
        private static readonly ConcurrentDictionary<string, Regex> WildcardRegexes = new(StringComparer.Ordinal);

        /// <summary>Hashed entries are <c>|1|base64(salt)|base64(HMAC-SHA1(salt, host))</c>.</summary>
        private static bool HashedMatches(string pattern, string hostToken)
        {
            var parts = pattern.Split('|');
            if (parts.Length != 4) return false;
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostToken));
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
