using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CL.Imaging.Metadata;

/// <summary>
/// A small, bounds-checked TIFF/EXIF IFD reader: IFD0, the Exif sub-IFD and the GPS sub-IFD. Malformed data yields
/// fewer fields, never an exception.
/// </summary>
internal sealed class ExifReader
{
    private const int MaxEntriesPerIfd = 512;
    private const ushort TagOrientation = 0x0112;

    private readonly byte[] _tiff;
    private readonly bool _bigEndian;
    private readonly Dictionary<ushort, Entry> _ifd0 = [];
    private readonly Dictionary<ushort, Entry> _exif = [];
    private readonly Dictionary<ushort, Entry> _gps = [];

    private readonly record struct Entry(ushort Type, uint Count, int ValueOffset);

    private ExifReader(byte[] tiff, bool bigEndian)
    {
        _tiff = tiff;
        _bigEndian = bigEndian;
    }

    /// <summary>Parses a TIFF block; null when the header is not TIFF.</summary>
    public static ExifReader? Parse(byte[]? tiff)
    {
        if (tiff is null || tiff.Length < 8) return null;
        bool bigEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I') bigEndian = false;
        else if (tiff[0] == 'M' && tiff[1] == 'M') bigEndian = true;
        else return null;
        var reader = new ExifReader(tiff, bigEndian);
        if (reader.U16(2) != 42) return null;
        var visited = new HashSet<int>();
        reader.ReadIfd((int)reader.U32(4), reader._ifd0, visited);
        if (reader._ifd0.TryGetValue(0x8769, out var exifPtr) && reader.Uint(exifPtr) is { } exifOffset)
            reader.ReadIfd((int)exifOffset, reader._exif, visited);
        if (reader._ifd0.TryGetValue(0x8825, out var gpsPtr) && reader.Uint(gpsPtr) is { } gpsOffset)
            reader.ReadIfd((int)gpsOffset, reader._gps, visited);
        return reader;
    }

    /// <summary>Fills the EXIF fields of <paramref name="metadata"/>.</summary>
    public ImageMetadata Apply(ImageMetadata metadata)
    {
        var orientation = Get(_ifd0, TagOrientation) is { } o && Uint(o) is >= 1 and <= 8 and var ov
            ? (ImageOrientation)ov
            : ImageOrientation.Normal;
        var dateText = Get(_exif, 0x9003) is { } dto ? Ascii(dto) : Get(_ifd0, 0x0132) is { } dt ? Ascii(dt) : null;
        return metadata with
        {
            HasExif = true,
            Orientation = orientation,
            CameraMake = Text(_ifd0, 0x010F),
            CameraModel = Text(_ifd0, 0x0110),
            Software = Text(_ifd0, 0x0131),
            Artist = Text(_ifd0, 0x013B),
            Copyright = Text(_ifd0, 0x8298),
            LensMake = Text(_exif, 0xA433),
            LensModel = Text(_exif, 0xA434),
            DateTaken = ParseDate(dateText),
            DateTakenOffset = ParseOffset(Text(_exif, 0x9011)),
            Iso = Get(_exif, 0x8827) is { } iso && Uint(iso) is { } isoValue and > 0 ? (int)isoValue : null,
            ExposureTime = Get(_exif, 0x829A) is { } et ? Rational(et, 0) : null,
            FNumber = Get(_exif, 0x829D) is { } fn ? Rational(fn, 0) : null,
            FocalLength = Get(_exif, 0x920A) is { } fl ? Rational(fl, 0) : null,
            FocalLength35mm = Get(_exif, 0xA405) is { } fl35 && Uint(fl35) is { } fl35Value and > 0 ? (int)fl35Value : null,
            FlashFired = Get(_exif, 0x9209) is { } flash && Uint(flash) is { } flashValue ? (flashValue & 1) == 1 : null,
            ExifWidth = Get(_exif, 0xA002) is { } w && Uint(w) is { } wv and > 0 ? (int)wv : null,
            ExifHeight = Get(_exif, 0xA003) is { } h && Uint(h) is { } hv and > 0 ? (int)hv : null,
            Gps = ReadGps(),
        };
    }

