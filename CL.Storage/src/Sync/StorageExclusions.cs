using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>
/// What one side's listing left out, kept small: folders left out with their contents are kept as prefixes, items
/// left out by a path rule (the same on both sides) only mark their folder, and only items left out for what they
/// are on this side (a link, an item the provider marks hidden) are kept one by one. Paths compare ignoring case,
/// which only ever keeps more.
/// </summary>
internal sealed class StorageExclusions
{
    private readonly HashSet<string> _pruned = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _items = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _keeping = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Gets how many paths are kept, for the item limit.</summary>
    public int Count => _pruned.Count + _items.Count;

    /// <summary>Leaves out a folder (or a link to one) and everything below it.</summary>
    public void Prune(string path)
    {
        _pruned.Add(path);
        Keep(Parent(path));
    }

    /// <summary>Leaves out one item for what it is on this side, so the other side's copy is left alone too.</summary>
    public void Item(string path)
    {
        _items.Add(path);
        Keep(Parent(path));
    }

    /// <summary>Records that a folder holds an item a path rule left out.</summary>
    public void Holds(string path) => Keep(Parent(path));

    /// <summary>Whether a path lies below a folder that was left out.</summary>
    public bool UnderPruned(string path)
    {
        if (_pruned.Count == 0) return false;
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (_pruned.Contains(path[..slash])) return true;
        }
        return false;
    }

    /// <summary>Whether this side left the path out: itself, or a folder above it.</summary>
    public bool Covers(string path) => _items.Contains(path) || _pruned.Contains(path) || UnderPruned(path);

    /// <summary>Whether a folder (or one below it) holds something that was left out, so it must not be deleted.</summary>
    public bool Keeps(string folder) => _keeping.Contains(folder);

    private void Keep(string folder)
    {
        while (_keeping.Add(folder) && folder.Length > 0)
            folder = Parent(folder);
    }

    internal static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}
