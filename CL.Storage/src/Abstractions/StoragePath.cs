using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Normalizes provider-neutral paths without allowing parent traversal.</summary>
public static class StoragePath
{
    /// <summary>Converts slash variants and redundant segments into a root-relative portable path.</summary>
    /// <param name="path">Path to normalize. Empty and root-like paths normalize to an empty string.</param>
    /// <returns>
    /// The normalized path, or <c>storage.invalid_path</c> for a control character (U+0000 to U+001F and U+007F,
    /// which includes NUL, CR and LF) or parent traversal.
    /// </returns>
    /// <remarks>
    /// Control characters are rejected because several protocols carry paths on a line-based control channel
    /// (an FTP path with CR/LF would start a second command); no supported provider needs them in a name.
    /// </remarks>
    public static Result<string> Normalize(string path)
    {
        if (path is null)
            return Result<string>.Failure(StorageErrors.InvalidPath("A storage path cannot be null."));
        if (IndexOfControlCharacter(path) is var index and >= 0)
            return Result<string>.Failure(StorageErrors.InvalidPath(
                $"A storage path cannot contain control characters (U+{(int)path[index]:X4} at position {index})."));

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
                return Result<string>.Failure(StorageErrors.InvalidPath("Parent traversal is not allowed."));
            normalized.Add(part);
        }

        return Result<string>.Success(string.Join('/', normalized));
    }

    /// <summary>The index of the first C0 control character or DEL in <paramref name="value"/>, or -1.</summary>
    internal static int IndexOfControlCharacter(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is < (char)0x20 or (char)0x7F)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether a path segment is a Windows reserved device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>,
    /// <c>COM0</c>-<c>COM9</c>, <c>LPT0</c>-<c>LPT9</c>, their superscript-digit forms, <c>CONIN$</c>, <c>CONOUT$</c>),
    /// with or without an extension and trailing dots or spaces (<c>nul.txt</c>, <c>CON .log</c>, <c>aux.</c>).
    /// </summary>
    internal static bool IsWindowsReservedName(string segment)
    {
        var name = segment.TrimEnd('.', ' ');
        var dot = name.IndexOf('.');
        if (dot >= 0) name = name[..dot];
        name = name.TrimEnd(' ');
        if (name.Length is < 3 or > 7) return false;
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (name[3] is >= '0' and <= '9' || (int)name[3] is 0xB9 or 0xB2 or 0xB3);
    }
}