    /// <summary>
    /// Rewrites the orientation tag of IFD0 in place (used when re-encoding pixels that were already turned upright).
    /// Returns false when the block has no orientation tag.
    /// </summary>
    public static bool TrySetOrientation(byte[] tiff, ImageOrientation orientation)
    {
        var reader = Parse(tiff);
        if (reader is null || !reader._ifd0.TryGetValue(TagOrientation, out var entry) || entry.Type != 3) return false;
        if (reader._bigEndian) BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(entry.ValueOffset, 2), (ushort)orientation);
        else BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry.ValueOffset, 2), (ushort)orientation);
        return true;
    }

    private GpsCoordinates? ReadGps()
    {
        if (Get(_gps, 2) is not { } lat || Get(_gps, 4) is not { } lon) return null;
        var latitude = Dms(lat);
        var longitude = Dms(lon);
        if (latitude is null || longitude is null) return null;
        if (Text(_gps, 1) is "S") latitude = -latitude;
        if (Text(_gps, 3) is "W") longitude = -longitude;
        double? altitude = Get(_gps, 6) is { } alt ? Rational(alt, 0) : null;
        if (altitude is not null && Get(_gps, 5) is { } altRef && altRef.Count >= 1 && _tiff[altRef.ValueOffset] == 1)
            altitude = -altitude;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return null;
        return new GpsCoordinates(latitude.Value, longitude.Value, altitude);
    }

    private double? Dms(Entry entry)
    {
        if (entry.Count < 3) return null;
        var d = Rational(entry, 0);
        var m = Rational(entry, 1);
        var s = Rational(entry, 2);
        return d is null || m is null || s is null ? null : d + m / 60 + s / 3600;
    }

    private void ReadIfd(int offset, Dictionary<ushort, Entry> target, HashSet<int> visited)
    {
        if (offset < 8 || offset + 2 > _tiff.Length || !visited.Add(offset)) return;
        var count = U16(offset);
        if (count > MaxEntriesPerIfd) return;
        for (var i = 0; i < count; i++)
        {
            var at = offset + 2 + i * 12;
            if (at + 12 > _tiff.Length) return;
            var tag = U16(at);
            var type = U16(at + 2);
            var n = U32(at + 4);
            var size = TypeSize(type);
            if (size == 0 || n == 0 || n > int.MaxValue / 8) continue;
            var total = (long)size * n;
            var valueOffset = total <= 4 ? at + 8 : (long)U32(at + 8);
            if (valueOffset < 0 || valueOffset + total > _tiff.Length) continue;
            target.TryAdd(tag, new Entry(type, n, (int)valueOffset));
        }
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private static Entry? Get(Dictionary<ushort, Entry> ifd, ushort tag) => ifd.TryGetValue(tag, out var e) ? e : null;

    private uint? Uint(Entry entry) => entry.Type switch
    {
        1 or 7 => _tiff[entry.ValueOffset],
        3 => U16(entry.ValueOffset),
        4 => U32(entry.ValueOffset),
        _ => null,
    };

    private double? Rational(Entry entry, int index)
    {
        if (entry.Type is not (5 or 10) || index >= entry.Count) return null;
        var at = entry.ValueOffset + index * 8;
        double numerator, denominator;
        if (entry.Type == 5) { numerator = U32(at); denominator = U32(at + 4); }
        else { numerator = (int)U32(at); denominator = (int)U32(at + 4); }
        return denominator == 0 ? null : numerator / denominator;
    }

    private string? Ascii(Entry entry)
    {
        if (entry.Type is not (2 or 7)) return null;
        var span = _tiff.AsSpan(entry.ValueOffset, (int)entry.Count);
        var nul = span.IndexOf((byte)0);
        if (nul >= 0) span = span[..nul];
        var text = Encoding.UTF8.GetString(span).Trim();
        return text.Length == 0 ? null : text;
    }

    private string? Text(Dictionary<ushort, Entry> ifd, ushort tag) => Get(ifd, tag) is { } e ? Ascii(e) : null;

    private static DateTime? ParseDate(string? text) =>
        text is not null && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;

    private static TimeSpan? ParseOffset(string? text)
    {
        if (text is not { Length: 6 } || text[0] is not ('+' or '-')) return null;
        if (!TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var offset)) return null;
        return text[0] == '-' ? -offset : offset;
    }

    private ushort U16(int at) => _bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(_tiff.AsSpan(at, 2))
        : BinaryPrimitives.ReadUInt16LittleEndian(_tiff.AsSpan(at, 2));

    private uint U32(int at) => _bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(_tiff.AsSpan(at, 4))
        : BinaryPrimitives.ReadUInt32LittleEndian(_tiff.AsSpan(at, 4));
}
