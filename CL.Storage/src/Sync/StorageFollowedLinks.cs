using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>
/// The links one side's listing followed (<see cref="StorageLinkHandling.Follow"/>): each link's path, whether it
/// leads to a folder, and for every item reached through a link the path its content is really read from.
/// </summary>
internal sealed class StorageFollowedLinks
{
    // Links compare ignoring case, which only ever leaves more alone; read paths are exact spellings.
    private readonly Dictionary<string, bool> _links = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, string> _readPaths = [with(StringComparer.Ordinal)];

    /// <summary>Records a followed link.</summary>
    public void Link(string relative, bool toDirectory) => _links[relative] = toDirectory;

    /// <summary>Records where an item reached through a link is read from.</summary>
    public void ReadFrom(string relative, string path) => _readPaths[relative] = path;

    /// <summary>The path an item's content is read from, when it was reached through a link.</summary>
    public string? ReadPath(string relative) => _readPaths.GetValueOrDefault(relative);

    /// <summary>Whether the path is a followed link to a file.</summary>
    public bool IsFileLink(string relative) => _links.TryGetValue(relative, out var toDirectory) && !toDirectory;

    /// <summary>Whether the path is a followed link, or lies below a followed link to a folder.</summary>
    public bool Through(string relative)
    {
        if (_links.Count == 0) return false;
        if (_links.ContainsKey(relative)) return true;
        for (var slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
        {
            if (_links.TryGetValue(relative[..slash], out var toDirectory) && toDirectory) return true;
        }
        return false;
    }
}
