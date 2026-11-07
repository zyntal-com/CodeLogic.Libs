using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using System.Formats.Tar;
using System.IO.Compression;

namespace CL.NetUtils.Services;

public partial class GeoIpService
{

    /// <summary>Disposes a reader that was swapped out, once lookups that snapshotted it have had time to finish.</summary>
    private void RetireLater(DatabaseReader? previous)
    {
        if (previous is null) return;
        if (RetireGracePeriod <= TimeSpan.Zero)
        {
            previous.Dispose();
            return;
        }
        var grace = RetireGracePeriod;
        _ = Task.Run(async () =>
        {
            await Task.Delay(grace).ConfigureAwait(false);
            try { previous.Dispose(); }
            catch (Exception ex) { _logger?.Debug($"Retired GeoIP reader could not be disposed: {ex.Message}"); }
        });
    }

    /// <inheritdoc/>
    /// <remarks>Closes the open reader at once; a lookup still running on it reports an error.</remarks>
    public void Dispose()
    {
        DatabaseReader? reader;
        lock (_lock)
        {
            reader = _databaseReader;
            _databaseReader = null;
        }
        reader?.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a reader that loads the whole database into memory, so the file is never held open and can be replaced
    /// while lookups continue (a memory-mapped reader locks the file on Windows).
    /// </summary>
    private static DatabaseReader OpenReader(string path) => new(path, FileAccessMode.Memory);

    /// <summary>
    /// Installs a validated staged database without a gap and without risking the working one: the replacement reader
    /// is opened first, then the file is renamed over <see cref="DatabasePath"/>, then the readers are swapped. If any
    /// step fails, the previous file and reader stay in use.
    /// </summary>
    private void InstallDatabase(string stagedPath)
    {
        var replacement = OpenReader(stagedPath);
        GeoIpDatabaseKind kind;
        try
        {
            kind = GeoIpDatabaseKinds.Detect(replacement.Metadata.DatabaseType)
                ?? throw new InvalidDataException($"The GeoIP database type '{replacement.Metadata.DatabaseType}' is not supported");
            File.Move(stagedPath, _databasePath, overwrite: true);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        DatabaseReader? previous;
        lock (_lock)
        {
            previous = _databaseReader;
            // Only an initialized service gets the new reader; otherwise InitializeAsync opens the installed file.
            _databaseReader = previous is null ? null : replacement;
            if (previous is not null) _kind = kind;
        }
        if (previous is null) replacement.Dispose();
        else RetireLater(previous);
    }

    /// <summary>
    /// Proves that <paramref name="path"/> is a usable MaxMind database before it may replace the working one: the
    /// metadata must parse, the edition must be one <see cref="LookupIpAsync"/> supports (City, Country, Enterprise, ASN,
    /// ISP) and, when <paramref name="expectedKind"/> is given (from <see cref="GeoIpConfig.DatabaseType"/>), the same
    /// kind, and a lookup must walk the search tree. A marker alone is not enough; a corrupt file can contain it.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a usable database of the expected kind.</exception>
    internal static void ValidateDatabase(string path, GeoIpDatabaseKind? expectedKind = null)
    {
        try
        {
            using var reader = OpenReader(path);
            var type = reader.Metadata.DatabaseType ?? "";
            var kind = GeoIpDatabaseKinds.Detect(type)
                ?? throw new InvalidDataException($"The GeoIP database type '{type}' is not supported (City, Country, Enterprise, ASN or ISP editions are)");
            if (expectedKind is { } expected && kind != expected)
                throw new InvalidDataException($"The GeoIP database type '{type}' is a {kind} database, but GeoIp.DatabaseType asks for {expected}");
            foreach (var probe in reader.Metadata.IPVersion == 6 ? new[] { "8.8.8.8", "2001:4860:4860::8888" } : ["8.8.8.8"])
                Probe(reader, kind, IPAddress.Parse(probe));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"The extracted file is not a usable MaxMind database: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts the <c>.mmdb</c> entry of a <c>tar.gz</c> or <c>zip</c> archive to <paramref name="outputPath"/>.
    /// Prefers an entry named <paramref name="preferredName"/>, else the first <c>.mmdb</c>; only the entry's
    /// content is used, never its path, and no other entry is written anywhere. The decompressed bytes are capped at
    /// <paramref name="maxBytes"/>, so an archive that inflates past the limit stops there.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The archive format is not supported, it holds no <c>.mmdb</c>, or the file is not a MaxMind database.
    /// </exception>
    /// <exception cref="DownloadTooLargeException">The decompressed content exceeds <paramref name="maxBytes"/>.</exception>
    internal static void ExtractDatabase(
        string archivePath,
        string outputPath,
        string preferredName,
        GeoIpDatabaseKind? expectedKind = null,
        long maxBytes = GeoIpConfig.DefaultMaxDownloadBytes,
        CancellationToken cancellationToken = default)
    {
        static bool IsMmdb(string name) => name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase);
        bool IsPreferred(string name) =>
            string.Equals(name.Replace('\\', '/').Split('/')[^1], preferredName, StringComparison.OrdinalIgnoreCase);

        void Write(Stream source, string path)
        {
            using var target = File.Create(path);
            var buffer = new byte[81_920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer);
                if (count == 0) break;
                target.Write(buffer.AsSpan(0, count));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var found = false;
        using (var file = File.OpenRead(archivePath))
        {
            Span<byte> magic = stackalloc byte[4];
            var read = file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            file.Position = 0;

            if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
            {
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var bounded = new BoundedReadStream(gzip, maxBytes, "unpacked GeoIP archive", cancellationToken);
                using var tar = new TarReader(bounded);
                while (tar.GetNextEntry() is { } entry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        || entry.DataStream is null || !IsMmdb(entry.Name))
                        continue;
                    var preferred = IsPreferred(entry.Name);
                    if (found && !preferred)
                        continue;
                    Write(entry.DataStream, outputPath);
                    found = true;
                    if (preferred)
                        break;
                }
            }
            else if (read >= 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K')
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                var candidates = zip.Entries.Where(e => !e.FullName.EndsWith('/') && IsMmdb(e.FullName)).ToList();
                var entry = candidates.FirstOrDefault(e => IsPreferred(e.FullName)) ?? candidates.FirstOrDefault();
                if (entry is not null)
                {
                    using var source = new BoundedReadStream(entry.Open(), maxBytes, "unpacked GeoIP database");
                    Write(source, outputPath);
                    found = true;
                }
            }
            else
            {
                throw new InvalidDataException("The GeoIP download is not a tar.gz or zip archive");
            }
        }

        if (!found)
            throw new InvalidDataException("The GeoIP archive contains no .mmdb file");

        cancellationToken.ThrowIfCancellationRequested();
        ValidateDatabase(outputPath, expectedKind);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>A lookup of the edition's kind; walks the search tree (and throws on a corrupt one).</summary>
    private static void Probe(DatabaseReader reader, GeoIpDatabaseKind kind, IPAddress ip)
    {
        _ = kind switch
        {
            GeoIpDatabaseKind.City => reader.TryCity(ip, out _),
            GeoIpDatabaseKind.Enterprise => reader.TryEnterprise(ip, out _),
            GeoIpDatabaseKind.Country => reader.TryCountry(ip, out _),
            GeoIpDatabaseKind.Asn => reader.TryAsn(ip, out _),
            GeoIpDatabaseKind.Isp => reader.TryIsp(ip, out _),
            _ => throw new InvalidOperationException($"Unsupported GeoIP database kind {kind}")
        };
    }
}
